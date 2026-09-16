using System.Text.RegularExpressions;
using TerminalSessionKeeper.Agents;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Matching;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Native;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Terminal;

namespace TerminalSessionKeeper.Snapshots;

public sealed record SnapshotOutcome(SnapshotRecord? Snapshot, string? Path, string Summary, bool Captured);

/// <summary>
/// Captures the open Windows Terminal tabs: what each one is, where it is, and which agent
/// conversation is live inside it.
/// </summary>
public sealed class SnapshotService
{
    private const string TerminalProcessName = "WindowsTerminal.exe";

    /// <summary>The ConPTY host sits under every tab and is not one itself.</summary>
    private const string ConsoleHostProcessName = "OpenConsole.exe";

    private static readonly Regex WindowsShellPattern =
        new(@"^(powershell|pwsh|cmd)\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsAgentPattern =
        new(@"^(claude|codex|junie|agy)\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WslLauncherPattern =
        new(@"^(wsl|ubuntu|debian|kali|opensuse\w*|sles\w*|oracle\w*|fedora\w*|archlinux)\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILog _log;
    private readonly SnapshotStore _store;
    private readonly OverrideStore _overrides;
    private readonly WslProbe _wslProbe;
    private readonly WtProfiles _profiles;
    private readonly TerminalWindows _windows;
    private readonly TabColorSampler _colors;

    public SnapshotService(
        ILog log,
        SnapshotStore store,
        OverrideStore overrides,
        WslProbe wslProbe,
        WtProfiles profiles,
        TerminalWindows windows,
        TabColorSampler colors)
    {
        _log = log;
        _store = store;
        _overrides = overrides;
        _wslProbe = wslProbe;
        _profiles = profiles;
        _windows = windows;
        _colors = colors;
    }

    /// <summary>Builds a snapshot without writing it. Used by the restore preview and by tests.</summary>
    public SnapshotRecord Capture(bool sampleColors = true)
    {
        var defaultDistro = Wsl.DefaultDistro(_log);
        var profileMap = _profiles.Load();
        var overrides = _overrides.Load();
        var wslTabs = _wslProbe.Probe(defaultDistro);
        var previous = PreviousByWtSession();

        var tree = ProcessTree.Capture();
        var agents = new WindowsAgentState(_log);

        var snapshot = new SnapshotRecord
        {
            CapturedAt = DateTimeOffset.Now.ToString("o"),
            Machine = Environment.MachineName,
            User = Environment.UserName,
            DefaultDistro = defaultDistro,
            BootTimeUtc = BootTime.Current(),
        };

        var unmatched = new List<string>();

        foreach (var terminal in tree.All.Where(IsTerminal).OrderBy(entry => entry.ProcessId))
        {
            var tabs = new List<TabRecord>();

            foreach (var child in tree.ChildrenOfByAge(terminal.ProcessId))
            {
                if (string.Equals(child.Name, ConsoleHostProcessName, StringComparison.OrdinalIgnoreCase)) continue;

                tabs.Add(BuildTab(child, tree, wslTabs, profileMap, overrides, defaultDistro, agents));
            }

            if (tabs.Count == 0) continue;

            // Since WT 1.18 every window of the app lives in one process, so all its windows'
            // tabs are gathered together — there is no way to tell from the process tree which
            // pane belongs to which window.
            var windows = _windows.Read(terminal.ProcessId);
            var onScreen = windows.SelectMany(window => window.Tabs).ToList();

            if (onScreen.Count > 0)
            {
                // Every tab takes part in the matching, including ones an override already
                // titled: their on-screen title still has to be consumed, or it looks like a
                // leftover and spoils the forced pairing at the end.
                var titles = onScreen.Select(element => element.Title).ToList();
                var match = TitleMatcher.Assign(tabs, titles);
                unmatched.AddRange(match.UnmatchedTitles);

                if (sampleColors) ApplyColors(tabs, windows, match.TabToTitle);
            }

            foreach (var tab in tabs) ApplyFallbacks(tab, previous);

            snapshot.Windows.Add(new WindowRecord
            {
                ProcessId = terminal.ProcessId,
                MainTitle = windows.Select(window => window.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)),
                Tabs = tabs,
            });
        }

        snapshot.UnmatchedTabTitles = unmatched
            .Distinct(StringComparer.Ordinal)
            .OrderBy(title => title, StringComparer.Ordinal)
            .ToList();

        return snapshot;
    }

    /// <summary>Captures and writes a snapshot, unless there is nothing open to capture.</summary>
    public SnapshotOutcome CaptureAndSave(AppSettings settings)
    {
        SnapshotRecord snapshot;

        try
        {
            snapshot = Capture(settings.SampleTabColors);
        }
        catch (Exception ex)
        {
            _log.Error("Snapshot failed.", ex);
            return new SnapshotOutcome(null, null, "Snapshot failed. See the log for details.", false);
        }

        if (snapshot.TabCount == 0)
        {
            // Overwriting a good snapshot with an empty one would quietly destroy the thing the
            // app exists to protect, so nothing is written.
            _log.Debug("No Windows Terminal tabs are open; nothing captured.");
            return new SnapshotOutcome(snapshot, null, "No Windows Terminal tabs are open.", false);
        }

        try
        {
            var path = _store.Save(snapshot, settings.EffectiveKeepSnapshots);
            var agents = snapshot.Tabs.Count(tab => !string.IsNullOrEmpty(tab.ResumeCommand));

            var summary = agents == 0
                ? $"Saved {Plural(snapshot.TabCount, "tab")}."
                : $"Saved {Plural(snapshot.TabCount, "tab")}, {agents} resumable.";

            _log.Info($"{summary} -> {path}");
            return new SnapshotOutcome(snapshot, path, summary, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not write the snapshot.", ex);
            return new SnapshotOutcome(snapshot, null, "Could not write the snapshot.", false);
        }
    }

    private static bool IsTerminal(ProcessEntry entry) =>
        string.Equals(entry.Name, TerminalProcessName, StringComparison.OrdinalIgnoreCase);

    private TabRecord BuildTab(
        ProcessEntry child,
        ProcessTree tree,
        IReadOnlyDictionary<string, WslTabRecord> wslTabs,
        IReadOnlyDictionary<string, string> profileMap,
        IReadOnlyList<TabOverride> overrides,
        string defaultDistro,
        WindowsAgentState agents)
    {
        var environment = ProcessPeb.Environment(child.ProcessId);
        environment.TryGetValue("WT_SESSION", out var wtSession);
        environment.TryGetValue("WT_PROFILE_ID", out var profileId);

        var tab = new TabRecord
        {
            WtSession = wtSession,
            ProfileId = profileId,
            ProfileName = profileId is not null && profileMap.TryGetValue(profileId, out var name) ? name : null,
            Launcher = child.Name,
            CommandLine = ProcessPeb.CommandLine(child.ProcessId),
        };

        WslTabRecord? wslRecord = null;
        if (!string.IsNullOrEmpty(wtSession)) wslTabs.TryGetValue(wtSession, out wslRecord);

        // What Windows Terminal launched decides what the tab is. The WSL probe's record only
        // refines a tab already known to be a WSL one, and is never allowed to reclassify a
        // Windows tab: a Windows-side agent that shells into WSL leaves a pane carrying this
        // tab's WT_SESSION for as long as that subprocess lives, and a snapshot landing in that
        // window would otherwise turn a win-agent tab into a wsl-shell and lose its session.
        if (WslLauncherPattern.IsMatch(child.Name))
        {
            tab.Distro = Wsl.DistroFor(child.Name, tab.CommandLine, defaultDistro);

            if (wslRecord is not null)
            {
                tab.Kind = string.IsNullOrEmpty(wslRecord.Agent) ? TabKinds.WslShell : TabKinds.WslAgent;
                tab.Cwd = wslRecord.Cwd;
                tab.GitBranch = wslRecord.GitBranch;
                tab.Agent = wslRecord.Agent;
                tab.SessionId = wslRecord.SessionId;
                tab.SessionTitle = wslRecord.SessionTitle;
                tab.ResumeCommand = wslRecord.ResumeCommand;
            }
            else
            {
                // A WSL tab the probe could not see — python3 missing from the distro, or the
                // pane's shell is not one it recognises. It still rebuilds as a WSL tab, just
                // without a directory or a session.
                tab.Kind = TabKinds.WslShell;
            }
        }
        else if (WindowsShellPattern.IsMatch(child.Name))
        {
            tab.Cwd = ProcessPeb.CurrentDirectory(child.ProcessId);

            var agentProcess = tree.ChildrenOf(child.ProcessId)
                .FirstOrDefault(grandchild => WindowsAgentPattern.IsMatch(grandchild.Name));

            if (agentProcess is null)
            {
                tab.Kind = TabKinds.WinShell;
            }
            else
            {
                var agent = Path.GetFileNameWithoutExtension(agentProcess.Name).ToLowerInvariant();

                // The agent's own working directory beats the shell's: `cd` inside the agent
                // does not move the shell that launched it.
                var agentCwd = ProcessPeb.CurrentDirectory(agentProcess.ProcessId);
                if (!string.IsNullOrEmpty(agentCwd)) tab.Cwd = agentCwd;

                tab.Kind = TabKinds.WinAgent;
                tab.Agent = agent;

                var session = agents.Resolve(agent, tab.Cwd);
                tab.SessionId = session.SessionId;
                tab.SessionTitle = session.Title;
                tab.ResumeCommand = session.ResumeCommand;

                if (string.IsNullOrEmpty(tab.ResumeCommand))
                {
                    _log.Debug($"No resumable {agent} session found for {tab.Cwd}.");
                }
            }
        }
        else
        {
            // Something Windows Terminal launched directly, e.g. `ping 8.8.8.8 -t`.
            tab.Kind = TabKinds.Raw;
            tab.Cwd = ProcessPeb.CurrentDirectory(child.ProcessId);
        }

        if (string.IsNullOrEmpty(tab.GitBranch) && IsWindowsPath(tab.Cwd))
        {
            tab.GitBranch = GitBranch.For(tab.Cwd);
        }

        ApplyOverride(tab, overrides);
        return tab;
    }

    private static void ApplyOverride(TabRecord tab, IReadOnlyList<TabOverride> overrides)
    {
        var entry = OverrideStore.For(overrides, tab.Cwd, tab.Agent);
        if (entry is null) return;

        if (!string.IsNullOrWhiteSpace(entry.EffectiveColor)) tab.Color = entry.EffectiveColor;

        if (!string.IsNullOrWhiteSpace(entry.Title))
        {
            tab.Title = entry.Title;
            tab.TitlePinned = true;
        }
    }

    /// <summary>
    /// Attaches each sampled colour to the record the matcher paired that on-screen tab with.
    /// Position would be the wrong key — a dragged tab keeps its colour and its title but not
    /// its place — so the colour rides along with the title's own pairing.
    /// </summary>
    private void ApplyColors(IReadOnlyList<TabRecord> tabs, IReadOnlyList<TerminalWindow> windows,
        IReadOnlyDictionary<int, int> pairing)
    {
        if (pairing.Count == 0) return;

        // One flat list, in the same order the titles were flattened into.
        var sampled = windows.SelectMany(_colors.Sample).ToList();

        foreach (var (tabIndex, titleIndex) in pairing)
        {
            if (titleIndex >= sampled.Count) continue;
            if (sampled[titleIndex] is not { } color) continue;

            // A colour pinned in overrides.json is the user's decision and outranks the screen.
            if (string.IsNullOrEmpty(tabs[tabIndex].Color)) tabs[tabIndex].Color = color;
        }
    }

    private static void ApplyFallbacks(TabRecord tab, IReadOnlyDictionary<string, TabRecord> previous)
    {
        // WT_SESSION is stable for the life of a tab, so a snapshot taken when UI Automation
        // could not reach the window — or when the window was covered — keeps whatever an
        // earlier snapshot resolved.
        if (!string.IsNullOrEmpty(tab.WtSession) && previous.TryGetValue(tab.WtSession, out var earlier))
        {
            if (string.IsNullOrEmpty(tab.Title)) tab.Title = earlier.Title;

            // Colour especially: the active tab's colour cannot be told apart from the terminal
            // background, so it is carried from a snapshot where the tab was not the active one.
            if (string.IsNullOrEmpty(tab.Color)) tab.Color = earlier.Color;
        }

        if (string.IsNullOrEmpty(tab.Title)) tab.Title = tab.FolderName;
    }

    private IReadOnlyDictionary<string, TabRecord> PreviousByWtSession()
    {
        var map = new Dictionary<string, TabRecord>(StringComparer.Ordinal);

        var previous = _store.Load();
        if (previous is null) return map;

        foreach (var tab in previous.Tabs)
        {
            if (!string.IsNullOrEmpty(tab.WtSession)) map[tab.WtSession] = tab;
        }

        return map;
    }

    private static bool IsWindowsPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
