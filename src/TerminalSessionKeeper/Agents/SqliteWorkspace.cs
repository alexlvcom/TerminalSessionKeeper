using Microsoft.Data.Sqlite;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Agents;

/// <summary>
/// Reads an agent's SQLite state safely while the agent is running.
///
/// Everything is read from a scratch copy, journals included. That is what makes it safe: a
/// live agent keeps its databases in WAL mode, and opening one in place either fails outright
/// or drags a hot journal along with it. The originals are never touched.
/// </summary>
internal sealed class SqliteWorkspace : IDisposable
{
    /// <summary>Beyond this a database is not worth copying; the caller falls back to whatever
    /// ranking it can manage without it.</summary>
    private const long MaxCopyBytes = 256L * 1024 * 1024;

    private readonly ILog _log;
    private readonly string _directory;

    public SqliteWorkspace(ILog log)
    {
        _log = log;
        _directory = Path.Combine(Path.GetTempPath(), "TerminalSessionKeeper",
            "sqlite-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>A private copy of <paramref name="fileName"/>, or null when it cannot be had.</summary>
    public string? CopyOf(string sourceDirectory, string fileName)
    {
        var origin = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(origin)) return null;

        try
        {
            if (new FileInfo(origin).Length > MaxCopyBytes)
            {
                _log.Debug($"Skipping {fileName}: larger than the copy budget.");
                return null;
            }

            Directory.CreateDirectory(_directory);

            var target = Path.Combine(_directory, fileName);
            File.Copy(origin, target, overwrite: true);

            // The -wal holds everything committed since the last checkpoint and the -shm is its
            // index. Copying the main file alone would read a stale database.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = origin + suffix;
                if (File.Exists(sidecar)) File.Copy(sidecar, target + suffix, overwrite: true);
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Debug($"Could not copy {fileName}. {ex.Message}");
            return null;
        }
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,

            // No pooling, so nothing holds the scratch copies open once reading is done.
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }

    /// <summary>
    /// The native SQLite provider loads on first touch, so a packaging or platform problem
    /// arrives as a type-initializer failure rather than as a SqliteException. Agent session
    /// lookup is one column of one kind of tab: losing the engine must cost exactly that.
    /// </summary>
    public static bool IsEngineUnavailable(Exception ex) =>
        ex is TypeInitializationException or DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException or System.Reflection.TargetInvocationException;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is not worth failing a snapshot over.
        }
    }
}

/// <summary>Where each agent keeps its state, and how a recorded directory is compared.</summary>
public static class AgentPaths
{
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Antigravity's CLI home. The name differs between installs — the newer layout is
    /// <c>.gemini/antigravity-cli</c>, the older one <c>.gemini/antigravity</c> — so both are
    /// tried, newest-looking first.
    /// </summary>
    public static IEnumerable<string> AgyHomes()
    {
        yield return Path.Combine(UserHome, ".gemini", "antigravity-cli");
        yield return Path.Combine(UserHome, ".gemini", "antigravity");
        yield return Path.Combine(UserHome, ".antigravity");
    }

    /// <summary>
    /// Compares two recorded working directories.
    ///
    /// Windows tools record the extended-length form (<c>\\?\C:\Users\…</c>) and sometimes a
    /// <c>file://</c> URI, and Windows paths are case-insensitive, so a plain string compare
    /// misses. Linux paths stay case-sensitive.
    /// </summary>
    public static string NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Trim().Replace('\\', '/');

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];

            // "file:///home/me" leaves a leading slash that belongs to the path; on Windows,
            // "file:///C:/x" leaves one that does not.
            if (text.Length > 2 && text[0] == '/' && char.IsLetter(text[1]) && text[2] == ':') text = text[1..];
        }

        if (text.StartsWith("//?/", StringComparison.Ordinal)) text = text[4..];

        text = text.TrimEnd('/');
        if (text.Length == 0) return string.Empty;

        return text.Length > 1 && text[1] == ':' ? text.ToLowerInvariant() : text;
    }
}
