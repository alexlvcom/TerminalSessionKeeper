using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalSessionKeeper.Model;

/// <summary>
/// The snapshot file format. It is deliberately plain, camel-cased JSON: fixing a mis-matched
/// title by hand is meant to be a one-line edit, which is why nothing here is an opaque blob.
/// </summary>
public sealed class SnapshotRecord
{
    public string CapturedAt { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string? DefaultDistro { get; set; }

    /// <summary>
    /// When the machine these tabs belonged to was last booted. Auto-restore compares it with
    /// the current boot time to tell "the session these tabs lived in is gone" from "the user
    /// is still sitting in it" — restoring into a live session doubles the next snapshot.
    /// </summary>
    public DateTimeOffset? BootTimeUtc { get; set; }

    public List<WindowRecord> Windows { get; set; } = new();

    /// <summary>On-screen tab titles that could not be attributed to a tab. Reported rather
    /// than guessed; the user resolves them in overrides.json.</summary>
    public List<string> UnmatchedTabTitles { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<TabRecord> Tabs => Windows.SelectMany(window => window.Tabs);

    [JsonIgnore]
    public int TabCount => Windows.Sum(window => window.Tabs.Count);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class WindowRecord
{
    public int ProcessId { get; set; }
    public string? MainTitle { get; set; }
    public List<TabRecord> Tabs { get; set; } = new();
}

/// <summary>What a tab is, which decides how it is rebuilt.</summary>
public static class TabKinds
{
    public const string WslAgent = "wsl-agent";
    public const string WslShell = "wsl-shell";
    public const string WinAgent = "win-agent";
    public const string WinShell = "win-shell";

    /// <summary>Something Windows Terminal launched directly, such as <c>ping 8.8.8.8 -t</c>.</summary>
    public const string Raw = "raw";

    public static bool IsWsl(string? kind) => kind is WslAgent or WslShell;
    public static bool IsWindows(string? kind) => kind is WinAgent or WinShell;
}

public sealed class TabRecord
{
    public string Kind { get; set; } = TabKinds.WinShell;

    /// <summary>
    /// The GUID Windows Terminal injects into every pane's environment — the only exact link
    /// between a WSL shell and its Windows tab. Process start times cannot stand in for it:
    /// WSL's clock drifts from the host's across hibernate, by many hours and not by a
    /// constant offset.
    /// </summary>
    public string? WtSession { get; set; }

    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>The direct child of WindowsTerminal.exe, e.g. <c>ubuntu.exe</c> or <c>pwsh.exe</c>.</summary>
    public string? Launcher { get; set; }

    public string? CommandLine { get; set; }
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public string? Distro { get; set; }

    /// <summary>"claude", "codex", "junie", or null for a plain shell.</summary>
    public string? Agent { get; set; }

    public string? SessionId { get; set; }
    public string? SessionTitle { get; set; }
    public string? ResumeCommand { get; set; }
    public string? Title { get; set; }

    /// <summary>
    /// Set when the title came from overrides.json. A pinned title is never overwritten by
    /// the matcher, and the restored tab is told to ignore the shell's own title updates.
    /// </summary>
    public bool TitlePinned { get; set; }

    /// <summary>Tab colour, e.g. "#7B3FF2". No API exposes the live one, so it only ever
    /// comes from overrides.json.</summary>
    public string? Color { get; set; }

    /// <summary>Last path segment of <see cref="Cwd"/>, the name a shell titles a tab with.</summary>
    [JsonIgnore]
    public string? FolderName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Cwd)) return null;
            var trimmed = Cwd.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return null;
            var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return cut < 0 ? trimmed : trimmed[(cut + 1)..];
        }
    }
}

/// <summary>An entry in overrides.json: the user's say over a tab's title and colour.</summary>
public sealed class TabOverride
{
    public string? Cwd { get; set; }

    /// <summary>Optional; narrows the entry to one agent when two tabs share a directory.</summary>
    public string? Agent { get; set; }

    public string? Title { get; set; }

    /// <summary>"color" is the snapshot's own spelling; "tabColor" matches the wt.exe flag.
    /// Both are accepted so a user can copy either from the docs.</summary>
    public string? Color { get; set; }

    public string? TabColor { get; set; }

    [JsonIgnore]
    public string? EffectiveColor => string.IsNullOrWhiteSpace(Color) ? TabColor : Color;
}
