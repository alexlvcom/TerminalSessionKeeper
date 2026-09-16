using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Snapshots;

/// <summary>
/// overrides.json: the user's say over a tab's title and colour.
///
/// It exists because no Windows Terminal API exposes a tab's colour, so a colour can only ever
/// come from here — and because a title the matcher could not work out is better fixed once by
/// hand than guessed at on every snapshot.
/// </summary>
public sealed class OverrideStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILog _log;

    public OverrideStore(ILog log, string? path = null)
    {
        _log = log;
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.OverridesFile : path;
    }

    public string Path => _path;

    public IReadOnlyList<TabOverride> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<TabOverride>();

            var json = File.ReadAllText(_path, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TabOverride>();

            return JsonSerializer.Deserialize<List<TabOverride>>(json, Options)
                   ?? (IReadOnlyList<TabOverride>)Array.Empty<TabOverride>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Hand-edited by design, so a typo here must never cost the user a snapshot.
            _log.Warn($"Ignoring unreadable overrides.json. {ex.Message}");
            return Array.Empty<TabOverride>();
        }
    }

    /// <summary>Writes an empty list if the file is missing, so there is something to edit.</summary>
    public void EnsureExists()
    {
        try
        {
            if (File.Exists(_path)) return;

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, "[]" + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Could not create overrides.json. {ex.Message}");
        }
    }

    /// <summary>
    /// The entry for a tab, matched on the exact working directory and, when the entry names
    /// one, the agent — which is how two tabs sharing a directory can be told apart.
    /// </summary>
    public static TabOverride? For(IReadOnlyList<TabOverride> overrides, string? cwd, string? agent)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        foreach (var entry in overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Cwd)) continue;
            if (!string.Equals(entry.Cwd, cwd, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrWhiteSpace(entry.Agent)
                && !string.Equals(entry.Agent, agent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entry;
        }

        return null;
    }
}
