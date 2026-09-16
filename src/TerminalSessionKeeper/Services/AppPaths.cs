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

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SnapshotDirectory);
    }
}
