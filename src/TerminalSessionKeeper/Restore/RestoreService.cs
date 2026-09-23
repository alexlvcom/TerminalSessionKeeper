using System.Diagnostics;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Native;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;

namespace TerminalSessionKeeper.Restore;

public sealed record RestoreOutcome(int Launched, int Total, string Summary, IReadOnlyList<string> Commands);

/// <summary>
/// Rebuilds the tabs from a snapshot in a fresh Windows Terminal window: each one in its own
/// directory, with its resume command waiting at the prompt.
/// </summary>
public sealed class RestoreService
{
    /// <summary>
    /// Windows Terminal drops tabs that arrive while it is still creating the window, so each
    /// launch is given room. Windows shells get noticeably more than WSL ones: a PowerShell
    /// profile can live on a redirected network drive and take about a second to load, and
    /// several starting at once makes module auto-loading flaky.
    /// </summary>
    private static readonly TimeSpan WslStagger = TimeSpan.FromMilliseconds(350);

    private static readonly TimeSpan WindowsStagger = TimeSpan.FromMilliseconds(1200);

    /// <summary>Grace before typing, so the last shell has reached a prompt.</summary>
    private static readonly TimeSpan SettleBeforeTyping = TimeSpan.FromMilliseconds(1500);

    private const string TerminalProcessName = "WindowsTerminal.exe";
    private const string ConsoleHostProcessName = "OpenConsole.exe";

    private readonly ILog _log;
    private readonly TabColorApplier _colors;

    public RestoreService(ILog log, TabColorApplier? colors = null)
    {
        _log = log;
        _colors = colors ?? new TabColorApplier(log);
    }

    /// <summary>
    /// The Windows Terminal window name this restore gathers its tabs into.
    ///
    /// Unique per restore, and that is the whole point. <c>wt -w &lt;name&gt;</c> creates a window
    /// with that name only if none exists and otherwise adds the tab to the one that does — so a
    /// fixed name meant the second restore appended its tabs to the window the first restore had
    /// built, however many days earlier. The configured name is the prefix; the clock makes it
    /// this restore's own.
    /// </summary>
    public static string WindowName(string configured, DateTimeOffset? now = null)
    {
        var prefix = string.IsNullOrWhiteSpace(configured) ? "tskrestore" : configured.Trim();

        return $"{prefix}-{(now ?? DateTimeOffset.Now):HHmmss}";
    }

    /// <summary>Prints what would be launched, and launches nothing.</summary>
    public RestoreOutcome DryRun(SnapshotRecord snapshot, AppSettings settings)
    {
        var commands = new List<string>();
        var tabs = snapshot.Tabs.ToList();
        var windowName = WindowName(settings.RestoreWindowName);

        var variables = AppSettings.ParseEnvironment(settings.RestoreWslEnvironment);
        if (variables.Count > 0)
        {
            commands.Add("WSL tabs start with: " +
                         string.Join(' ', variables.Select(variable => $"{variable.Name}={variable.Value}")) +
                         " [passed through WSLENV]");
        }

        foreach (var tab in tabs)
        {
            var arguments = WtArguments.ForTab(tab, windowName, settings.PinTitles);
            if (arguments is null)
            {
                commands.Add($"(skipped: {tab.Kind} tab with nothing to rebuild from)");
                continue;
            }

            commands.Add(WtArguments.Describe(arguments));

            var setup = WtArguments.SetupCommand(tab, settings.TypeCdIntoWslTabs);
            if (!string.IsNullOrEmpty(setup)) commands.Add("    types: " + setup);

            var title = WtArguments.TitleToReapply(tab, settings.PinTitles);
            if (!string.IsNullOrEmpty(title)) commands.Add($"    renames the tab to: {title}");

            if (settings.RestoreTabColors && !string.IsNullOrWhiteSpace(tab.Color))
            {
                commands.Add($"    colours the tab {tab.Color} [as the colour picker does, so it can be reset]");
            }

            if (!string.IsNullOrEmpty(tab.ResumeCommand))
            {
                commands.Add($"    types: {tab.ResumeCommand}" + (settings.AutoResume ? " [and runs it]" : " [waits at the prompt]"));
            }
        }

        return new RestoreOutcome(0, tabs.Count, $"Dry run: {tabs.Count} tab(s) would be rebuilt.", commands);
    }

    public RestoreOutcome Restore(SnapshotRecord snapshot, AppSettings settings)
    {
        var tabs = snapshot.Tabs.ToList();
        if (tabs.Count == 0)
        {
            return new RestoreOutcome(0, 0, "That snapshot has no tabs in it.", Array.Empty<string>());
        }

        var environment = ComposeEnvironment(settings);
        var pending = new List<(TabRecord Tab, int ProcessId)>();
        var colored = new List<ColoredTab>();
        var launched = 0;

        // A window of this restore's own. The first tab creates it and the rest join it by name.
        var windowName = WindowName(settings.RestoreWindowName);

        foreach (var tab in tabs)
        {
            var arguments = WtArguments.ForTab(tab, windowName, settings.PinTitles);
            if (arguments is null)
            {
                _log.Warn($"Skipping a {tab.Kind} tab: not enough information to rebuild it.");
                continue;
            }

            var before = TerminalChildProcessIds();

            if (!Launch(arguments, environment))
            {
                continue;
            }

            // Index among the launched tabs, which is the order they sit in the strip — and what
            // the colour pass focuses them by.
            if (settings.RestoreTabColors && !string.IsNullOrWhiteSpace(tab.Color))
            {
                colored.Add(new ColoredTab(launched, tab.Color));
            }

            launched++;

            var stagger = TabKinds.IsWindows(tab.Kind) ? WindowsStagger : WslStagger;
            Thread.Sleep(stagger);

            // Only a tab that gets typed into needs its process found, and it is found by
            // difference: the staggering means exactly one new child appears per launch.
            if (NeedsAttention(tab, settings))
            {
                var processId = WaitForNewTerminalChild(before);
                if (processId is null)
                {
                    _log.Warn($"Could not find the process for the restored '{tab.Title}' tab; " +
                              "its resume command was not typed.");
                }
                else
                {
                    pending.Add((tab, processId.Value));
                }
            }
        }

        // Colours first: it moves the focus from tab to tab, and typing goes into each tab's
        // console queue rather than to whatever is focused, so this way round nothing races.
        if (colored.Count > 0) _colors.Apply(colored, windowName, launched);

        if (pending.Count > 0)
        {
            Thread.Sleep(SettleBeforeTyping);
            foreach (var (tab, processId) in pending) TypeInto(tab, processId, settings);
        }

        var summary = $"Restored {launched} of {tabs.Count} tab(s) into a new window, '{windowName}'.";
        if (!settings.AutoResume && tabs.Any(tab => !string.IsNullOrEmpty(tab.ResumeCommand)))
        {
            summary += " Agent tabs have their resume command waiting at the prompt.";
        }

        _log.Info(summary);
        return new RestoreOutcome(launched, tabs.Count, summary, Array.Empty<string>());
    }

    /// <summary>
    /// Auto-restore is only safe when the session those tabs lived in is actually gone. The
    /// snapshot's own boot time answers the first half; a terminal window that still has tabs
    /// answers the second. Restoring alongside a live window puts both into the next snapshot,
    /// and the restore after that would rebuild twenty tabs instead of ten.
    /// </summary>
    public bool ShouldAutoRestore(SnapshotRecord snapshot, AppSettings settings, out string reason)
    {
        if (!settings.RestoreAfterReboot)
        {
            reason = "automatic restore is turned off";
            return false;
        }

        if (snapshot.TabCount == 0)
        {
            reason = "the last snapshot is empty";
            return false;
        }

        if (!BootTime.IsFromEarlierSession(snapshot.BootTimeUtc))
        {
            reason = snapshot.BootTimeUtc is null
                ? "the last snapshot predates boot-time tracking"
                : "the last snapshot is from this same session";
            return false;
        }

        if (BootTime.IsSameSession(settings.LastAutoRestoreBootTimeUtc))
        {
            reason = "tabs were already restored automatically this session";
            return false;
        }

        if (AnyTerminalHasTabs())
        {
            reason = "a Windows Terminal window is already open with tabs";
            return false;
        }

        reason = "first run since boot, and no terminal window is open";
        return true;
    }

    /// <summary>After a submitted setup line, long enough for the shell to finish it and draw
    /// the prompt the resume command waits at.</summary>
    private static readonly TimeSpan SettleAfterSetup = TimeSpan.FromMilliseconds(700);

    private static bool NeedsAttention(TabRecord tab, AppSettings settings) =>
        !string.IsNullOrEmpty(tab.ResumeCommand)
        || WtArguments.SetupCommand(tab, settings.TypeCdIntoWslTabs) is not null
        || WtArguments.TitleToReapply(tab, settings.PinTitles) is not null;

    private void TypeInto(TabRecord tab, int processId, AppSettings settings)
    {
        var setup = WtArguments.SetupCommand(tab, settings.TypeCdIntoWslTabs);

        if (!string.IsNullOrEmpty(setup))
        {
            // Submitted: the directory has to be in place before the prompt the resume command
            // waits at.
            ConsoleInjector.Send(processId, setup + "\r", _log);
            Thread.Sleep(SettleAfterSetup);
        }

        if (!string.IsNullOrEmpty(tab.ResumeCommand))
        {
            // No trailing return unless the user asked for one. Twelve agents and their MCP
            // servers all booting at once, right after a reboot, is the worst possible moment.
            var text = settings.AutoResume ? tab.ResumeCommand + "\r" : tab.ResumeCommand;
            ConsoleInjector.Send(processId, text, _log);
        }

        // Last, and deliberately so. Typing does not produce a new prompt, so nothing runs after
        // this to rename the tab back to its folder — but anything submitted earlier would have.
        var title = WtArguments.TitleToReapply(tab, settings.PinTitles);
        if (!string.IsNullOrEmpty(title)) ConsoleInjector.SetTitle(processId, title, _log);
    }

    private bool Launch(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList quotes each element; a hand-joined command line is what silently
        // truncated -p "Windows PowerShell" to -p Windows and lost every multi-word title.
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or ObjectDisposedException or SystemException)
        {
            _log.Error($"Could not launch {WtArguments.Describe(arguments)}", ex);
            return false;
        }
    }

    /// <summary>
    /// The environment wt.exe is launched with.
    ///
    /// Windows Terminal hands a tab the caller's environment whenever the command line is
    /// overridden, which is every WSL tab here. Inheriting this process's own environment
    /// unchanged is how PowerShell 7's module directories once ended up ahead of Windows
    /// PowerShell's in a restored 5.1 tab, whose profile then failed to resolve cmdlets
    /// ("Import-PowerShellDataFile is not recognized") and silently lost the user's git aliases.
    /// PSModulePath is therefore rebuilt from the User and Machine values a fresh process gets.
    ///
    /// The same hand-off is what carries <see cref="AppSettings.RestoreWslEnvironment"/> into the
    /// restored tabs, and only into them. WSL imports a Windows variable only when WSLENV names
    /// it, so each one is appended there; Windows Terminal adds its own WT_SESSION on top.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComposeEnvironment(AppSettings settings,
        string? inheritedWslEnv = null)
    {
        var composed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var variables = AppSettings.ParseEnvironment(settings.RestoreWslEnvironment);
        if (variables.Count > 0)
        {
            foreach (var (name, value) in variables) composed[name] = value;

            var inherited = inheritedWslEnv ?? Environment.GetEnvironmentVariable("WSLENV") ?? string.Empty;
            var names = inherited.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var (name, _) in variables)
            {
                // An entry may carry flags (NAME/u); the name is what makes it a duplicate.
                if (!names.Any(entry => string.Equals(entry.Split('/')[0], name, StringComparison.Ordinal)))
                {
                    names.Add(name);
                }
            }

            composed["WSLENV"] = string.Join(':', names);
        }

        var parts = new[]
            {
                Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine),
            }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0) composed["PSModulePath"] = string.Join(';', parts);

        return composed;
    }

    private static HashSet<int> TerminalChildProcessIds()
    {
        var tree = ProcessTree.Capture();

        return tree.All
            .Where(entry => string.Equals(entry.Name, TerminalProcessName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(terminal => tree.ChildrenOf(terminal.ProcessId))
            .Where(child => !string.Equals(child.Name, ConsoleHostProcessName, StringComparison.OrdinalIgnoreCase))
            .Select(child => child.ProcessId)
            .ToHashSet();
    }

    private static int? WaitForNewTerminalChild(HashSet<int> before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        while (DateTime.UtcNow < deadline)
        {
            var now = TerminalChildProcessIds();
            now.ExceptWith(before);

            // Exactly one tab is launched between two samples, so a single new child is the
            // one just created. More than one means something else opened a tab at the same
            // moment, and guessing which is which would be worse than not typing at all.
            if (now.Count == 1) return now.Single();
            if (now.Count > 1) return null;

            Thread.Sleep(150);
        }

        return null;
    }

    /// <summary>True when any Windows Terminal process has a tab open.</summary>
    public static bool AnyTerminalHasTabs()
    {
        var tree = ProcessTree.Capture();

        return tree.All
            .Where(entry => string.Equals(entry.Name, TerminalProcessName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(terminal => tree.ChildrenOf(terminal.ProcessId))
            .Any(child => !string.Equals(child.Name, ConsoleHostProcessName, StringComparison.OrdinalIgnoreCase));
    }
}
