using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Terminal;

/// <summary>
/// Maps the WT_PROFILE_ID a tab carries to the profile name <c>wt.exe -p</c> expects, by
/// reading Windows Terminal's own settings.json.
/// </summary>
public sealed class WtProfiles
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        // Windows Terminal ships settings.json with comments and trailing commas, and the
        // user's copy usually keeps them.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILog _log;

    public WtProfiles(ILog log) => _log = log;

    public static IEnumerable<string> CandidateSettingsPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(localAppData,
            @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(localAppData,
            @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json");
        yield return Path.Combine(localAppData, @"Microsoft\Windows Terminal\settings.json");
    }

    /// <summary>Profile GUID to display name. Empty when settings.json cannot be read.</summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        foreach (var path in CandidateSettingsPaths())
        {
            var map = TryLoad(path);
            if (map.Count > 0) return map;
        }

        _log.Debug("No Windows Terminal settings.json yielded any profiles.");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> TryLoad(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;

        try
        {
            // Explicit UTF-8: a BOM-less settings.json read as the ANSI code page turns any
            // non-ASCII profile name into mojibake, and the name is what -p has to match.
            var json = File.ReadAllText(path, new UTF8Encoding(false));
            using var document = JsonDocument.Parse(json, JsonOptions);

            if (!document.RootElement.TryGetProperty("profiles", out var profiles)) return map;

            var list = profiles.ValueKind switch
            {
                JsonValueKind.Array => profiles,
                JsonValueKind.Object when profiles.TryGetProperty("list", out var inner) => inner,
                _ => default,
            };

            if (list.ValueKind != JsonValueKind.Array) return map;

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("guid", out var guid) || guid.ValueKind != JsonValueKind.String) continue;
                if (!entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;

                var guidText = guid.GetString();
                var nameText = name.GetString();
                if (!string.IsNullOrEmpty(guidText) && !string.IsNullOrEmpty(nameText)) map[guidText] = nameText;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Could not read Windows Terminal settings at {path}. {ex.Message}");
        }

        return map;
    }
}
