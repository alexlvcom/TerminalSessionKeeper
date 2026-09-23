using System.Text.Json;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Settings;

public sealed class AppSettings
{
    public const int DefaultSnapshotIntervalMinutes = 10;
    public const int MinSnapshotIntervalMinutes = 1;
    public const int MaxSnapshotIntervalMinutes = 24 * 60;

    /// <summary>Take a snapshot on a timer, so an unplanned restart still has a recent one.</summary>
    public bool AutoSnapshotEnabled { get; set; } = true;

    /// <summary>Minutes between automatic snapshots. Read it through <see cref="SnapshotInterval"/>.</summary>
    public int AutoSnapshotIntervalMinutes { get; set; } = DefaultSnapshotIntervalMinutes;

    /// <summary>
    /// Put the tabs back automatically after a reboot. Guarded — see
    /// <c>RestoreService.ShouldAutoRestore</c>; restoring into a session that still has its
    /// original window captures both on the next snapshot and doubles the tab count.
    /// </summary>
    public bool RestoreAfterReboot { get; set; } = true;

    /// <summary>
    /// Run each resume command instead of leaving it at the prompt. Off by default on
    /// purpose: a dozen agents and their MCP servers all booting at once, right after a
    /// reboot, is the worst possible moment for it.
    /// </summary>
    public bool AutoResume { get; set; }

    /// <summary>Freeze restored titles with --suppressApplicationTitle instead of letting the
    /// shell and the agent rename their own tabs.</summary>
    public bool PinTitles { get; set; }

    public bool ShowBalloonNotifications { get; set; } = true;

    /// <summary>
    /// Recover each tab's colour by reading it off the screen. No API exposes a tab colour, so
    /// this is the only way to keep them — but it needs the window visible, and it samples the
    /// screen to do it, so it can be turned off.
    /// </summary>
    public bool SampleTabColors { get; set; } = true;

    /// <summary>
    /// Put each tab's colour back when the tabs are rebuilt. Doing it properly means lending
    /// Windows Terminal a setTabColor action for a second or two — see
    /// <c>WtSettingsPatch</c> — because the colour <c>wt.exe</c> can set from the command line
    /// is one the user could never clear again. Turn it off to leave restored tabs uncoloured
    /// and Windows Terminal's own settings.json untouched.
    /// </summary>
    public bool RestoreTabColors { get; set; } = true;

    /// <summary>Snapshots kept on disk. They are a few KB each.</summary>
    public int KeepSnapshots { get; set; } = 20;

    /// <summary>
    /// Base name of the Windows Terminal window restored tabs are gathered into. Each restore
    /// adds the time it ran, so it always builds a window of its own — see
    /// <c>RestoreService.WindowName</c>.
    /// </summary>
    public string RestoreWindowName { get; set; } = "tskrestore";

    /// <summary>
    /// Extra environment for restored WSL tabs, one <c>NAME=value</c> per line. Each name is
    /// added to <c>WSLENV</c> so it reaches the Linux side, and it exists only in the tabs a
    /// restore builds — a tab opened by hand afterwards never sees it. The app attaches no
    /// meaning to any name; what the variables are for is the user's business.
    /// </summary>
    public string RestoreWslEnvironment { get; set; } = string.Empty;

    /// <summary>
    /// Type <c>cd -- &lt;dir&gt;</c> into each restored WSL tab once its shell is up, in case the
    /// shell changed directory while starting up, after <c>wsl --cd</c> had applied. A shell that
    /// does not is already in place, and the line is then only noise in every tab.
    /// </summary>
    public bool TypeCdIntoWslTabs { get; set; } = true;

    /// <summary>
    /// Boot time of the session an automatic restore last ran in. Stops a second automatic
    /// restore inside one boot — the app restarting must not rebuild the tabs again.
    /// </summary>
    public DateTimeOffset? LastAutoRestoreBootTimeUtc { get; set; }

    /// <summary>
    /// The interval the timer actually uses. settings.json is hand-editable, so the stored
    /// value is clamped — zero or a negative number would throw when assigned to a timer.
    /// </summary>
    public TimeSpan SnapshotInterval => TimeSpan.FromMinutes(Math.Clamp(
        AutoSnapshotIntervalMinutes, MinSnapshotIntervalMinutes, MaxSnapshotIntervalMinutes));

    public int EffectiveKeepSnapshots => Math.Clamp(KeepSnapshots, 1, 500);

    /// <summary>
    /// The <c>NAME=value</c> lines of <see cref="RestoreWslEnvironment"/>. Blank lines and
    /// <c>#</c> comments are skipped, the value is everything after the first <c>=</c>, and a
    /// name that is not a plain identifier is dropped: WSLENV separates names with <c>:</c> and
    /// flags them with <c>/</c>, with no way to escape either.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> ParseEnvironment(string? text)
    {
        var variables = new List<(string Name, string Value)>();
        if (string.IsNullOrWhiteSpace(text)) return variables;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var name = line[..separator].Trim();
            if (!IsVariableName(name)) continue;

            variables.RemoveAll(variable => variable.Name == name);
            variables.Add((name, line[(separator + 1)..].Trim()));
        }

        return variables;
    }

    private static bool IsVariableName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILog _log;

    public SettingsStore(ILog log, string? path = null)
    {
        _log = log;
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.SettingsFile : path;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Could not read settings; using defaults. {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            _log.Warn($"Could not save settings. {ex.Message}");
        }
    }
}
