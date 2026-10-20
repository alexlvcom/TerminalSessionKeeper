using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AlexLV.WindowsUi;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;

namespace TerminalSessionKeeper.Ui;

public enum UpdateState
{
    NotChecked,
    Checking,
    UpToDate,
    Available,
    Failed,
}

/// <summary>What the Updates tab's status card shows after a check.</summary>
public sealed record UpdateStatus(UpdateState State, string Title, string Detail)
{
    /// <summary>A current app: the card says so in place, with no dialog (shared Windows standard).</summary>
    public static UpdateStatus Latest(string version, DateTime checkedAtLocal) =>
        new(UpdateState.UpToDate, $"You're running the latest version (v{version})",
            $"Last checked {checkedAtLocal:yyyy-MM-dd HH:mm}");

    /// <summary>A failed check: the card shows the reason in place, with no dialog.</summary>
    public static UpdateStatus Failed(string version, string reason) =>
        new(UpdateState.Failed, "Could not check for updates", $"Running v{version} · {reason}");

    /// <summary>A newer release: the card offers Update, which opens the Update / Later dialog.</summary>
    public static UpdateStatus Available(string version, string newer, DateTime checkedAtLocal) =>
        new(UpdateState.Available, $"Version {newer} is available",
            $"You have v{version} · Last checked {checkedAtLocal:yyyy-MM-dd HH:mm}");

    /// <summary>
    /// The card on open: what the last recorded check actually found, never an assumed result.
    /// A newer version that is already the running one counts as current.
    /// </summary>
    public static UpdateStatus FromLastCheck(AppSettings settings, string currentVersion)
    {
        if (settings.LastUpdateCheckUtc is not { } checkedUtc)
            return new UpdateStatus(UpdateState.NotChecked, "Not checked yet", $"Version {currentVersion} · Use Check for updates to look now");

        var checkedAt = checkedUtc.ToLocalTime();
        switch (settings.LastUpdateOutcome)
        {
            case UpdateCheckOutcome.Current:
                return Latest(currentVersion, checkedAt);
            case UpdateCheckOutcome.Available
                when Version.TryParse(settings.LastUpdateAvailableVersion, out var newer)
                     && Version.TryParse(currentVersion, out var current):
                return newer > current
                    ? Available(currentVersion, newer.ToString(3), checkedAt)
                    : Latest(currentVersion, checkedAt);
            case UpdateCheckOutcome.Failed:
                var failed = Failed(currentVersion, settings.LastUpdateFailure ?? "The check did not finish");
                return failed with { Detail = $"{failed.Detail} · Last tried {checkedAt:yyyy-MM-dd HH:mm}" };
            default:
                // Settings from before 1.4.0 kept only the time, not what the check found.
                return new UpdateStatus(UpdateState.NotChecked, $"Last checked {checkedAt:yyyy-MM-dd HH:mm}",
                    $"Version {currentVersion} · Use Check for updates to see the result");
        }
    }
}

/// <summary>
/// Everything that is set once and then forgotten, grouped onto tabs that each fit without
/// scrolling. Nothing is stored until Save; Cancel, Esc or the title-bar X discards the draft. The
/// theme is the one live preview: it applies at once and is restored on any close without a
/// successful Save.
/// </summary>
public partial class SettingsWindow : ThemedWindow
{
    private static readonly UpdateCheckFrequency[] Frequencies =
    {
        UpdateCheckFrequency.Hourly, UpdateCheckFrequency.Daily, UpdateCheckFrequency.Weekly,
        UpdateCheckFrequency.Monthly, UpdateCheckFrequency.Never,
    };

    private readonly AppSettings _draft;
    private readonly AppSettings _original;
    private readonly Func<AppSettings, bool> _onSave;
    private readonly Func<Task<UpdateStatus>> _onCheckForUpdates;
    private readonly StartupManager _startupManager;
    private readonly bool _startupWasEnabled;
    private bool _saved;
    private bool _loading = true;

    public SettingsWindow(
        AppSettings draft,
        StartupManager startupManager,
        Func<AppSettings, bool> onSave,
        Func<Task<UpdateStatus>> onCheckForUpdates)
    {
        InitializeComponent();
        _draft = draft;
        _original = draft.Clone();
        _onSave = onSave;
        _onCheckForUpdates = onCheckForUpdates;
        _startupManager = startupManager;
        _startupWasEnabled = startupManager.IsEnabled;

        Title = $"{AppInfo.DisplayName} Settings (v{Changelog.MarketingVersion})";
        StartWithWindowsLabel.Text = $"Start {AppInfo.DisplayName} with Windows";

        AutoSnapshot.IsChecked = draft.AutoSnapshotEnabled;
        Interval.Text = draft.AutoSnapshotIntervalMinutes.ToString();
        Keep.Text = draft.EffectiveKeepSnapshots.ToString();
        RestoreAfterReboot.IsChecked = draft.RestoreAfterReboot;
        AutoResume.IsChecked = draft.AutoResume;
        WindowName.Text = draft.RestoreWindowName;
        WslEnvironment.Text = (draft.RestoreWslEnvironment ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
        TypeCd.IsChecked = draft.TypeCdIntoWslTabs;
        PinTitles.IsChecked = draft.PinTitles;
        SampleColors.IsChecked = draft.SampleTabColors;
        RestoreColors.IsChecked = draft.RestoreTabColors;
        Notifications.IsChecked = draft.ShowBalloonNotifications;
        StartWithWindows.IsChecked = _startupWasEnabled;
        Frequency.SelectedIndex = Math.Max(0, Array.IndexOf(Frequencies, UpdateSchedule.Normalize(draft.CheckFrequency)));
        (draft.Theme switch { AppTheme.Light => ThemeLight, AppTheme.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;

        foreach (var box in new[] { AutoSnapshot, RestoreAfterReboot, AutoResume, TypeCd, PinTitles, SampleColors, RestoreColors, Notifications, StartWithWindows })
        {
            box.Checked += (_, _) => OnEdited();
            box.Unchecked += (_, _) => OnEdited();
        }
        foreach (var text in new[] { Interval, Keep, WindowName, WslEnvironment }) text.TextChanged += (_, _) => OnEdited();
        Frequency.SelectionChanged += (_, _) => OnEdited();

        ShowUpdateStatus(UpdateStatus.FromLastCheck(draft, Changelog.MarketingVersion));

        BuildAbout();
        BuildDiagnostics();
        _loading = false;
        OnEdited();

        Loaded += (_, _) => SaveButton.Focus();
        Closing += OnClosing;
    }

    public void SelectTab(string name)
    {
        foreach (TabItem tab in Tabs.Items)
        {
            if (string.Equals(tab.Header as string, name, StringComparison.OrdinalIgnoreCase)) Tabs.SelectedItem = tab;
        }
    }

    private AppTheme SelectedTheme =>
        ThemeLight.IsChecked == true ? AppTheme.Light : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System;

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Apply(SelectedTheme);
        OnEdited();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row) return;
        var toggle = FindToggle(row);
        if (toggle is null || !toggle.IsEnabled) return;
        if (e.OriginalSource is DependencyObject source && IsInside(source, toggle)) return;
        toggle.IsChecked = toggle.IsChecked != true;
        toggle.Focus();
    }

    private static CheckBox? FindToggle(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CheckBox box) return box;
            if (FindToggle(child) is { } nested) return nested;
        }

        return null;
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor) return true;
        }

        return false;
    }

    /// <summary>Copies the controls into the draft and refreshes dependent state and the change count.</summary>
    private void OnEdited()
    {
        if (_loading) return;

        var intervalOn = AutoSnapshot.IsChecked == true;
        Interval.IsEnabled = intervalOn;
        IntervalLabel.Opacity = intervalOn ? 1 : 0.45;

        _draft.AutoSnapshotEnabled = intervalOn;
        _draft.RestoreAfterReboot = RestoreAfterReboot.IsChecked == true;
        _draft.AutoResume = AutoResume.IsChecked == true;
        _draft.TypeCdIntoWslTabs = TypeCd.IsChecked == true;
        _draft.PinTitles = PinTitles.IsChecked == true;
        _draft.SampleTabColors = SampleColors.IsChecked == true;
        _draft.RestoreTabColors = RestoreColors.IsChecked == true;
        _draft.ShowBalloonNotifications = Notifications.IsChecked == true;
        _draft.RestoreWslEnvironment = WslEnvironment.Text.Replace("\r\n", "\n").Trim();
        var windowName = WindowName.Text.Trim();
        if (windowName.Length > 0) _draft.RestoreWindowName = windowName;
        _draft.CheckFrequency = Frequencies[Math.Max(0, Frequency.SelectedIndex)];
        _draft.Theme = SelectedTheme;
        if (TryReadInterval(out var minutes)) _draft.AutoSnapshotIntervalMinutes = minutes;
        if (TryReadKeep(out var keep)) _draft.KeepSnapshots = keep;
        if (IntervalError.Visibility == Visibility.Visible && TryReadInterval(out _)) ShowError(Interval, IntervalError, false);
        if (KeepError.Visibility == Visibility.Visible && TryReadKeep(out _)) ShowError(Keep, KeepError, false);

        var changes = 0;
        if ((StartWithWindows.IsChecked == true) != _startupWasEnabled) changes++;
        if (_draft.AutoSnapshotEnabled != _original.AutoSnapshotEnabled) changes++;
        if (Interval.Text.Trim() != _original.AutoSnapshotIntervalMinutes.ToString()) changes++;
        if (Keep.Text.Trim() != _original.EffectiveKeepSnapshots.ToString()) changes++;
        if (_draft.RestoreAfterReboot != _original.RestoreAfterReboot) changes++;
        if (_draft.AutoResume != _original.AutoResume) changes++;
        if (_draft.RestoreWindowName != _original.RestoreWindowName) changes++;
        if (_draft.RestoreWslEnvironment != (_original.RestoreWslEnvironment ?? string.Empty).Replace("\r\n", "\n").Trim()) changes++;
        if (_draft.TypeCdIntoWslTabs != _original.TypeCdIntoWslTabs) changes++;
        if (_draft.PinTitles != _original.PinTitles) changes++;
        if (_draft.SampleTabColors != _original.SampleTabColors) changes++;
        if (_draft.RestoreTabColors != _original.RestoreTabColors) changes++;
        if (_draft.ShowBalloonNotifications != _original.ShowBalloonNotifications) changes++;
        if (_draft.CheckFrequency != UpdateSchedule.Normalize(_original.CheckFrequency)) changes++;
        if (_draft.Theme != _original.Theme) changes++;
        DirtyText.Text = changes switch { 0 => "", 1 => "1 unsaved change", _ => $"{changes} unsaved changes" };
    }

    private bool TryReadInterval(out int minutes) =>
        int.TryParse(Interval.Text.Trim(), out minutes)
        && minutes is >= AppSettings.MinSnapshotIntervalMinutes and <= AppSettings.MaxSnapshotIntervalMinutes;

    private bool TryReadKeep(out int keep) =>
        int.TryParse(Keep.Text.Trim(), out keep) && keep is >= 1 and <= 500;

    private static void ShowError(TextBox box, TextBlock error, bool show)
    {
        error.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) box.SetResourceReference(StyleProperty, "InvalidTextBox");
        else box.ClearValue(StyleProperty);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Out-of-range numbers are reported beside their field instead of being clamped silently.
        var intervalOk = TryReadInterval(out _);
        var keepOk = TryReadKeep(out _);
        if (!intervalOk || !keepOk)
        {
            Tabs.SelectedItem = SnapshotsTab;
            ShowError(Interval, IntervalError, !intervalOk);
            ShowError(Keep, KeepError, !keepOk);
            var bad = intervalOk ? Keep : Interval;
            bad.Focus();
            bad.SelectAll();
            return;
        }

        OnEdited();
        if (!_onSave(_draft))
        {
            MessageDialog.Show($"Could not save settings to {AppPaths.SettingsFile}. Your changes are still open in this window; check the log, then try Save again.",
                MessageKind.Error, owner: this);
            return;
        }

        _saved = true;

        // Autostart lives in the registry rather than settings.json, so it is applied here.
        var wantStartup = StartWithWindows.IsChecked == true;
        if (wantStartup != _startupManager.IsEnabled && !_startupManager.TrySetEnabled(wantStartup))
        {
            MessageDialog.Show("Settings were saved, but the startup setting could not be changed. See the log for details.",
                MessageKind.Warning, owner: this);
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Any close without a successful Save restores the saved theme.
        if (!_saved && ThemeManager.Choice != _original.Theme) ThemeManager.Apply(_original.Theme);
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
        ShowUpdateStatus(new UpdateStatus(UpdateState.Checking, "Checking for updates…", "Contacting GitHub releases"));
        try
        {
            ShowUpdateStatus(await _onCheckForUpdates());
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = true;
        }
    }

    private void ShowUpdateStatus(UpdateStatus status)
    {
        // A recorded newer release offers Update; a manual check of it opens the Update / Later dialog.
        UpdateNowButton.Visibility = status.State == UpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle.Text = status.Title;
        UpdateDetail.Text = status.Detail;
        UpdateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, status.State switch
        {
            UpdateState.Failed => "Danger",
            UpdateState.Available or UpdateState.Checking => "Accent",
            UpdateState.NotChecked => "Muted",
            _ => "Ok",
        });
    }

    private void BuildAbout()
    {
        AboutIcon.Source = ThemeManager.WindowIcon;
        AboutTitle.Text = $"{AppInfo.DisplayName} v{Changelog.MarketingVersion}";
        AboutBuild.Text = Changelog.BuildDate == DateTime.MinValue
            ? $"Build {Changelog.BuildVersion} · by alexlvcom · "
            : $"Build {Changelog.BuildVersion} · built {Changelog.BuildDate:yyyy-MM-dd HH:mm} · by alexlvcom · ";
        NotesHeader.Text = $"Release notes for v{Changelog.MarketingVersion}:";
        ReleaseNotesView.Fill(NotesPanel, Changelog.Current);
    }

    private void BuildDiagnostics()
    {
        var rows = new (string Label, string Path, string Folder)[]
        {
            ("Executable", Environment.ProcessPath ?? "", Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ""),
            ("Settings", AppPaths.SettingsFile, Path.GetDirectoryName(AppPaths.SettingsFile) ?? ""),
            ("Snapshots", AppPaths.SnapshotDirectory, AppPaths.SnapshotDirectory),
            ("Logs", AppPaths.LogDirectory, AppPaths.LogDirectory),
        };

        for (var i = 0; i < rows.Length; i++)
        {
            var (label, path, folder) = rows[i];
            var row = new Border();
            row.SetResourceReference(StyleProperty, i == 0 ? "FirstCardRow" : "CardRow");
            var stack = new StackPanel();
            var caption = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) };
            caption.SetResourceReference(StyleProperty, "MutedText");
            stack.Children.Add(caption);

            var line = new DockPanel();
            var open = new Button { Content = "Open", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            open.Click += (_, _) => OpenFolder(folder, this);
            DockPanel.SetDock(open, Dock.Right);
            line.Children.Add(open);
            var box = new TextBox { Text = path };
            box.SetResourceReference(StyleProperty, "PathBox");
            line.Children.Add(box);
            stack.Children.Add(line);
            row.Child = stack;
            PathsPanel.Children.Add(row);
        }
    }

    private void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/alexlvcom/TerminalSessionKeeper");

    private void History_Click(object sender, RoutedEventArgs e) => new HistoryWindow { Owner = this }.ShowDialog();

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        var details = $"{AppInfo.DisplayName} v{Changelog.MarketingVersion} (Build {Changelog.BuildVersion})\n" +
                      $"Built: {(Changelog.BuildDate == DateTime.MinValue ? "Unknown" : Changelog.BuildDate.ToString("yyyy-MM-dd HH:mm"))}\n" +
                      $"Executable: {Environment.ProcessPath}\n" +
                      $"Settings: {AppPaths.SettingsFile}\n" +
                      $"Snapshots: {AppPaths.SnapshotDirectory}\n" +
                      $"Logs: {AppPaths.LogDirectory}\n" +
                      $"Theme: {ThemeManager.Choice} ({(ThemeManager.IsDark ? "dark" : "light")})\n" +
                      "Repository: https://github.com/alexlvcom/TerminalSessionKeeper\n\n" +
                      $"Release notes (v{Changelog.MarketingVersion}):\n{Changelog.GetReleaseNotes(Changelog.MarketingVersion)}";
        try
        {
            Clipboard.SetText(details);
            MessageDialog.Show("Diagnostic details copied to clipboard.", MessageKind.Success, owner: this);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            MessageDialog.Show($"Could not copy to the clipboard.\n\n{ex.Message}", MessageKind.Warning, owner: this);
        }
    }

    internal static void OpenFolder(string path, Window? owner)
    {
        try
        {
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show($"Failed to open folder: {ex.Message}", MessageKind.Error, owner: owner);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageDialog.Show($"Could not open {url}.\n\n{ex.Message}", MessageKind.Warning);
        }
    }
}
