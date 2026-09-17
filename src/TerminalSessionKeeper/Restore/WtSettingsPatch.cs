using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Terminal;

namespace TerminalSessionKeeper.Restore;

/// <summary>
/// Lends Windows Terminal a handful of <c>setTabColor</c> actions for as long as it takes to
/// colour the restored tabs, then takes them back out.
///
/// It exists because of a distinction Windows Terminal draws between two tab colours. The one
/// <c>wt new-tab --tabColor</c> sets belongs to the tab's settings; the one the right-click
/// colour picker sets is a runtime override on top of it. The picker's Reset button clears only
/// the override — so a tab coloured from the command line comes back to that colour every time
/// the user tries to clear it, and there is no way to be rid of it short of closing the tab.
/// Restoring a colour the user cannot then remove is not restoring it.
///
/// The <c>setTabColor</c> action writes the same runtime override the picker does, and Reset
/// clears it as it should — but Windows Terminal exposes actions only to keys, never to
/// <c>wt.exe</c>. So the action is added to settings.json with a key bound to it, the key is
/// pressed, and the action is removed again. F13 upwards are used deliberately: no keyboard can
/// produce them, so nothing the user presses can collide with one while it exists.
///
/// Everything here is written to be taken back out even if the app dies mid-restore:
/// <see cref="Revert"/> removes entries by id rather than by restoring a copy of the file, so a
/// change Windows Terminal or the user made in the meantime is never clobbered, and the marker
/// file lets the next start finish the job.
/// </summary>
public sealed class WtSettingsPatch
{
    /// <summary>Every entry this class adds is named with it, and removed by it.</summary>
    public const string IdPrefix = "TerminalSessionKeeper.RestoreTabColor.";

    /// <summary>VK_F13. Twelve keys to F24, none of which exist on a keyboard.</summary>
    public const int FirstVirtualKey = 0x7C;

    public const int MaxColorsPerPass = 12;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILog _log;
    private readonly string? _path;

    public WtSettingsPatch(ILog log, string? path = null)
    {
        _log = log;
        _path = path ?? WtProfiles.CandidateSettingsPaths().FirstOrDefault(File.Exists);
    }

    public bool IsAvailable => _path is not null;

    /// <summary>
    /// Adds one <c>setTabColor</c> action per colour, bound to F13 upwards in the order given,
    /// and returns the virtual key codes to press for each. Empty when nothing could be added.
    /// </summary>
    public IReadOnlyList<int> Apply(IReadOnlyList<string> colors)
    {
        if (_path is null || colors.Count == 0) return Array.Empty<int>();

        try
        {
            var original = File.ReadAllText(_path, new UTF8Encoding(false));
            var patched = Insert(original, colors);
            if (patched is null) return Array.Empty<int>();

            if (!IsParseable(patched))
            {
                _log.Warn("Not colouring restored tabs: the patched Windows Terminal settings " +
                          "would not parse, so the file has been left alone.");
                return Array.Empty<int>();
            }

            WriteMarker();
            File.WriteAllText(_path, patched, new UTF8Encoding(false));

            _log.Debug($"Added {colors.Count} temporary setTabColor action(s) to {_path}.");
            return Enumerable.Range(0, colors.Count).Select(index => FirstVirtualKey + index).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or ArgumentException)
        {
            _log.Warn($"Could not add the temporary tab-colour actions. {ex.Message}");
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Removes every entry this class ever added, whether this run added it or an earlier run
    /// died before it could. Safe to call when there is nothing to remove.
    /// </summary>
    public void Revert()
    {
        if (_path is null) return;

        try
        {
            if (!File.Exists(_path)) return;

            var text = File.ReadAllText(_path, new UTF8Encoding(false));
            var cleaned = RemoveEntries(text);

            if (cleaned is not null && !ReferenceEquals(cleaned, text) && cleaned != text)
            {
                if (IsParseable(cleaned))
                {
                    File.WriteAllText(_path, cleaned, new UTF8Encoding(false));
                    _log.Debug("Removed the temporary tab-colour actions from the Windows Terminal settings.");
                }
                else
                {
                    _log.Warn("Left the temporary tab-colour actions in place: removing them " +
                              "would have produced a settings file that does not parse. Delete " +
                              $"the entries named {IdPrefix}* by hand.");
                    return;
                }
            }

            ClearMarker();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Could not remove the temporary tab-colour actions. {ex.Message}");
        }
    }

    /// <summary>True when a previous run was interrupted while the settings were patched.</summary>
    public static bool MarkerExists() => File.Exists(AppPaths.TerminalPatchMarkerFile);

    /// <summary>
    /// The patched text, or null when there is nowhere to put the entries. Text in, text out:
    /// re-serialising the document would strip the comments and the formatting out of a file
    /// the user reads and edits.
    /// </summary>
    public static string? Insert(string settings, IReadOnlyList<string> colors)
    {
        var actions = new List<string>();
        var keys = new List<string>();

        for (var index = 0; index < colors.Count && index < MaxColorsPerPass; index++)
        {
            var id = IdPrefix + index;
            actions.Add($"{{ \"command\": {{ \"action\": \"setTabColor\", \"color\": \"{colors[index]}\" }}, \"id\": \"{id}\" }}");
            keys.Add($"{{ \"id\": \"{id}\", \"keys\": \"ctrl+alt+shift+f{13 + index}\" }}");
        }

        var patched = InsertInto(settings, "actions", actions);
        if (patched is null) return null;

        return InsertInto(patched, "keybindings", keys);
    }

    /// <summary>
    /// Every object carrying one of this class's ids, gone — along with the comma that held it
    /// in its array. Done on the text for the same reason <see cref="Insert"/> is.
    /// </summary>
    public static string? RemoveEntries(string settings)
    {
        var text = settings;

        while (true)
        {
            var marker = text.IndexOf(IdPrefix, StringComparison.Ordinal);
            if (marker < 0) return text;

            var open = OpeningBrace(text, marker);
            if (open < 0) return null;

            var close = ClosingBrace(text, open);
            if (close < 0) return null;

            var from = open;
            var to = close + 1;

            // Take the comma with it, whichever side of the object it is on, or the array is
            // left with a dangling separator.
            var after = to;
            while (after < text.Length && char.IsWhiteSpace(text[after])) after++;

            if (after < text.Length && text[after] == ',')
            {
                to = after + 1;
            }
            else
            {
                var before = from - 1;
                while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
                if (before >= 0 && text[before] == ',') from = before;
            }

            // And the line it sat on, indent and newline both, so the array is not left with a
            // blank line where the entry used to be.
            while (from > 0 && (text[from - 1] == ' ' || text[from - 1] == '\t')) from--;

            while (to < text.Length && (text[to] == ' ' || text[to] == '\t' || text[to] == '\r')) to++;
            if (to < text.Length && text[to] == '\n') to++;

            text = text[..from] + text[to..];
        }
    }

    /// <summary>
    /// Puts <paramref name="block"/> at the top of the named array. The array is created when
    /// the file has no such key — a settings.json with no "actions" at all is perfectly valid.
    /// </summary>
    private static string? InsertInto(string settings, string key, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0) return settings;

        var keyText = $"\"{key}\"";
        var at = settings.IndexOf(keyText, StringComparison.Ordinal);

        if (at >= 0)
        {
            var open = settings.IndexOf('[', at);
            if (open < 0) return null;

            // Only if the bracket really belongs to this key: anything but whitespace and the
            // colon between them means the match was somewhere else entirely.
            var between = settings[(at + keyText.Length)..open];
            if (between.Any(character => character != ':' && !char.IsWhiteSpace(character))) return null;

            // An array that was empty takes no trailing comma, or the file is left with a
            // separator and nothing after it.
            var next = open + 1;
            while (next < settings.Length && char.IsWhiteSpace(settings[next])) next++;
            var empty = next < settings.Length && settings[next] == ']';

            // Step over the line break the file already has after the bracket rather than
            // adding a second one — removing the entries again has to give back the file byte
            // for byte.
            var at2 = open + 1;
            var lead = string.Empty;

            if (at2 < settings.Length && settings[at2] is '\r' or '\n')
            {
                if (settings[at2] == '\r' && at2 + 1 < settings.Length && settings[at2 + 1] == '\n') at2 += 2;
                else at2++;
            }
            else
            {
                lead = Newline(settings);
            }

            return settings[..at2] + lead + Block(entries, !empty, Newline(settings)) + settings[at2..];
        }

        // No such array: add one straight after the opening brace of the document.
        var root = settings.IndexOf('{');
        if (root < 0) return null;

        var newline = Newline(settings);

        return settings[..(root + 1)]
               + $"{newline}    {keyText}:{newline}    [{newline}"
               + Block(entries, trailingComma: false, newline)
               + $"    ],{newline}"
               + settings[(root + 1)..];
    }

    /// <summary>The entries as lines of an array, indented to match a hand-edited settings.json.</summary>
    private static string Block(IReadOnlyList<string> entries, bool trailingComma, string newline)
    {
        var block = new StringBuilder();

        for (var index = 0; index < entries.Count; index++)
        {
            var last = index == entries.Count - 1;
            block.Append("        ").Append(entries[index]).Append(!last || trailingComma ? "," : string.Empty);
            block.Append(newline);
        }

        return block.ToString();
    }

    /// <summary>The line ending the file already uses, so an edit does not mix the two.</summary>
    private static string Newline(string settings) => settings.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int OpeningBrace(string text, int from)
    {
        var depth = 0;

        for (var index = from; index >= 0; index--)
        {
            if (text[index] == '}') depth++;
            else if (text[index] == '{')
            {
                if (depth == 0) return index;
                depth--;
            }
        }

        return -1;
    }

    private static int ClosingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;

        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];

            if (inString)
            {
                if (character == '\\') index++;
                else if (character == '"') inString = false;
                continue;
            }

            switch (character)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return index;
                    break;
            }
        }

        return -1;
    }

    private static bool IsParseable(string settings)
    {
        try
        {
            using var _ = JsonDocument.Parse(settings, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.TerminalPatchMarkerFile,
                $"{{\"settingsPath\":{JsonSerializer.Serialize(_path)},\"at\":\"{DateTimeOffset.UtcNow:o}\"}}",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"Could not write the tab-colour marker file. {ex.Message}");
        }
    }

    private void ClearMarker()
    {
        try
        {
            if (File.Exists(AppPaths.TerminalPatchMarkerFile)) File.Delete(AppPaths.TerminalPatchMarkerFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"Could not remove the tab-colour marker file. {ex.Message}");
        }
    }
}
