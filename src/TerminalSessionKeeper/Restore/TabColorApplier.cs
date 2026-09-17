using System.Diagnostics;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Native;
using TerminalSessionKeeper.Terminal;

namespace TerminalSessionKeeper.Restore;

/// <summary>A restored tab that should come back coloured: where it landed, and in what colour.</summary>
/// <param name="Index">Its position among the tabs this restore launched, counting from zero.</param>
public sealed record ColoredTab(int Index, string Color);

/// <summary>
/// Paints the restored tabs, one focused tab and one keystroke at a time.
///
/// <see cref="WtSettingsPatch"/> explains why it has to be done this way rather than with
/// <c>wt new-tab --tabColor</c>: the command line sets a colour the user can never clear again,
/// and a colour that cannot be cleared is worse than no colour at all.
///
/// The colours are grouped first, because each distinct colour costs one action in Windows
/// Terminal's settings and one key to press it with, while a tab costs only a keystroke. Twelve
/// distinct colours fit in a pass; a thirteenth starts another one.
///
/// Nothing is pressed on trust. <c>wt focus-tab</c> hands its request to the running terminal and
/// returns, so the tab that had the focus a moment ago may still have it — which is how the first
/// keystroke of a pass once coloured the wrong tab. Every keystroke here waits for UI Automation
/// to confirm that the intended tab is selected, in the window this restore built, and is dropped
/// if it cannot be: a stray Ctrl+Alt+Shift+F13 in somebody else's window is not worth a colour.
/// </summary>
public sealed class TabColorApplier
{
    /// <summary>Long enough for Windows Terminal to notice its settings changed and reload them.</summary>
    private static readonly TimeSpan SettingsReload = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long to wait for a focus request to actually move the selection.</summary>
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FocusPoll = TimeSpan.FromMilliseconds(120);

    /// <summary>After the selection has moved, before the key: the strip is still animating.</summary>
    private static readonly TimeSpan BeforeKeystroke = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan AfterKeystroke = TimeSpan.FromMilliseconds(120);

    private const string TerminalProcessName = "WindowsTerminal";

    private readonly ILog _log;
    private readonly TerminalWindows _windows;
    private readonly Func<WtSettingsPatch> _patches;

    public TabColorApplier(ILog log, Func<WtSettingsPatch>? patches = null)
    {
        _log = log;
        _windows = new TerminalWindows(log);
        _patches = patches ?? (() => new WtSettingsPatch(log));
    }

    /// <summary>How many tabs came back coloured.</summary>
    public int Apply(IReadOnlyList<ColoredTab> tabs, string windowName, int launchedTabs)
    {
        if (tabs.Count == 0) return 0;

        if (!_patches().IsAvailable)
        {
            _log.Warn("Restored tab colours were skipped: Windows Terminal's settings.json could not be found.");
            return 0;
        }

        var window = Locate(windowName, launchedTabs);
        if (window is null)
        {
            _log.Warn("Restored tab colours were skipped: the rebuilt window could not be identified.");
            return 0;
        }

        // Tabs the restore appended to a window that already had some are offset by whatever was
        // there first, and --index counts from the start of the strip.
        var offset = Math.Max(TabCount(window.Value) - launchedTabs, 0);
        if (offset > 0) _log.Debug($"The restore window already had {offset} tab(s); colours are offset by that.");

        var applied = 0;

        foreach (var pass in Passes(tabs))
        {
            var colors = pass.Select(group => group.Key).ToList();
            var patch = _patches();
            var keys = patch.Apply(colors);

            if (keys.Count == 0) break;

            try
            {
                Thread.Sleep(SettingsReload);

                for (var index = 0; index < pass.Count; index++)
                {
                    foreach (var tab in pass[index])
                    {
                        if (Paint(window.Value, windowName, offset + tab.Index, (ushort)keys[index])) applied++;
                    }
                }
            }
            finally
            {
                patch.Revert();
            }
        }

        _log.Info($"Coloured {applied} of {tabs.Count} restored tab(s).");
        return applied;
    }

    /// <summary>Takes back any actions an interrupted restore left in the terminal's settings.</summary>
    public void RevertLeftovers()
    {
        if (!WtSettingsPatch.MarkerExists()) return;

        _log.Warn("A previous restore was interrupted while colouring tabs; removing what it " +
                  "left in the Windows Terminal settings.");

        _patches().Revert();
    }

    /// <summary>Distinct colours, in groups of at most one settings pass.</summary>
    public static IReadOnlyList<IReadOnlyList<IGrouping<string, ColoredTab>>> Passes(IReadOnlyList<ColoredTab> tabs) =>
        tabs.Where(tab => !string.IsNullOrWhiteSpace(tab.Color))
            .GroupBy(tab => tab.Color.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Chunk(WtSettingsPatch.MaxColorsPerPass)
            .Select(chunk => (IReadOnlyList<IGrouping<string, ColoredTab>>)chunk)
            .ToList();

    private bool Paint((IntPtr Handle, int ProcessId) window, string windowName, int index, ushort key)
    {
        if (!RequestFocus(windowName, index)) return false;
        if (!WaitForSelection(window, index)) return false;

        // The keystroke goes wherever the focus is, so the window has to be the one in front —
        // and has to be this window, not merely some terminal. Windows refuses to hand the
        // foreground to a background process while the user is busy in another application, so
        // this is asked for rather than assumed, and given up on rather than forced.
        if (!WaitForForeground(window.Handle))
        {
            _log.Warn($"Tab {index} was not coloured: the rebuilt window did not come to the front.");
            return false;
        }

        if (!KeyboardInput.SendCtrlAltShift(key))
        {
            _log.Warn($"Tab {index} was not coloured: the keystroke was not accepted.");
            return false;
        }

        Thread.Sleep(AfterKeystroke);
        return true;
    }

    /// <summary>
    /// The rebuilt window, found by asking Windows Terminal to focus its first tab and seeing
    /// which window comes forward with that tab selected. There is no way to ask the terminal
    /// for a named window's handle, and matching on tab titles would pick the wrong window the
    /// moment two tabs share a folder name — so the answer has to be checked rather than taken:
    /// the window in front is any terminal window until it turns out to have the first tab
    /// selected and room for every tab this restore launched.
    /// </summary>
    private (IntPtr Handle, int ProcessId)? Locate(string windowName, int launchedTabs)
    {
        if (!RequestFocus(windowName, 0)) return null;

        var deadline = DateTime.UtcNow + FocusTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var foreground = KeyboardInput.Foreground();

            if (foreground != IntPtr.Zero && IsTerminal(foreground))
            {
                var candidate = (Handle: foreground, ProcessId: KeyboardInput.ProcessIdOf(foreground));
                if (Qualifies(Tabs(candidate), launchedTabs)) return candidate;
            }

            Thread.Sleep(FocusPoll);
        }

        // Nothing came forward — the user was working in another application the whole time, and
        // Windows does not let a background window take the foreground from it. The window is
        // still findable; whether it can be brought forward to press a key at is Paint's problem.
        foreach (var process in Process.GetProcessesByName(TerminalProcessName))
        {
            foreach (var candidate in _windows.Read(process.Id))
            {
                if (Qualifies(candidate.Tabs, launchedTabs)) return (candidate.Handle, process.Id);
            }
        }

        return null;
    }

    private bool WaitForForeground(IntPtr window)
    {
        var deadline = DateTime.UtcNow + FocusTimeout;

        while (DateTime.UtcNow < deadline)
        {
            KeyboardInput.Focus(window);
            Thread.Sleep(BeforeKeystroke);

            if (KeyboardInput.Foreground() == window) return true;
        }

        return false;
    }

    /// <summary>The window just asked to focus its first tab, with room for everything restored.</summary>
    private static bool Qualifies(IReadOnlyList<TabElement>? tabs, int launchedTabs) =>
        tabs is { Count: > 0 } && tabs[0].IsSelected && tabs.Count >= launchedTabs;

    private int TabCount((IntPtr Handle, int ProcessId) window) => Tabs(window)?.Count ?? 0;

    private IReadOnlyList<TabElement>? Tabs((IntPtr Handle, int ProcessId) window) =>
        _windows.Read(window.ProcessId)
            .FirstOrDefault(candidate => candidate.Handle == window.Handle)?
            .Tabs;

    /// <summary>
    /// Waits for the tab at <paramref name="index"/> to be the selected one. This is the whole
    /// reason a colour lands on the tab it was meant for: focusing is a request, not a result.
    /// </summary>
    private bool WaitForSelection((IntPtr Handle, int ProcessId) window, int index)
    {
        var deadline = DateTime.UtcNow + FocusTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var tabs = Tabs(window);

            if (tabs is not null && index < tabs.Count && tabs[index].IsSelected) return true;

            Thread.Sleep(FocusPoll);
        }

        _log.Warn($"Tab {index} was not coloured: it never became the selected tab.");
        return false;
    }

    private bool RequestFocus(string windowName, int index)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // --target, not --index: wt.exe rejects the latter and the tab quietly never moves.
        foreach (var argument in new[] { "-w", windowName, "focus-tab", "--target", index.ToString() })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;

            // wt.exe hands the request to the running terminal and exits; waiting for it is not
            // waiting for the tab to change, which is what WaitForSelection is for.
            process.WaitForExit((int)FocusTimeout.TotalMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or ObjectDisposedException or SystemException)
        {
            _log.Warn($"Could not focus tab {index} to colour it. {ex.Message}");
            return false;
        }
    }

    private static bool IsTerminal(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;

        try
        {
            using var process = Process.GetProcessById(KeyboardInput.ProcessIdOf(window));
            return string.Equals(process.ProcessName, TerminalProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
