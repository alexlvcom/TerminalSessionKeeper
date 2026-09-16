# Terminal Session Keeper

Saves your open Windows Terminal tabs — including the live agent conversations
inside them — and puts them back after a restart.

Windows Update does not care that you had twelve tabs open, each in a different
project with a different agent session. This does.

It is a tray app. There is no window, no shell profile to edit, nothing to add to
your dotfiles, and no scheduled task.

---

## Download

Grab the latest **TerminalSessionKeeper.exe** from the [Releases page](https://github.com/alexlvcom/TerminalSessionKeeper/releases/latest) — or directly:

**https://github.com/alexlvcom/TerminalSessionKeeper/releases/latest/download/TerminalSessionKeeper.exe**

It's a single, self-contained executable — **no .NET runtime install needed**. Just download and
double-click. It runs without administrator privileges and never asks for elevation.

Open **Settings…** from its tray menu, tick **Start with Windows**, and you are done.

### First run: Windows SmartScreen

The exe is **unsigned** (no code-signing certificate for a free hobby tool), so Windows may show
**"Windows protected your PC."** This is *not* a virus warning — it only means the file is new and
hasn't built up download reputation yet. Click **More info → Run anyway**.

Prefer to be sure? The source is right here — [build it yourself](#build).

### Verify your download (optional)

Each release lists the SHA-256 of the exe. To check the file you downloaded matches:

```powershell
Get-FileHash .\TerminalSessionKeeper.exe -Algorithm SHA256
```

Compare the output against the hash shown on that version's
[release page](https://github.com/alexlvcom/TerminalSessionKeeper/releases/latest).

### Requirements

Windows Terminal 1.18 or newer. WSL tabs additionally need `python3` inside the distro — that is
all, and it is only needed to read the WSL side; Windows tabs work without WSL at all.

Everything it keeps lives in `%LocalAppData%\TerminalSessionKeeper`: `Snapshots\`,
`settings.json`, `overrides.json` and `Logs\`. Uninstalling is deleting the exe and that folder.

---

## Supported agents

| Agent | Resumed with |
|---|---|
| Claude Code | `claude --resume <id>` |
| Codex | `codex resume <id>` |
| Antigravity | `agy --conversation <id>` |
| Junie | `junie --resume --session-id=<id>` |

All four work whether the agent is running on the Windows side of a tab or inside
WSL. A tab running something else — or nothing — still comes back in its own
directory; it just has no command waiting at the prompt.

---

## The tray menu

Four things and an exit. Everything else lives in one of the two windows.

| | |
|---|---|
| **Snapshot now** | Save the tabs that are open right now |
| **Restore last snapshot** | Put them back, in a new window |
| **Snapshots…** | Every saved snapshot, what is in it, and restore or preview any of them |
| **Settings…** | Everything below |
| **Exit** | |

### Settings

| | |
|---|---|
| **Take a snapshot automatically** | And how often — 10 minutes by default, 1 minute to 24 hours |
| **Keep the newest N snapshots** | 20 by default; they are a few KB each |
| **Put the tabs back automatically after a reboot** | Guarded — see below |
| **Run each resume command** | Off by default — see below |
| **Keep saved tab titles** | Freeze restored titles instead of letting the shell rename them |
| **Remember tab colours** | See *Titles and colours* |
| **Show a notification** | After a snapshot or a restore |
| **Start with Windows** | `HKCU\...\CurrentVersion\Run`. No elevation, no scheduled task |

---

## What a restored tab looks like

Each tab comes back in its own directory with its resume command **waiting at the
prompt**:

```
[11:04] ~/source/my-mcp (master●) $ claude --resume 043c02a0-a42b-4441-84d2-754721971e63
```

Press Enter in the tabs you actually want. Nothing starts on its own — twelve
agents and their MCP servers booting at once, right after a reboot, is not what
you want. **Run resume commands automatically** starts them all, if you disagree.

The command is typed into the tab's console input queue, so the prompt receives it
exactly as if you had typed it. That works the same for a PowerShell tab — where
PSReadLine cannot be pre-filled through its own API from outside its prompt — and
for a WSL tab, where the keystrokes cross the ConPTY into the Linux pty and land
on the zsh command line. This is why there is no shell-side hook to install.

A WSL tab also gets one short `cd` typed and run first. That is deliberate: a
`.zshrc` that restores your last working directory runs *after* `wsl --cd` has
applied, and without it every restored tab lands in the same folder.

---

## Titles and colours

Titles come back as they were, set through the tab's console rather than through
the shell. Going through the shell never survived — a title command has to be
submitted, and the prompt that follows is exactly when a themed shell renames the
tab after its folder again.

A restored tab keeps its saved title until something changes it: resuming the
session, or running any other command. That is normal shell behaviour and it is
why agent tabs end up correctly named again on their own. **Keep saved
tab titles** freezes them for good instead, at the cost of the shell and the
agents never updating them again.

**Colours are kept too.** No Windows Terminal API exposes a tab colour and nothing
persists one, so the window is asked to render itself and the colour is read from
the tab's own pixels. That works while the terminal is behind other windows — as
it usually is when the snapshot timer fires — because the window draws itself
rather than being grabbed off the screen.

A *minimized* window has nothing to draw, and its colours carry forward from the
previous snapshot instead. Anything uncertain is recorded as no colour rather
than as a guess, since a wrong colour would be reapplied on every restore. Turn
the whole thing off with **Remember tab colours** if you would rather it never
looked.

To pin a colour by hand regardless, put it in
`%LocalAppData%\TerminalSessionKeeper\overrides.json` — an entry here always wins:

```json
[
  {
    "cwd": "/home/you/source/my-api",
    "title": "ABC-1022",
    "tabColor": "#7B3FF2"
  }
]
```

`cwd` must match the snapshot exactly. Use this for any title the matcher could
not work out — the **Snapshots** window lists those at the bottom. A title set here
is pinned: neither the matcher nor the shell overwrites it.

Snapshots are plain JSON and meant to be edited; fixing a wrong title is a
one-line change.

---

## How it finds your sessions

Tabs are identified by `WT_SESSION`, the id Windows Terminal puts in every pane's
environment. That links a WSL shell to its Windows tab exactly. Process start
times cannot stand in for it: WSL's clock drifts from the host's across hibernate,
by 15 hours and more in practice, and not by a constant offset.

Session ids come from the running processes themselves — the open
`/tmp/claude-*/<project>/<session>/` handle for Claude inside WSL, the codex
SQLite state for Codex — so two tabs in the same folder still get their own
conversation.

Titles are matched to tabs by content, not by position: dragging a tab changes
where it sits but not what it is. In order: the shell's own untouched title, then
the agent's session title, then the **git branch** — which is how a hand-renamed
tab like `ABC-1022` finds its way back to `my-api`, whose branch is
`ABC-1022-JWT-Alongside-Session-Auth` — then the folder name. Every tab has exactly
one title, so if one tab and one title are left over at the end, that pairing is
forced and gets made. More than one left over is reported rather than guessed at.

---

## Restoring after a reboot

**Restore after reboot** only fires when it is safe to: the first run after a
reboot *and* no Windows Terminal window currently has tabs. Restoring next to a
window that is still open puts **both** into the next snapshot, and the restore
after that would rebuild twenty tabs instead of ten. Each snapshot records the
machine's boot time so the app can tell "the session these tabs belonged to is
gone" from "the user is still sitting in it".

Restoring by hand while a window is open is allowed — it asks first, and says
exactly what will happen.

---

## If something looks wrong

| Symptom | Cause |
|---|---|
| No WSL tabs in the snapshot | `python3` is missing from the distro. Windows tabs are unaffected |
| A tab has no resume command | No live session was found; the **Snapshots** window says which agent and which folder |
| A restored tab has no command at its prompt | Its shell took longer than expected to start. The log names the tab |
| Some titles are missing | UI Automation could not reach the window. Titles from an earlier snapshot of the same tab are reused |
| A title could not be attributed | Listed under "could not be attributed" — pin it in `overrides.json` |
| Tab colours are missing | The window was minimized when the snapshot ran. They come back on the next snapshot taken with it open, or pin them in `overrides.json` |

The **Snapshots** window prints exactly what was captured, and **Preview restore**
prints exactly what would be launched. Between those two, most problems are
visible without digging. `Logs\terminalsessionkeeper.log` has the rest.

---

## Build

Needs the .NET 8 SDK and nothing else.

```powershell
dotnet test TerminalSessionKeeper.sln -c Release

dotnet publish src\TerminalSessionKeeper\TerminalSessionKeeper.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

That produces the same single `publish\TerminalSessionKeeper.exe` the releases ship.
