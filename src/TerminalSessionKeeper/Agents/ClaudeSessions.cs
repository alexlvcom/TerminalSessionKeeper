using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalSessionKeeper.Agents;

/// <summary>
/// Finds the session a Windows-side <c>claude.exe</c> is running, and the title it gave itself.
///
/// The WSL side can do better — a live claude there holds an open handle to its own session
/// directory, which is exact — but on Windows the newest per-session scratch directory under
/// %TEMP% is the best available signal, so ids already handed to an earlier tab are tracked and
/// skipped rather than letting two tabs in one directory claim the same session.
/// </summary>
public sealed class ClaudeSessions
{
    private static readonly Regex SessionIdPattern =
        new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.CultureInvariant);

    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claude's project slug: the working directory with every non-alphanumeric
    /// character replaced by a dash.</summary>
    public static string ProjectSlug(string? path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Regex.Replace(path, "[^A-Za-z0-9]", "-");

    public string? SessionIdFor(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var slug = ProjectSlug(cwd);

        var scratch = Path.Combine(Path.GetTempPath(), "claude", slug);
        var fromScratch = NewestUnclaimed(
            () => Directory.EnumerateDirectories(scratch),
            path => new DirectoryInfo(path).LastWriteTimeUtc,
            path => Path.GetFileName(path),
            name => SessionIdPattern.IsMatch(name));

        if (fromScratch is not null)
        {
            _claimed.Add(fromScratch);
            return fromScratch;
        }

        var projects = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", slug);

        var fromTranscript = NewestUnclaimed(
            () => Directory.EnumerateFiles(projects, "*.jsonl"),
            path => new FileInfo(path).LastWriteTimeUtc,
            Path.GetFileNameWithoutExtension,
            _ => true);

        if (fromTranscript is not null) _claimed.Add(fromTranscript);
        return fromTranscript;
    }

    /// <summary>
    /// The title claude gave the conversation. It writes <c>ai-title</c> records into the
    /// transcript as the conversation develops, so the last one is the current title.
    /// </summary>
    public static string? SessionTitle(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var projects = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        if (!Directory.Exists(projects)) return null;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(projects))
            {
                var transcript = Path.Combine(directory, sessionId + ".jsonl");
                if (File.Exists(transcript)) return LastAiTitle(transcript);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? LastAiTitle(string transcript)
    {
        string? title = null;

        try
        {
            // Explicit UTF-8: a title is routinely non-ASCII, and reading it as the ANSI code
            // page is how "✳" becomes "âœ³" in a restored tab.
            foreach (var line in File.ReadLines(transcript, new UTF8Encoding(false)))
            {
                // A transcript runs to megabytes; skip the JSON parse for the lines that
                // cannot possibly be a title record.
                if (!line.Contains("\"ai-title\"", StringComparison.Ordinal)) continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var type) || type.GetString() != "ai-title") continue;
                    if (!root.TryGetProperty("aiTitle", out var value)) continue;

                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) title = text;
                }
                catch (JsonException)
                {
                    // A partially written line at the tail of a live transcript.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return title;
        }

        return title;
    }

    /// <summary>
    /// Newest candidate whose id has not already been handed to another tab. Walking
    /// newest-first and skipping claimed ids is what keeps two tabs in one directory apart.
    /// </summary>
    private string? NewestUnclaimed(
        Func<IEnumerable<string>> enumerate,
        Func<string, DateTime> timestamp,
        Func<string, string> toId,
        Func<string, bool> isValidId)
    {
        try
        {
            return enumerate()
                .Select(path => (Id: toId(path), Path: path))
                .Where(candidate => isValidId(candidate.Id) && !_claimed.Contains(candidate.Id))
                .OrderByDescending(candidate => timestamp(candidate.Path))
                .Select(candidate => candidate.Id)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or DirectoryNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}
