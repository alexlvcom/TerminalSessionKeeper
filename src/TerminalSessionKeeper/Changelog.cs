using System.Reflection;

namespace TerminalSessionKeeper;

/// <summary>
/// App version info and human-readable change history shown in the tray menu → About.
/// Add a new entry at the top of <see cref="Entries"/> whenever behaviour changes,
/// and bump &lt;VersionPrefix&gt; in the .csproj to match.
/// </summary>
public static class Changelog
{
    /// <summary>Newest first. Each entry: version, date, and bullet notes.</summary>
    public static readonly (string Version, string Date, string[] Notes)[] Entries =
    {
        ("1.1.0", "2026-09-17", new[]
        {
            "A restored tab colour can be cleared again. Windows Terminal keeps two colours per",
            "     tab — the one the command line sets and the one the right-click picker sets on",
            "     top of it — and the picker's Reset clears only the second, so a colour restored",
            "     with --tabColor came straight back every time it was cleared. Colours are now",
            "     applied the way the picker applies them, through the setTabColor action, which",
            "     means lending Windows Terminal that action on an F13-and-up key for the second",
            "     or two it takes to press it. The entries are removed by id afterwards, and by",
            "     the next start if the app is killed mid-restore.",
            "Tabs no longer pick up colours they never had. The sampler was reading its own",
            "     output: a restored colour is sampled again on the next snapshot, and un-blending",
            "     multiplies a rounding error by more than three, so colours drifted until they",
            "     saturated — #DD153D became #FF0051, another tab sank to black, and a plain tab",
            "     acquired a grey. A colour now has to be read the same way twice before it is",
            "     recorded, a reading close to the colour on file leaves it alone, and a colour",
            "     read as cleared twice is forgotten — so clearing one in the terminal sticks.",
            "A frame the two background readings disagree about is no longer read at all. One",
            "     bad frame that reported the tab strip as black had invented a colour for every",
            "     uncoloured tab in the window at once.",
            "A tab scrolled out of an overflowing tab strip is skipped rather than sampled from",
            "     the sliver of it still on screen.",
            "New setting, 'Put tab colours back when tabs are rebuilt', on by default. Turn it",
            "     off to leave restored tabs uncoloured and Windows Terminal's settings untouched.",
            "A restore builds a window of its own again. Every restore aimed at one fixed window",
            "     name, and wt.exe adds a tab to a window that already has that name rather than",
            "     making one — so the second restore appended its tabs to the window the first had",
            "     built, however many days earlier. The configured name is now the prefix and the",
            "     time the restore ran completes it.",
        }),

        ("1.0.0", "2026-09-16", new[]
        {
            "Initial release. Saves the open Windows Terminal tabs — including the live",
            "     claude and codex conversations inside them — and rebuilds the window after",
            "     a restart, each tab back in its own directory.",
            "Tabs are identified by WT_SESSION, the id Windows Terminal puts in every pane's",
            "     environment. Process start times are never used: WSL's clock drifts from the",
            "     host's across hibernate, by many hours and not by a constant offset.",
            "Session ids come from the running processes themselves — claude's open session",
            "     handle inside WSL, the codex SQLite state — so two tabs in one folder still",
            "     get their own conversation.",
            "Titles are matched to tabs by content, not by position: the agent's own session",
            "     title, then the git branch (which is how a tab renamed 'ABC-1022' finds its",
            "     way back to the checkout on ABC-1022-JWT-Alongside-Session-Auth), then the",
            "     folder name. One tab and one title left over are paired; more than one are",
            "     reported rather than guessed at.",
            "Restored tabs come back with the resume command waiting at the prompt, typed",
            "     into the console input queue. Nothing runs until Enter — a dozen agents and",
            "     their MCP servers all booting at once, right after a reboot, is not what you",
            "     want. Turn on 'Run resume commands automatically' if you disagree.",
            "No shell-side hook to install: the WSL side is driven the same way as the Windows",
            "     side, by attaching to the tab's console and typing into it.",
            "Agents: claude, codex, junie and agy (Antigravity), on both the Windows and the",
            "     WSL side of a tab.",
            "Tab colours are kept. No Windows Terminal API exposes them, so the window is asked",
            "     to render itself and the colour is read from the tab's own pixels — which works",
            "     while the terminal is behind other windows, as it usually is when the snapshot",
            "     timer fires. A minimized window cannot be read, and those colours carry forward",
            "     from the previous snapshot instead. Anything uncertain is left uncoloured rather",
            "     than guessed at, and overrides.json always wins.",
            "The tray menu holds two actions and two windows; everything set once lives in",
            "     Settings, and everything about a saved snapshot lives in the Snapshots window,",
            "     where it can be read before it is acted on.",
            "Automatic restore waits for the first run after a reboot and for there to be no",
            "     terminal window already open — restoring alongside a live window would put",
            "     both into the next snapshot and double the tab count after that.",
        }),
    };

    public const string Author = "Alex LV";
    public const string AuthorEmail = "alex@alexlv.com";
    public const string Copyright = "Copyright © 2026 Alex LV";

    /// <summary>Author line shown in the About window header.</summary>
    public static string AuthorLine => $"{Author}  ·  {AuthorEmail}";

    /// <summary>Marketing version, e.g. "1.0.0" (from the newest changelog entry).</summary>
    public static string MarketingVersion => Entries[0].Version;

    /// <summary>Date of the newest changelog entry.</summary>
    public static string MarketingDate => Entries[0].Date;

    /// <summary>Full build version including the auto-incrementing build/revision.</summary>
    public static string BuildVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version?.ToString() ?? MarketingVersion;
        }
    }

    /// <summary>Timestamp this executable was built.</summary>
    public static DateTime BuildDate
    {
        get
        {
            try
            {
                // Environment.ProcessPath is the only reliable source in a single-file publish,
                // where Assembly.Location is empty.
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return File.GetLastWriteTime(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall through to the sentinel.
            }

            return DateTime.MinValue;
        }
    }

    /// <summary>What the app is for.</summary>
    public static readonly string[] Description =
    {
        "Saves the open Windows Terminal tabs — including the live claude and codex",
        "conversations running inside them — and rebuilds the window after a restart,",
        "with each tab back in its own directory and its resume command waiting at the",
        "prompt.",
        "",
        "Windows Update does not care that you had twelve tabs open, each in a different",
        "project with a different agent session. This does.",
    };

    /// <summary>What the app touches, surfaced for transparency.</summary>
    public static readonly string[] SystemNotes =
    {
        @"Reads:   the process tree, and each tab's environment and working directory",
        @"         claude and codex session state, and .git/HEAD for the branch name",
        @"Writes:  %LocalAppData%\TerminalSessionKeeper  (snapshots, settings, log)",
        @"         HKCU\...\CurrentVersion\Run           (only while 'Start with Windows' is on)",
        "         Nothing machine-wide. No elevation. No scheduled task. Nothing in your dotfiles.",
    };

    /// <summary>
    /// Everything except the version header. Used for the About window body, which already shows
    /// the version and build stamp in its header.
    /// </summary>
    public static string FormatBody()
    {
        var lines = new List<string>();
        lines.AddRange(Description);
        lines.Add("");
        lines.AddRange(SystemNotes);
        lines.Add("");
        lines.Add("What's changed");
        lines.Add("──────────────");

        foreach (var (version, date, notes) in Entries)
        {
            lines.Add("");
            lines.Add($"v{version}  ({date})");
            foreach (var note in notes) lines.Add("  • " + note);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The version header plus <see cref="FormatBody"/> — the form used for the clipboard, so a
    /// pasted copy identifies the exact build it came from.
    /// </summary>
    public static string FormatFull()
    {
        var header = new[]
        {
            $"{AppInfo.DisplayName}  v{MarketingVersion}",
            $"Build {BuildVersion}" +
                (BuildDate == DateTime.MinValue ? "" : $"   (built {BuildDate:yyyy-MM-dd HH:mm})"),
            $"{Author} <{AuthorEmail}>",
            Copyright,
            "",
        };

        return string.Join(Environment.NewLine, header) + FormatBody();
    }
}
