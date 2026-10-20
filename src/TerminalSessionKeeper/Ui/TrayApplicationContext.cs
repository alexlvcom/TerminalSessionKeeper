using System.Drawing;
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
/// snapshot lives in the Snapshots window, where it can be seen before it is acted on.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SnapshotService _snapshots;
    private readonly SnapshotStore _store;
    private readonly RestoreService _restore;
    private readonly StartupManager _startupManager;
    private readonly SettingsStore _settingsStore;
    private readonly ILog _log;
    private readonly Icon _icon;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _snapshotTimer;

    /// <summary>Owns the UI thread's handle, so background work can marshal back onto it.</summary>
    private readonly Control _sync;

    private AppSettings _settings;
    private SnapshotsForm? _snapshotsForm;
    private SettingsForm? _settingsForm;
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

        _sync = new Control();
        _sync.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("&Snapshot now", null, (_, _) => RunSnapshot(manual: true)),
            new ToolStripMenuItem("&Restore last snapshot", null, (_, _) => RestoreLatest()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("S&napshots…", null, (_, _) => ShowSnapshots()),
            new ToolStripMenuItem("Se&ttings…", null, (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("E&xit", null, (_, _) => ExitApplication()),
        });

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

                if (_snapshotsForm is { IsDisposed: false, Visible: true }) _snapshotsForm.Reload();
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

            if (answer != DialogResult.Yes) return;
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
        if (_snapshotsForm is { IsDisposed: false })
        {
            _snapshotsForm.Reload();
            _snapshotsForm.Show();
            _snapshotsForm.WindowState = FormWindowState.Normal;
            _snapshotsForm.Activate();
            return;
        }

        _snapshotsForm = new SnapshotsForm(_store, PreviewText, StartRestore, _icon);
        _snapshotsForm.FormClosed += (_, _) => _snapshotsForm = null;
        _snapshotsForm.Show();
        _snapshotsForm.Activate();
    }

    private void ShowSettings(bool selectAboutTab = false)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (selectAboutTab) _settingsForm.SelectAboutTab();
            _settingsForm.Activate();
            return;
        }

        // A copy, so Cancel really cancels.
        var draft = _settingsStore.Load();

        // Save hands the draft to ApplySettings and closes the window only when it was written,
        // so a failed save keeps the user's edits on screen.
        var settingsForm = new SettingsForm(draft, _startupManager, _icon, ApplySettings,
            () => CheckForUpdatesAsync(manual: true));
        if (selectAboutTab) settingsForm.SelectAboutTab();
        _settingsForm = settingsForm;
        settingsForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, settingsForm)) _settingsForm = null;
        };

        // Modal: the settings decide what the timer and a restore do, so nothing else should be
        // running against a half-edited copy.
        _busy = true;

        try
        {
            settingsForm.ShowDialog();
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
            MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _busy = false;
        }
    }

    private DialogResult Ask(string message)
    {
        _busy = true;

        try
        {
            return MessageBox.Show(message, AppInfo.DisplayName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
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

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdate || (!manual && _settings.CheckFrequency == UpdateCheckFrequency.Never)) return;
        _checkingUpdate = true;
        try
        {
            var update = await _updates.CheckAsync(Version.Parse(Changelog.MarketingVersion), _updateCancellation.Token);
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settingsStore.Save(_settings);

            if (update is null)
            {
                if (manual) MessageBox.Show($"You are running version {Changelog.MarketingVersion}.\n\nNo newer installer version is available.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!manual && _lastOfferedUpdate == update.Version) return;
            _lastOfferedUpdate = update.Version;
            ShowUpdateDialog(update);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error("Update check failed.", ex);
            if (manual) MessageBox.Show($"Could not check for updates. Please try again later.\n\n{ex.Message}", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _checkingUpdate = false; }
    }

    private void ShowUpdateDialog(AppUpdate update)
    {
        using var dialog = new Form
        {
            Text = $"Update {AppInfo.DisplayName} (v{Changelog.MarketingVersion})",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            ClientSize = new Size(470, 200),
            Icon = _icon,
        };
        var message = new Label
        {
            Text = $"Version {update.Version} is available. You have {Changelog.MarketingVersion}.\n\nUpdate will download and open the installer. Setup will close the app and restart it after updating.",
            Location = new Point(18, 18),
            Size = new Size(434, 115),
        };
        var install = new Button { Text = "Update", Location = new Point(268, 152), Size = new Size(88, 30) };
        var later = new Button { Text = "Later", Location = new Point(364, 152), Size = new Size(88, 30), DialogResult = DialogResult.Cancel };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
        dialog.FormClosing += (_, _) => cancellation.Cancel();
        install.Click += async (_, _) =>
        {
            install.Enabled = false;
            message.Text = "Downloading and verifying the installer…";
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            attempt.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                var cache = Path.Combine(AppPaths.Root, "Updates");
                var installer = await _updates.DownloadAsync(update, cache, attempt.Token);
                if (dialog.IsDisposed) return;
                _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installer,
                    UseShellExecute = true,
                }) ?? throw new InvalidOperationException("Windows could not open the installer.");
                dialog.Close();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                if (dialog.IsDisposed) return;
                message.Text = "The download timed out. Check your connection and click Update to try again.";
                install.Enabled = true;
            }
            catch (Exception ex)
            {
                if (dialog.IsDisposed) return;
                message.Text = $"Update failed. Please try again.\n\n{ex.Message}";
                install.Enabled = true;
            }
        };
        dialog.Controls.AddRange(new Control[] { message, install, later });
        dialog.AcceptButton = install;
        dialog.CancelButton = later;
        dialog.ShowDialog();
    }

    private void ExitApplication()
    {
        _log.Info("Exiting.");
        _snapshotTimer.Stop();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
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
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _snapshotsForm?.Dispose();
            _settingsForm?.Dispose();
            _sync.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
