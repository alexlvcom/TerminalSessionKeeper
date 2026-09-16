#!/usr/bin/env python3
"""Probe this WSL distro for Windows Terminal tabs and their live agent sessions.

Carried inside TerminalSessionKeeper.exe and staged into the distro on each
snapshot; nothing is installed into the user's dotfiles. It has to run inside
WSL because /proc and the Linux-side agent state are only reachable from there.

Emits a JSON array on stdout, one entry per interactive shell that Windows
Terminal started, identified by the WT_SESSION marker WT injects into every
pane's environment. Each entry carries enough to rebuild the tab and resume the
exact agent conversation that was running in it.
"""

import json
import os
import re
import sqlite3
import sys

HOME = os.path.expanduser("~")
AGENTS = ("claude", "codex", "junie", "agy")
CODEX_HOME = os.path.join(HOME, ".codex")

# Antigravity's CLI home. The directory name differs between installs, so both the
# newer and the older layout are tried.
AGY_HOMES = (
    os.path.join(HOME, ".gemini", "antigravity-cli"),
    os.path.join(HOME, ".gemini", "antigravity"),
    os.path.join(HOME, ".antigravity"),
)

# /tmp/claude-<uid>/<project-slug>/<session-uuid>/... held open by a live claude
CLAUDE_FD_RE = re.compile(
    r"/claude-\d+/[^/]+/"
    r"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:/|$)"
)


def _read(path, binary=False):
    try:
        with open(path, "rb" if binary else "r") as handle:
            return handle.read()
    except OSError:
        return None


def proc_environ(pid):
    raw = _read("/proc/%d/environ" % pid, binary=True)
    if not raw:
        return {}
    env = {}
    for chunk in raw.split(b"\0"):
        if b"=" in chunk:
            key, _, value = chunk.partition(b"=")
            env[key.decode("utf-8", "replace")] = value.decode("utf-8", "replace")
    return env


def proc_cmdline(pid):
    raw = _read("/proc/%d/cmdline" % pid, binary=True)
    if not raw:
        return []
    return [p.decode("utf-8", "replace") for p in raw.split(b"\0") if p]


def proc_cwd(pid):
    try:
        return os.readlink("/proc/%d/cwd" % pid)
    except OSError:
        return None


def proc_stat(pid):
    """Return (ppid, comm).

    Parsed off the last ')' so a process whose name contains spaces or
    parentheses does not break the field split.
    """
    raw = _read("/proc/%d/stat" % pid)
    if not raw:
        return None, None
    close = raw.rfind(")")
    if close < 0:
        return None, None
    comm = raw[raw.find("(") + 1:close]
    fields = raw[close + 2:].split()
    if len(fields) < 2:
        return None, comm
    return int(fields[1]), comm


def all_pids():
    return sorted(int(name) for name in os.listdir("/proc") if name.isdigit())


def agent_kind(comm, argv):
    """Classify a process as one of the known agents, or None."""
    name = os.path.basename(argv[0]) if argv else (comm or "")
    for agent in AGENTS:
        if name == agent or (comm or "") == agent:
            return agent
    # Agents launched through node keep the script path in argv
    for arg in argv[1:3]:
        if os.path.basename(arg) in AGENTS:
            return os.path.basename(arg)
    return None


def claude_project_slug(cwd):
    return re.sub(r"[^A-Za-z0-9]", "-", cwd or "")


def claude_session_fallback(cwd):
    """Newest transcript for this directory, used if the live handle is gone."""
    if not cwd:
        return None
    project = os.path.join(HOME, ".claude", "projects", claude_project_slug(cwd))
    try:
        files = [f for f in os.listdir(project) if f.endswith(".jsonl")]
    except OSError:
        return None
    if not files:
        return None
    files.sort(key=lambda f: os.path.getmtime(os.path.join(project, f)), reverse=True)
    return os.path.splitext(files[0])[0]


def claude_session_id(pid, cwd):
    """Exact session id of a RUNNING claude, from its open /tmp/claude-* handle.

    Exact, so two tabs sitting in the same directory still get their own
    session -- which a newest-first directory scan could never guarantee.
    """
    fd_dir = "/proc/%d/fd" % pid
    try:
        entries = os.listdir(fd_dir)
    except OSError:
        entries = []
    for entry in entries:
        try:
            target = os.readlink(os.path.join(fd_dir, entry))
        except OSError:
            continue
        match = CLAUDE_FD_RE.search(target)
        if match:
            return match.group(1)
    return claude_session_fallback(cwd)


def claude_session_title(session_id):
    """Claude writes ai-title records into its transcript; the last one wins."""
    if not session_id:
        return None
    projects = os.path.join(HOME, ".claude", "projects")
    try:
        slugs = os.listdir(projects)
    except OSError:
        return None
    for slug in slugs:
        path = os.path.join(projects, slug, session_id + ".jsonl")
        if not os.path.isfile(path):
            continue
        title = None
        try:
            with open(path) as handle:
                for line in handle:
                    try:
                        record = json.loads(line)
                    except ValueError:
                        continue
                    if record.get("type") == "ai-title" and record.get("aiTitle"):
                        title = record["aiTitle"]
        except OSError:
            return None
        return title
    return None


def _codex_db(name):
    path = os.path.join(CODEX_HOME, name)
    if not os.path.isfile(path):
        return None
    try:
        return sqlite3.connect("file:%s?mode=ro" % path, uri=True, timeout=2)
    except sqlite3.Error:
        return None


def _normalize_dir(value):
    """Reduce a recorded working directory to something comparable.

    Windows tools record the extended-length form ("\\\\?\\C:\\Users\\...") and
    sometimes a file:// URI, and Windows paths are case-insensitive, so a plain
    string compare misses. Linux paths stay case-sensitive.
    """
    if not value:
        return ""
    value = value.strip().replace("\\", "/")
    if value.lower().startswith("file://"):
        value = value[7:]
        # "file:///home/me" keeps its leading slash; "file:///C:/x" does not.
        if len(value) > 2 and value[0] == "/" and value[1].isalpha() and value[2] == ":":
            value = value[1:]
    if value.startswith("//?/"):
        value = value[4:]
    value = value.rstrip("/")
    if len(value) > 1 and value[1] == ":":
        value = value.lower()
    return value


def same_directory(left, right):
    return _normalize_dir(left) == _normalize_dir(right)


# Kept under its original name for the codex reader below.
_same_directory = same_directory


def codex_session_id(cwd):
    """Most recently active codex thread rooted at this directory.

    Codex stops bumping rollout mtimes on resume, so thread liveness is read from
    logs_2.sqlite; state_5.sqlite says which threads are actually resumable.

    logs_2.sqlite is not always readable -- a live codex keeps it in WAL mode --
    so the thread table's own recency columns are the fallback ranking.
    """
    if not cwd:
        return None
    state = _codex_db("state_5.sqlite")
    if state is None:
        return None

    try:
        rows = state.execute(
            "SELECT id, cwd, COALESCE(recency_at_ms, 0), COALESCE(updated_at_ms, 0) FROM threads"
        ).fetchall()
    except sqlite3.Error:
        # Older schemas without the recency columns
        try:
            rows = [(r[0], r[1], 0, 0) for r in state.execute("SELECT id, cwd FROM threads")]
        except sqlite3.Error:
            state.close()
            return None
    finally:
        state.close()

    candidates = {row[0]: max(row[2], row[3]) for row in rows if _same_directory(row[1], cwd)}
    if not candidates:
        return None

    logs = _codex_db("logs_2.sqlite")
    if logs is not None:
        try:
            query = (
                "SELECT thread_id FROM logs "
                "WHERE thread_id IS NOT NULL AND thread_id != '' "
                "ORDER BY ts DESC, id DESC LIMIT 5000"
            )
            for (thread_id,) in logs.execute(query):
                if thread_id in candidates:
                    return thread_id
        except sqlite3.Error:
            pass
        finally:
            logs.close()

    return max(candidates, key=lambda key: candidates[key])


def _clean_codex_title(title):
    """Reduce a codex thread title to something that fits on a tab.

    Codex stores the entire first user message here, preamble and all, which can
    run to many lines of pasted context.
    """
    if not title:
        return None
    if "## My request:" in title:
        title = title.split("## My request:", 1)[1]
    for line in title.splitlines():
        line = " ".join(line.split())
        if not line or line.startswith(("#", "-", "`")):
            continue
        return line[:60].rstrip() if len(line) <= 60 else line[:57].rstrip() + "..."
    return None


def codex_session_title(session_id):
    if not session_id:
        return None
    state = _codex_db("state_5.sqlite")
    if state is None:
        return None
    try:
        # "name" is what the user renamed the thread to, if anything; "title" is
        # codex's own derived text, which is the whole first message.
        try:
            row = state.execute("SELECT name, title FROM threads WHERE id=?", (session_id,)).fetchone()
        except sqlite3.Error:
            row = state.execute("SELECT NULL, title FROM threads WHERE id=?", (session_id,)).fetchone()
        if not row:
            return None
        if row[0] and row[0].strip():
            return row[0].strip()
        return _clean_codex_title(row[1])
    except sqlite3.Error:
        return None
    finally:
        state.close()


def junie_session(cwd):
    """Newest Junie CLI session rooted at this directory, as (id, task name).

    Note that ~/.junie/instances next door is something else entirely: those
    files belong to the JetBrains IDE plugin and hold an RPC port and a live
    pid, not a resumable CLI session.
    """
    index = os.path.join(HOME, ".junie", "sessions", "index.jsonl")
    if not os.path.isfile(index):
        return None, None
    best = None
    try:
        with open(index) as handle:
            for line in handle:
                try:
                    record = json.loads(line)
                except ValueError:
                    continue
                if not same_directory(record.get("projectDir"), cwd):
                    continue
                updated = record.get("updatedAt", 0)
                if record.get("sessionId") and (best is None or updated > best[0]):
                    best = (updated, record["sessionId"], record.get("taskName"))
    except OSError:
        return None, None
    return (best[1], best[2]) if best else (None, None)


def _agy_db():
    for home in AGY_HOMES:
        path = os.path.join(home, "conversation_summaries.db")
        if not os.path.isfile(path):
            continue
        try:
            return sqlite3.connect("file:%s?mode=ro" % path, uri=True, timeout=2)
        except sqlite3.Error:
            continue
    return None


def _agy_workspaces(raw):
    """workspace_uris is a JSON array of URIs, but older builds wrote a bare path."""
    if not raw:
        return []
    text = raw.strip()
    if text.startswith("["):
        try:
            parsed = json.loads(text)
        except ValueError:
            parsed = None
        if isinstance(parsed, list):
            return [_normalize_dir(v) for v in parsed if isinstance(v, str)]
    return [_normalize_dir(text)]


def agy_session(cwd):
    """Most recently used resumable Antigravity conversation for this directory.

    Killed conversations are skipped -- they cannot be resumed. Ordering happens
    in SQL so the datetime columns never have to be parsed: their encoding
    differs between builds but is consistent within one database.
    """
    if not cwd:
        return None, None
    db = _agy_db()
    if db is None:
        return None, None
    target = _normalize_dir(cwd)
    try:
        rows = db.execute(
            "SELECT conversation_id, title, preview, workspace_uris "
            "FROM conversation_summaries "
            "WHERE conversation_id IS NOT NULL AND conversation_id != '' "
            "AND (killed IS NULL OR killed = 0 OR killed = 'false') "
            "ORDER BY last_user_input_time DESC, last_modified_time DESC"
        )
        for conversation_id, title, preview, workspaces in rows:
            if target in _agy_workspaces(workspaces):
                return conversation_id, _shorten(title or preview)
    except sqlite3.Error:
        return None, None
    finally:
        db.close()
    return None, None


def _shorten(text):
    if not text:
        return None
    for line in text.splitlines():
        line = " ".join(line.split())
        if not line or line.startswith(("#", "-", "`")):
            continue
        return line[:60] if len(line) <= 60 else line[:57].rstrip() + "..."
    return None


def git_branch(cwd):
    """Current branch for a directory, read straight from .git/HEAD.

    Tabs are very often named after the ticket in the branch name, so this is
    one of the strongest signals for matching a hand-renamed tab back to the
    directory it belongs to.
    """
    if not cwd:
        return None
    path = cwd
    for _ in range(40):
        marker = os.path.join(path, ".git")
        head = None
        if os.path.isdir(marker):
            head = os.path.join(marker, "HEAD")
        elif os.path.isfile(marker):
            # Worktree or submodule: the file points at the real git dir.
            pointer = _read(marker)
            if not pointer or not pointer.startswith("gitdir:"):
                return None
            target = pointer.split(":", 1)[1].strip()
            if not os.path.isabs(target):
                target = os.path.join(path, target)
            head = os.path.join(target, "HEAD")
        else:
            parent = os.path.dirname(path)
            if parent == path:
                return None
            path = parent
            continue

        ref = _read(head)
        if not ref:
            return None
        ref = ref.strip()
        if ref.startswith("ref:") and "/" in ref:
            return ref.split("/", 2)[-1]
        return None  # detached HEAD
    return None


def resume_command(agent, session_id):
    if not session_id:
        return None
    if agent == "claude":
        return "claude --resume %s" % session_id
    if agent == "codex":
        return "codex resume %s" % session_id
    if agent == "junie":
        return "junie --resume --session-id=%s" % session_id
    if agent == "agy":
        # agy has --continue for "the most recent", but a tab has to name its own
        # conversation or two tabs in one project would both reopen the same one.
        return "agy --conversation %s" % session_id
    return None


def find_agent(shell_pid, children, meta):
    """Breadth-first walk of a shell's descendants for the agent it is running."""
    queue = list(children.get(shell_pid, []))
    while queue:
        candidate = queue.pop(0)
        argv = proc_cmdline(candidate)
        kind = agent_kind(meta.get(candidate, (None, None))[1], argv)
        if kind:
            return kind, candidate, argv
        queue.extend(children.get(candidate, []))
    return None, None, None


def main():
    pids = all_pids()
    children = {}
    meta = {}
    for pid in pids:
        parent, comm = proc_stat(pid)
        if parent is None:
            continue
        meta[pid] = (parent, comm)
        children.setdefault(parent, []).append(pid)

    # Candidate shells: anything WT started. Helper shells spawned *inside* a
    # tab (MCP servers, tool calls) inherit WT_SESSION too, so they show up here
    # as well and get filtered out below.
    candidates = {}
    for pid in pids:
        comm = meta.get(pid, (None, None))[1]
        if comm not in ("zsh", "bash", "sh", "fish"):
            continue
        env = proc_environ(pid)
        wt_session = env.get("WT_SESSION")
        if not wt_session:
            # Not a Windows Terminal pane (docker helper, IDE terminal, ...)
            continue
        candidates[pid] = (wt_session, env, comm)

    def nested_in_candidate(pid, wt_session):
        """True if an ancestor is already a candidate shell for the same pane."""
        seen = set()
        cursor = meta.get(pid, (None, None))[0]
        while cursor and cursor not in seen:
            seen.add(cursor)
            if cursor in candidates and candidates[cursor][0] == wt_session:
                return True
            cursor = meta.get(cursor, (None, None))[0]
        return False

    tabs = []
    for pid in sorted(candidates):
        wt_session, env, comm = candidates[pid]
        # Keep only the outermost shell per WT_SESSION.
        if nested_in_candidate(pid, wt_session):
            continue

        agent, agent_pid, agent_argv = find_agent(pid, children, meta)

        shell_cwd = proc_cwd(pid)
        cwd = (proc_cwd(agent_pid) if agent_pid else shell_cwd) or shell_cwd

        session_id = None
        session_title = None
        if agent == "claude":
            session_id = claude_session_id(agent_pid, cwd)
            session_title = claude_session_title(session_id)
        elif agent == "codex":
            session_id = codex_session_id(cwd)
            session_title = codex_session_title(session_id)
        elif agent == "junie":
            session_id, session_title = junie_session(cwd)
        elif agent == "agy":
            session_id, session_title = agy_session(cwd)

        tabs.append({
            "wtSession": wt_session,
            "wtProfileId": env.get("WT_PROFILE_ID"),
            "shellPid": pid,
            "shell": comm,
            "cwd": cwd,
            "gitBranch": git_branch(cwd),
            "agent": agent,
            "agentPid": agent_pid,
            "sessionId": session_id,
            "sessionTitle": session_title,
            "resumeCommand": resume_command(agent, session_id),
        })

    json.dump(tabs, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
