using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlexLV.WindowsUi;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Snapshots;
using TerminalSessionKeeper.Ui;

namespace TerminalSessionKeeper.Tests;

/// <summary>
/// Opens the real WPF windows off screen, in both themes. Catches what a unit test of the settings
/// model cannot: a window that throws while it lays out, and a Settings tab that would scroll.
/// </summary>
public sealed class WindowTests
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Every_Settings_tab_fits_without_scrolling(AppTheme theme) => OnSta(() =>
    {
        ThemeManager.Initialize(AppInfo.DisplayName, TrayApplicationContext.Accent, theme);
        var window = new SettingsWindow(new AppSettings(), new StartupManager(NullLog.Instance),
            _ => true, () => Task.FromResult(new UpdateStatus(UpdateState.UpToDate, "", "")));
        try
        {
            ShowOffScreen(window);
            var tabs = FindChild<TabControl>(window)!;

            // A second header row would reorder the tabs whenever another row's tab is picked.
            var tops = tabs.Items.Cast<TabItem>().Select(t => Math.Round(t.TranslatePoint(new Point(0, 0), tabs).Y)).Distinct().ToList();
            Assert.True(tops.Count == 1, $"Tab headers wrap onto {tops.Count} rows");

            foreach (TabItem tab in tabs.Items)
            {
                tabs.SelectedItem = tab;
                window.UpdateLayout();
                var content = (FrameworkElement)tab.Content;
                var room = ((FrameworkElement)VisualTreeHelper.GetParent(content)).ActualHeight;
                content.Measure(new Size(content.ActualWidth, double.PositiveInfinity));
                Assert.True(content.DesiredSize.Height <= room + 0.5,
                    $"{tab.Header} needs {content.DesiredSize.Height:0}px but has {room:0}px");
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_other_windows_open_and_lay_out() => OnSta(() =>
    {
        ThemeManager.Initialize(AppInfo.DisplayName, TrayApplicationContext.Accent, AppTheme.Dark);
        using var temp = new TempDirectory();
        var store = new SnapshotStore(NullLog.Instance, temp.Path);
        using var updates = new UpdateService();
        var update = new AppUpdate(new Version(9, 9, 9), new Uri("https://github.com/alexlvcom/TerminalSessionKeeper/releases/download/v9.9.9/TerminalSessionKeeper-Setup.exe"), null, new string('0', 64));

        foreach (var window in new Window[]
                 {
                     new SnapshotsWindow(store, _ => "preview", (_, _) => { }),
                     new HistoryWindow(),
                     new UpdateWindow(update, updates, CancellationToken.None),
                 })
        {
            ShowOffScreen(window);
            Assert.True(window.ActualWidth > 0);
            window.Close();
        }
    });

    private static void ShowOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// WPF windows need an STA thread, and the one WPF Application belongs to the thread that
    /// created it, so every window test runs on one long-lived dispatcher thread.
    /// </summary>
    private static readonly Lazy<System.Windows.Threading.Dispatcher> Sta = new(() =>
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "WindowTests.Sta" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        Sta.Value.Invoke(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        failure?.Throw();
    }
}
