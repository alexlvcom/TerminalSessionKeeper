using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Agents;

public sealed record AgyConversation(string Id, string? Title, IReadOnlyList<string> Workspaces);

/// <summary>
/// Resolves the Antigravity CLI (<c>agy</c>) conversation a tab is running.
///
/// <c>conversation_summaries.db</c> in the CLI's home lists every conversation with the
/// workspace it belongs to, so a tab is matched on its working directory the same way codex is.
/// Killed conversations are skipped — they cannot be resumed — and the rows are ordered by last
/// user input so the newest conversation in a directory wins.
/// </summary>
public sealed class AgySessions
{
    private const string DatabaseName = "conversation_summaries.db";

    private readonly ILog _log;
    private readonly IReadOnlyList<string> _homes;
    private readonly Lazy<IReadOnlyList<AgyConversation>> _conversations;

    public AgySessions(ILog log, IEnumerable<string>? homes = null)
    {
        _log = log;
        _homes = (homes ?? AgentPaths.AgyHomes()).ToList();
        _conversations = new Lazy<IReadOnlyList<AgyConversation>>(Load);
    }

    /// <summary>The most recently active resumable conversation rooted at <paramref name="cwd"/>.</summary>
    public AgyConversation? ConversationFor(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var target = AgentPaths.NormalizeDirectory(cwd);

        // The rows arrive newest-first, so the first workspace match is the answer.
        return _conversations.Value.FirstOrDefault(conversation =>
            conversation.Workspaces.Any(workspace => workspace == target));
    }

    /// <summary>
    /// <c>workspace_uris</c> is a JSON array of URIs in every build seen so far, but older ones
    /// wrote a single bare path, so both shapes are accepted rather than assumed.
    /// </summary>
    public static IReadOnlyList<string> ParseWorkspaces(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var text = raw.Trim();

        // A leading bracket means it was meant to be a list, so a parse failure is a broken list
        // — not a directory that happens to start with one.
        if (text.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

                return document.RootElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => AgentPaths.NormalizeDirectory(element.GetString()))
                    .Where(path => path.Length > 0)
                    .ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        var single = AgentPaths.NormalizeDirectory(text);
        return single.Length > 0 ? new[] { single } : Array.Empty<string>();
    }

    private IReadOnlyList<AgyConversation> Load()
    {
        foreach (var home in _homes)
        {
            if (!Directory.Exists(home)) continue;

            var conversations = LoadFrom(home);
            if (conversations.Count > 0) return conversations;
        }

        return Array.Empty<AgyConversation>();
    }

    private IReadOnlyList<AgyConversation> LoadFrom(string home)
    {
        try
        {
            using var workspace = new SqliteWorkspace(_log);

            var path = workspace.CopyOf(home, DatabaseName);
            if (path is null) return Array.Empty<AgyConversation>();

            using var connection = SqliteWorkspace.OpenReadOnly(path);
            using var command = connection.CreateCommand();

            // Ordering in SQL avoids parsing the datetime columns at all: their encoding differs
            // between builds, but within one database it is consistent, and both ISO-8601 text
            // and an epoch number sort correctly on their own terms.
            command.CommandText =
                "SELECT conversation_id, title, preview, workspace_uris FROM conversation_summaries " +
                "WHERE conversation_id IS NOT NULL AND conversation_id != '' " +
                "AND (killed IS NULL OR killed = 0 OR killed = 'false') " +
                "ORDER BY last_user_input_time DESC, last_modified_time DESC";

            var conversations = new List<AgyConversation>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                var preview = reader.IsDBNull(2) ? null : reader.GetString(2);
                var workspaces = ParseWorkspaces(reader.IsDBNull(3) ? null : reader.GetString(3));

                if (workspaces.Count == 0) continue;

                conversations.Add(new AgyConversation(
                    id,
                    string.IsNullOrWhiteSpace(title) ? Shorten(preview) : Shorten(title),
                    workspaces));
            }

            _log.Debug($"Antigravity state at {home}: {conversations.Count} resumable conversation(s).");
            return conversations;
        }
        catch (SqliteException ex)
        {
            // A schema this build does not know: no conversations rather than no snapshot.
            _log.Debug($"Could not read the Antigravity conversation summaries. {ex.Message}");
            return Array.Empty<AgyConversation>();
        }
        catch (Exception ex) when (SqliteWorkspace.IsEngineUnavailable(ex))
        {
            _log.Warn("The SQLite engine is unavailable; agy tabs will restore without a resume " +
                      $"command. {ex.GetBaseException().Message}");
            return Array.Empty<AgyConversation>();
        }
    }

    /// <summary>Titles are free text and a preview is a whole message; a tab needs neither in full.</summary>
    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = string.Join(' ', rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0 || line[0] is '#' or '-' or '`') continue;

            return line.Length <= 60 ? line : line[..57].TrimEnd() + "...";
        }

        return null;
    }
}
