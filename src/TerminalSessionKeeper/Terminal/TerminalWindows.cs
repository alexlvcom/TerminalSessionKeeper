using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Terminal;

/// <summary>
/// One tab as UI Automation sees it: its title, where it is, and whether it is the active tab.
/// The geometry and the selection state are what make its colour readable.
/// </summary>
public sealed record TabElement(string Title, Rectangle Bounds, bool IsSelected);

/// <summary>One top-level Windows Terminal window and the tabs in it.</summary>
public sealed record TerminalWindow(IntPtr Handle, string? Title, Rectangle Bounds, IReadOnlyList<TabElement> Tabs);

/// <summary>
/// Reads the on-screen state of a Windows Terminal process through UI Automation.
///
/// UIA is the only way to see a tab's title — no setting, file or API exposes it — and since
/// Windows Terminal 1.18 every window of the app lives in one process, so this walks all of that
/// process's top-level windows rather than the single handle MainWindowHandle happens to name.
///
/// Best effort throughout: UIA needs the window to be responsive, and a failure costs the
/// snapshot its titles, never its session data.
/// </summary>
public sealed class TerminalWindows
{
    private readonly ILog _log;

    public TerminalWindows(ILog log) => _log = log;

    public IReadOnlyList<TerminalWindow> Read(int processId)
    {
        try
        {
            var root = AutomationElement.RootElement;
            if (root is null) return Array.Empty<TerminalWindow>();

            var ofProcess = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            var isTab = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

            var windows = new List<TerminalWindow>();

            foreach (AutomationElement window in root.FindAll(TreeScope.Children, ofProcess))
            {
                var tabs = new List<TabElement>();

                foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, isTab))
                {
                    var tab = Describe(element);
                    if (tab is not null) tabs.Add(tab);
                }

                windows.Add(new TerminalWindow(
                    SafeHandle(window), SafeName(window), SafeBounds(window), tabs));
            }

            return windows;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            _log.Warn($"Could not read the terminal windows of process {processId}. {ex.Message}");
            return Array.Empty<TerminalWindow>();
        }
    }

    private static TabElement? Describe(AutomationElement element)
    {
        try
        {
            var name = element.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var selected = false;
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                && pattern is SelectionItemPattern selection)
            {
                selected = selection.Current.IsSelected;
            }

            return new TabElement(name, ToRectangle(element.Current.BoundingRectangle), selected);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            // A tab closed between the enumeration and the read.
            return null;
        }
    }

    private static IntPtr SafeHandle(AutomationElement element)
    {
        try
        {
            return new IntPtr(element.Current.NativeWindowHandle);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return IntPtr.Zero;
        }
    }

    private static Rectangle SafeBounds(AutomationElement element)
    {
        try
        {
            return ToRectangle(element.Current.BoundingRectangle);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return Rectangle.Empty;
        }
    }

    private static string? SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name;
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return null;
        }
    }

    private static Rectangle ToRectangle(System.Windows.Rect rect)
    {
        if (double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
            || double.IsNaN(rect.Width) || double.IsNaN(rect.Height))
        {
            return Rectangle.Empty;
        }

        return new Rectangle((int)Math.Round(rect.X), (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
    }

    private static bool IsAutomationFailure(Exception ex) =>
        ex is ElementNotAvailableException or COMException or TimeoutException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException;
}
