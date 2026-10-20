using System.Drawing;
using System.Windows.Forms;
using AlexLV.WindowsUi;
using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Restore;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Snapshots;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// Owns the notification-area icon and its menu. There is no main window: work happens on a menu
/// command, on the snapshot timer, or once at startup when a reboot left tabs to rebuild.
///
/// The menu holds only what is worth reaching for in a hurry — two actions and two windows.
/// Everything that is set once lives in Settings, and everything about a particular saved
/// snapshot lives in the Snapshots window, where it can be seen before it is acted on. The tray
/// stays WinForms (NotifyIcon and its message loop); every window is WPF with the shared kit.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>Terminal Session Keeper's accent, the green of the prompt in its icon.</summary>
    public static readonly AccentColors Accent = AccentColors.FromHex("#1D7347", "#78E0A0", onDark: "#0B1F14");

    private readonly SnapshotService _snapshots;
    private readonly SnapshotStore _store;
    private readonly RestoreService _restore;
    private readonly StartupManager _startupManager;
    private readonly SettingsStore _settingsStore;
    private readonly ILog _log;
    private readonly Icon _icon;

    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenuStyler _menuStyler;
    private readonly System.Windows.Forms.Timer _snapshotTimer;

    /// <summary>Owns the UI thread's handle, so background work can marshal back onto it.</summary>
    private readonly Control _sync;

    private AppSettings _settings;
    private SnapshotsWindow? _snapshotsWindow;
    private SettingsWindow? _settingsWindow;
    private readonly ShutdownListenerWindow _shutdownListener;
    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private DateTime _nextUpdateCheckUtc = DateTime.MaxValue;
    private bool _checkingUpdate;
    private Version? _lastOfferedUpdate;

    /// <summary>True while a snapshot or a restore is running, or a dialog is open.</summary>
    private volatile bool _busy;

    public TrayApplicationContext(
        SnapshotService snapshots,
        SnapshotStore store,
        RestoreService restore,
        StartupManager startupManager,
        SettingsStore settingsStore,
        ILog log)
    {
        _snapshots = snapshots;
        _store = store;
        _restore = restore;
        _startupManager = startupManager;
        _settingsStore = settingsStore;
        _log = log;
        _settings = settingsStore.Load();
        _icon = AppIcon.Load();

        ThemeManager.Initialize(AppInfo.DisplayName, Accent, _settings.Theme);
        ThemeManager.WindowIcon = WinFormsHosting.ToImageSource(_icon);

        _sync = new Control();
        _sync.CreateControl();

        var snapshotItem = new ToolStripMenuItem("&Snapshot now", null, (_, _) => RunSnapshot(manual: true));
        var restoreItem = new ToolStripMenuItem("&Restore last snapshot", null, (_, _) => RestoreLatest());
        var snapshotsItem = new ToolStripMenuItem("S&napshots…", null, (_, _) => ShowSnapshots());
        var settingsItem = new ToolStripMenuItem("Se&ttings…", null, (_, _) => ShowSettings());
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => ExitApplication());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            snapshotItem,
            restoreItem,
            new ToolStripSeparator(),
            snapshotsItem,
            settingsItem,
            new ToolStripSeparator(),
            exitItem,
        });

        _menuStyler = new TrayMenuStyler(menu);
        _menuStyler.SetGlyph(snapshotItem, "");
        _menuStyler.SetGlyph(restoreItem, "");
        _menuStyler.SetGlyph(snapshotsItem, "");
        _menuStyler.SetGlyph(settingsItem, "");
        _menuStyler.SetGlyph(exitItem, "");
        // Double-clicking the icon opens this window, so it is drawn as the default.
        snapshotsItem.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = TooltipText(),
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowSnapshots();
        _notifyIcon.BalloonTipClicked += (_, _) => ShowSnapshots();

        // A WinForms timer ticks on the UI thread, so the tick itself needs no marshalling; the
        // work it starts is handed straight to a worker.
        _snapshotTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)_settings.SnapshotInterval.TotalMilliseconds,
            Enabled = _settings.AutoSnapshotEnabled,
        };

        _snapshotTimer.Tick += (_, _) =>
        {
            if (!_busy) RunSnapshot(manual: false);
        };

        _log.Info(_settings.AutoSnapshotEnabled
            ? $"Automatic snapshots are on, every {DescribeInterval()}."
            : "Automatic snapshots are off.");

        _shutdownListener = new ShutdownListenerWindow(Shutdown);
        _shutdownListener.Show();

        _updateTimer.Interval = (int)TimeSpan.FromMinutes(15).TotalMilliseconds;
        _updateTimer.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow >= _nextUpdateCheckUtc)
            {
                await CheckForUpdatesAsync(manual: false);
                _nextUpdateCheckUtc = UpdateSchedule.NextCheck(DateTime.UtcNow, _settings.CheckFrequency);
            }
        };

        ApplyUpdateSchedule();
        if (_settings.CheckFrequency != UpdateCheckFrequency.Never)
        {
            _ = CheckForUpdatesAsync(manual: false);
        }

        ConsiderAutoRestore();
    }

    /// <summary>Entry point for the single-instance listener; a second launch shows the snapshots.</summary>
    public void ActivateFromOtherInstance() => OnUiThread(ShowSnapshots);

    // ---------------------------------------------------------------- snapshot

    private void RunSnapshot(bool manual)
    {
        RunInBackground(() =>
        {
            var outcome = _snapshots.CaptureAndSave(_settings);

            OnUiThread(() =>
            {
                // A manual snapshot always reports back. An automatic one speaks up only when
                // something went wrong — a balloon every ten minutes would be noise.
                if (manual)
                {
                    Notify(outcome.Summary, outcome.Captured ? ToolTipIcon.Info : ToolTipIcon.Warning);
                }
                else if (!outcome.Captured && outcome.Snapshot is null)
                {
                    Notify(outcome.Summary, ToolTipIcon.Error);
                }

                if (_snapshotsWindow is { IsVisible: true }) _snapshotsWindow.Reload();
            });
        });
    }

    // ----------------------------------------------------------------- restore

    private void RestoreLatest()
    {
        var snapshot = _store.Load();
        if (snapshot is null || snapshot.TabCount == 0)
        {
            Warn("There is no saved snapshot to restore yet.");
            return;
        }

        StartRestore(snapshot, "the last snapshot");
    }

    /// <summary>Restores a snapshot, after checking the one thing that would make it a mistake.</summary>
    private void StartRestore(SnapshotRecord snapshot, string label)
    {
        if (RestoreService.AnyTerminalHasTabs())
        {
            // The trap worth warning about: restoring alongside a live window means the next
            // snapshot captures both, and the restore after that rebuilds twice as many tabs.
            var answer = Ask(
                $"A Windows Terminal window is already open.{Environment.NewLine}{Environment.NewLine}" +
                $"Restoring will open a second window with {snapshot.TabCount} more tab(s). The next " +
                "snapshot would then capture both windows together." + Environment.NewLine + Environment.NewLine +
                "Restore anyway?");

            if (answer != MessageResult.Yes) return;
        }

        _log.Info($"Restoring {snapshot.TabCount} tab(s) from {label}.");

        RunInBackground(() =>
        {
            var outcome = _restore.Restore(snapshot, _settings);
            OnUiThread(() => Notify(outcome.Summary, ToolTipIcon.Info));
        });
    }

    private string PreviewText(SnapshotRecord snapshot)
    {
        var outcome = _restore.DryRun(snapshot, _settings);
        return string.Join(Environment.NewLine,
            new[] { outcome.Summary, string.Empty }.Concat(outcome.Commands));
    }

    /// <summary>
    /// Runs once at startup. Everything about this is deliberately conservative: the guard is
    /// what keeps a restore from landing on top of a session the user is still sitting in.
    /// </summary>
    private void ConsiderAutoRestore()
    {
        var snapshot = _store.Load();
        if (snapshot is null)
        {
            // Worth saying out loud: "why did it not restore?" is the first question anyone asks
            // after a reboot, and an empty snapshots folder is the commonest answer.
            _log.Info("Not restoring automatically: there is no saved snapshot yet.");
            return;
        }

        if (!_restore.ShouldAutoRestore(snapshot, _settings, out var reason))
        {
            _log.Info($"Not restoring automatically: {reason}.");
            return;
        }

        _log.Info($"Restoring {snapshot.TabCount} tab(s) automatically: {reason}.");

        // Recorded before the work starts, so a crash mid-restore cannot turn into a second
        // automatic restore on the next launch.
        _settings.LastAutoRestoreBootTimeUtc = BootTime.Current();
        _settingsStore.Save(_settings);

        RunInBackground(() =>
        {
            var outcome = _restore.Restore(snapshot, _settings);
            OnUiThread(() => Notify(outcome.Summary, ToolTipIcon.Info));
        });
    }

    // -------------------------------------------------------------------- ui

    private void ShowSnapshots()
    {
        if (_snapshotsWindow is not null)
        {
            _snapshotsWindow.Reload();
            WinFormsHosting.ShowModeless(_snapshotsWindow);
            return;
        }

        _snapshotsWindow = new SnapshotsWindow(_store, PreviewText, StartRestore);
        _snapshotsWindow.Closed += (_, _) => _snapshotsWindow = null;
        WinFormsHosting.ShowModeless(_snapshotsWindow);
    }

    private void ShowSettings(string? initialTab = null)
    {
        if (_settingsWindow is not null)
        {
            if (initialTab != null) _settingsWindow.SelectTab(initialTab);
            _settingsWindow.Activate();
            return;
        }

        // A copy, so Cancel really cancels. Save hands the draft to ApplySettings and closes the
        // window only when it was written, so a failed save keeps the user's edits on screen.
        var settingsWindow = new SettingsWindow(_settings.Clone(), _startupManager, ApplySettings,
            () => CheckForUpdatesAsync(manual: true));
        if (initialTab != null) settingsWindow.SelectTab(initialTab);
        _settingsWindow = settingsWindow;
        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, settingsWindow)) _settingsWindow = null;
        };

        // Modal: the settings decide what the timer and a restore do, so nothing else should be
        // running against a half-edited copy.
        _busy = true;

        try
        {
            settingsWindow.ShowDialog();
        }
        finally
        {
            _busy = false;
        }
    }

    private bool ApplySettings(AppSettings settings)
    {
        // Bookkeeping the app wrote while the window was open is not part of the draft.
        settings.LastUpdateCheckUtc = _settings.LastUpdateCheckUtc;
        settings.SkippedUpdateVersion = _settings.SkippedUpdateVersion;
        settings.LastAutoRestoreBootTimeUtc = _settings.LastAutoRestoreBootTimeUtc;
        if (!_settingsStore.Save(settings)) return false;

        _settings = settings;
        ApplyUpdateSchedule();

        _snapshotTimer.Enabled = false;
        _snapshotTimer.Interval = (int)_settings.SnapshotInterval.TotalMilliseconds;
        _snapshotTimer.Enabled = _settings.AutoSnapshotEnabled;

        _notifyIcon.Text = TooltipText();
        ThemeManager.Apply(_settings.Theme);

        _log.Info(_settings.AutoSnapshotEnabled
            ? $"Automatic snapshots are on, every {DescribeInterval()}."
            : "Automatic snapshots are off.");
        return true;
    }

    // -------------------------------------------------------------- plumbing

    /// <summary>
    /// A snapshot talks to WSL and a restore sleeps between tab launches, so neither belongs on
    /// the UI thread — the tray menu has to stay answerable throughout.
    /// </summary>
    private void RunInBackground(Action work)
    {
        if (_busy)
        {
            _log.Debug("Ignoring the request: another snapshot or restore is already running.");
            return;
        }

        _busy = true;

        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _log.Error("Background work failed.", ex);
                OnUiThread(() => Notify("Something went wrong. See the log for details.", ToolTipIcon.Error));
            }
            finally
            {
                _busy = false;
            }
        })
        {
            IsBackground = true,
            Name = "TerminalSessionKeeper.Work",
        };

        // UI Automation is happiest off the STA message pump, and nothing here touches WinForms
        // without marshalling first.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void OnUiThread(Action action)
    {
        try
        {
            if (_sync.IsDisposed) return;

            if (_sync.InvokeRequired)
            {
                _sync.BeginInvoke(action);
                return;
            }

            action();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The app is shutting down; the result has nowhere left to go.
        }
    }

    private void Notify(string message, ToolTipIcon iconKind)
    {
        if (!_settings.ShowBalloonNotifications) return;
        _notifyIcon.ShowBalloonTip(5000, AppInfo.DisplayName, message, iconKind);
    }

    private void Warn(string message)
    {
        // A modal dialog pumps messages, so hold the timer off while it is up.
        _busy = true;

        try
        {
            MessageDialog.Show(message, MessageKind.Info);
        }
        finally
        {
            _busy = false;
        }
    }

    private MessageResult Ask(string message)
    {
        _busy = true;

        try
        {
            return MessageDialog.Show(message, MessageKind.Warning, MessageButtons.YesNo, defaultIsNegative: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private string DescribeInterval()
    {
        var minutes = (int)_settings.SnapshotInterval.TotalMinutes;
        return minutes == 1 ? "minute" : $"{minutes} minutes";
    }

    private string TooltipText()
    {
        var text = $"{AppInfo.DisplayName} v{Changelog.MarketingVersion}";
        if (!_settings.AutoSnapshotEnabled) return text;

        var withInterval = $"{text} — every {DescribeInterval()}";

        // NotifyIcon.Text rejects anything longer than 63 characters.
        return withInterval.Length <= 63 ? withInterval : text;
    }

    public void Shutdown()
    {
        _log.Info("Shutdown requested by listener.");
        ExitApplication();
    }

    private void ApplyUpdateSchedule()
    {
        _settings.CheckFrequency = UpdateSchedule.Normalize(_settings.CheckFrequency);
        _updateTimer.Stop();
        _nextUpdateCheckUtc = UpdateSchedule.NextCheck(DateTime.UtcNow, _settings.CheckFrequency);
        if (_settings.CheckFrequency != UpdateCheckFrequency.Never) _updateTimer.Start();
    }

    /// <summary>Checks GitHub. Manual checks report every outcome; automatic ones only offer an update.</summary>
    private async Task<UpdateStatus> CheckForUpdatesAsync(bool manual)
    {
        var current = Changelog.MarketingVersion;
        if (_checkingUpdate) return new UpdateStatus(UpdateState.Checking, "Checking for updates…", "A check is already running");
        if (!manual && _settings.CheckFrequency == UpdateCheckFrequency.Never)
        {
            return new UpdateStatus(UpdateState.NotChecked, "Automatic checks are off", $"Version {current}");
        }

        _checkingUpdate = true;
        try
        {
            var update = await _updates.CheckAsync(Version.Parse(current), _updateCancellation.Token);
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settingsStore.Save(_settings);
            var checkedAt = $"Last checked {DateTime.Now:yyyy-MM-dd HH:mm}";

            if (update is null)
            {
                if (manual) MessageDialog.Show($"You are running version {current}.\n\nNo newer installer version is available.", MessageKind.Info);
                return new UpdateStatus(UpdateState.UpToDate, $"{AppInfo.DisplayName} is up to date", $"Version {current} · {checkedAt}");
            }

            var available = new UpdateStatus(UpdateState.Available, $"Version {update.Version.ToString(3)} is available",
                $"You have {current} · {checkedAt}");
            if (!manual && _lastOfferedUpdate == update.Version) return available;
            _lastOfferedUpdate = update.Version;
            ShowUpdateDialog(update);
            return available;
        }
        catch (OperationCanceledException)
        {
            return new UpdateStatus(UpdateState.Failed, "Update check stopped", $"Version {current}");
        }
        catch (Exception ex)
        {
            _log.Error("Update check failed.", ex);
            if (manual) MessageDialog.Show($"Could not check for updates. Please try again later.\n\n{ex.Message}", MessageKind.Warning);
            return new UpdateStatus(UpdateState.Failed, "Could not check for updates", $"Version {current} · {ex.Message}");
        }
        finally { _checkingUpdate = false; }
    }

    private void ShowUpdateDialog(AppUpdate update)
    {
        var wasBusy = _busy;
        _busy = true;
        try
        {
            new UpdateWindow(update, _updates, _updateCancellation.Token).ShowDialog();
        }
        finally
        {
            _busy = wasBusy;
        }
    }

    private void ExitApplication()
    {
        _log.Info("Exiting.");
        _snapshotTimer.Stop();
        _notifyIcon.Visible = false;
        _settingsWindow?.Close();
        _snapshotsWindow?.Close();
        ExitThread();
    }

    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        // A crash path can dispose twice (ExitThread, then Program.Main); cancelling a disposed source throws.
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _updateCancellation.Cancel();
            _updateCancellation.Dispose();
            _updates.Dispose();
            _shutdownListener.Dispose();
            _snapshotTimer.Stop();
            _snapshotTimer.Dispose();
            _notifyIcon.Visible = false;
            _menuStyler.Dispose();
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _sync.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
