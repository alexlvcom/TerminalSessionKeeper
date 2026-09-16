using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Agents;

public sealed record JunieSession(string Id, string? ProjectDir, string? TaskName, long UpdatedAt);

/// <summary>
/// Resolves the Junie CLI session a tab is running, from <c>~/.junie/sessions/index.jsonl</c>.
///
/// Note that <c>~/.junie/instances</c> next door is not this: those files belong to the
/// JetBrains IDE plugin and describe an RPC port and a live pid, not a resumable CLI session.
/// </summary>
public sealed class JunieSessions
{
    private readonly ILog _log;
    private readonly string _indexPath;
    private readonly Lazy<IReadOnlyList<JunieSession>> _sessions;

    public JunieSessions(ILog log, string? junieHome = null)
    {
        _log = log;
        _indexPath = Path.Combine(
            junieHome ?? Path.Combine(AgentPaths.UserHome, ".junie"), "sessions", "index.jsonl");
        _sessions = new Lazy<IReadOnlyList<JunieSession>>(Load);
    }

    /// <summary>The most recently updated session rooted at <paramref name="cwd"/>.</summary>
    public JunieSession? SessionFor(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var target = AgentPaths.NormalizeDirectory(cwd);

        return _sessions.Value
            .Where(session => AgentPaths.NormalizeDirectory(session.ProjectDir) == target)
            .MaxBy(session => session.UpdatedAt);
    }

    public static IReadOnlyList<JunieSession> Parse(IEnumerable<string> lines)
    {
        var sessions = new List<JunieSession>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (!root.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String) continue;

                var sessionId = id.GetString();
                if (string.IsNullOrWhiteSpace(sessionId)) continue;

                sessions.Add(new JunieSession(
                    sessionId,
                    root.TryGetProperty("projectDir", out var dir) && dir.ValueKind == JsonValueKind.String
                        ? dir.GetString()
                        : null,
                    root.TryGetProperty("taskName", out var task) && task.ValueKind == JsonValueKind.String
                        ? task.GetString()
                        : null,
                    root.TryGetProperty("updatedAt", out var updated) && updated.TryGetInt64(out var value)
                        ? value
                        : 0));
            }
            catch (JsonException)
            {
                // A partially written line at the tail of a live index.
            }
        }

        return sessions;
    }

    private IReadOnlyList<JunieSession> Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return Array.Empty<JunieSession>();

            // Explicit UTF-8: a task name is routinely non-ASCII, and the ANSI code page would
            // turn it into mojibake on the way to a tab title.
            var sessions = Parse(File.ReadLines(_indexPath, new UTF8Encoding(false)));
            _log.Debug($"Junie index: {sessions.Count} session(s).");
            return sessions;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"Could not read the Junie session index. {ex.Message}");
            return Array.Empty<JunieSession>();
        }
    }
}
