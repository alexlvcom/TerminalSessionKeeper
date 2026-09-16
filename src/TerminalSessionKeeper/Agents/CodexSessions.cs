using Microsoft.Data.Sqlite;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Agents;

public sealed record CodexThread(string Id, string? Cwd, string? Name, string? Title, long RecencyMs);

/// <summary>
/// Resolves the codex thread a tab is running, from codex's own SQLite state.
///
/// <c>state_5.sqlite</c> lists the threads that can actually be resumed; <c>logs_2.sqlite</c>
/// says which one was active most recently, because codex stops bumping rollout mtimes once a
/// thread is resumed.
///
/// Everything is read once per snapshot and answered from memory afterwards — a window with ten
/// codex tabs must not mean ten passes over the log table.
/// </summary>
public sealed class CodexSessions
{
    private readonly ILog _log;
    private readonly string _codexHome;
    private readonly Lazy<State> _state;

    private sealed record State(
        IReadOnlyList<CodexThread> Threads,
        IReadOnlyDictionary<string, int> LogRank);

    public CodexSessions(ILog log, string? codexHome = null)
    {
        _log = log;
        _codexHome = codexHome ?? Path.Combine(AgentPaths.UserHome, ".codex");
        _state = new Lazy<State>(Load);
    }

    /// <summary>The most recently active resumable thread rooted at <paramref name="cwd"/>.</summary>
    public CodexThread? ThreadFor(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var state = _state.Value;
        var target = AgentPaths.NormalizeDirectory(cwd);

        var candidates = state.Threads
            .Where(thread => AgentPaths.NormalizeDirectory(thread.Cwd) == target)
            .ToList();

        if (candidates.Count == 0) return null;

        // The log ordering is the real answer; the thread table's own recency columns are the
        // fallback for when the log database could not be read.
        return candidates
            .OrderBy(thread => state.LogRank.TryGetValue(thread.Id, out var rank) ? rank : int.MaxValue)
            .ThenByDescending(thread => thread.RecencyMs)
            .First();
    }

    /// <summary>
    /// A tab-sized title. <c>name</c> is what the user renamed the thread to; <c>title</c> is
    /// codex's own, and it holds the entire first user message — preamble, pasted context and
    /// all — so it has to be cut down before it can go on a tab.
    /// </summary>
    public static string? DisplayTitle(CodexThread? thread)
    {
        if (thread is null) return null;
        if (!string.IsNullOrWhiteSpace(thread.Name)) return thread.Name.Trim();
        return CleanTitle(thread.Title);
    }

    public static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var text = title;

        const string marker = "## My request:";
        var markerAt = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt >= 0) text = text[(markerAt + marker.Length)..];

        foreach (var rawLine in text.Split('\n'))
        {
            var line = string.Join(' ', rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            // Skip headings, bullets and fences: the first line with actual prose is the one
            // that means anything on a tab.
            if (line.Length == 0 || line[0] is '#' or '-' or '`') continue;

            return line.Length <= 60 ? line.TrimEnd() : line[..57].TrimEnd() + "...";
        }

        return null;
    }

    private State Load()
    {
        var empty = new State(Array.Empty<CodexThread>(), new Dictionary<string, int>(StringComparer.Ordinal));

        if (!Directory.Exists(_codexHome)) return empty;

        try
        {
            using var workspace = new SqliteWorkspace(_log);

            var statePath = workspace.CopyOf(_codexHome, "state_5.sqlite");
            if (statePath is null) return empty;

            var threads = ReadThreads(statePath);
            if (threads.Count == 0) return empty;

            var logRank = new Dictionary<string, int>(StringComparer.Ordinal);
            var logsPath = workspace.CopyOf(_codexHome, "logs_2.sqlite");
            if (logsPath is not null) logRank = ReadLogRank(logsPath);

            _log.Debug($"Codex state: {threads.Count} thread(s), {logRank.Count} ranked by activity.");
            return new State(threads, logRank);
        }
        catch (Exception ex) when (SqliteWorkspace.IsEngineUnavailable(ex))
        {
            // Codex support is one column of one kind of tab. Losing the SQLite engine has to
            // cost exactly that and nothing else — never the whole snapshot.
            _log.Warn("The SQLite engine is unavailable; codex tabs will restore without a " +
                      $"resume command. {ex.GetBaseException().Message}");
            return empty;
        }
    }

    private IReadOnlyList<CodexThread> ReadThreads(string path)
    {
        var threads = new List<CodexThread>();

        // Older codex builds have no recency columns, so the richer query is tried first and the
        // plain one stands in when the schema does not have them.
        foreach (var sql in new[]
                 {
                     "SELECT id, cwd, name, title, MAX(COALESCE(recency_at_ms, 0), COALESCE(updated_at_ms, 0)) FROM threads",
                     "SELECT id, cwd, NULL, title, 0 FROM threads",
                 })
        {
            threads.Clear();

            try
            {
                using var connection = SqliteWorkspace.OpenReadOnly(path);
                using var command = connection.CreateCommand();
                command.CommandText = sql;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(id)) continue;

                    threads.Add(new CodexThread(
                        id,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
                }

                return threads;
            }
            catch (SqliteException ex)
            {
                _log.Debug($"Codex thread query failed, trying the next shape. {ex.Message}");
            }
        }

        _log.Warn("Could not read codex threads; codex tabs will restore without a resume command.");
        return Array.Empty<CodexThread>();
    }

    private Dictionary<string, int> ReadLogRank(string path)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var connection = SqliteWorkspace.OpenReadOnly(path);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT thread_id FROM logs " +
                "WHERE thread_id IS NOT NULL AND thread_id != '' " +
                "ORDER BY ts DESC, id DESC LIMIT 5000";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var id = reader.GetString(0);

                // First sighting wins: the query is newest-first, so the lowest rank is the most
                // recently active thread.
                if (!rank.ContainsKey(id)) rank[id] = rank.Count;
            }
        }
        catch (SqliteException ex)
        {
            _log.Debug($"Codex activity log unreadable; falling back to the recency columns. {ex.Message}");
        }

        return rank;
    }
}
