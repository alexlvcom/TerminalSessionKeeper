namespace TerminalSessionKeeper;

/// <summary>
/// How the app names itself to the user.
///
/// The three-word name is what people read — window titles, menus, balloons, dialogs. The
/// single-word form stays reserved for things that are identifiers rather than prose: the
/// assembly, the repository, the folder under %LocalAppData%, and the Run-key value name,
/// which must not change or an existing autostart registration would be orphaned.
/// </summary>
public static class AppInfo
{
    /// <summary>The name shown to the user. Always this, in any user-visible string.</summary>
    public const string DisplayName = "Terminal Session Keeper";

    /// <summary>The identifier form: assembly, folder and registry value names.</summary>
    public const string ShortName = "TerminalSessionKeeper";
}
