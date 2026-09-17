namespace TerminalSessionKeeper.Services;

/// <summary>All TerminalSessionKeeper state lives under %LocalAppData%\TerminalSessionKeeper.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalSessionKeeper");

    public static string LogDirectory => Path.Combine(Root, "Logs");

    /// <summary>Saved snapshots, newest kept; hand-editable JSON by design.</summary>
    public static string SnapshotDirectory => Path.Combine(Root, "Snapshots");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Per-directory title and tab-colour pins. Colours cannot be read back from
    /// Windows Terminal, so this file is the only way they survive a restore.</summary>
    public static string OverridesFile => Path.Combine(Root, "overrides.json");

    /// <summary>
    /// What the tab-colour sampler has been reading, per tab, across snapshots. A colour has to
    /// be read twice before it is recorded; this is where the first reading waits.
    /// </summary>
    public static string TabColorMemoryFile => Path.Combine(Root, "tab-colors.json");

    /// <summary>
    /// Written while restored tab colours are being applied, and deleted again immediately.
    /// Applying a colour means adding an action to Windows Terminal's own settings.json for a
    /// moment; if the app dies in that moment, this file is what the next start uses to take
    /// the addition back out.
    /// </summary>
    public static string TerminalPatchMarkerFile => Path.Combine(Root, "wt-patch-in-progress.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SnapshotDirectory);
    }
}
