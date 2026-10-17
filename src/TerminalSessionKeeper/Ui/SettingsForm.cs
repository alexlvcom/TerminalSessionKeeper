using System.Diagnostics;
using System.Drawing;
using System.IO;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// Everything that is set once and then forgotten. It lives here rather than in the tray menu so
/// that the menu holds only the things worth reaching for in a hurry.
///
/// Grouped onto tabs rather than stacked into one scrolling column: every page fits without
/// scrolling, so nothing is hidden below a fold.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly StartupManager _startupManager;
    private readonly Icon _icon;
    private readonly Action<AppSettings>? _onSaveSettings;
    private readonly Func<Task>? _onCheckForUpdates;

    private readonly TabControl _tabs;
    private readonly TabPage _updatesTab;
    private readonly TabPage _aboutTab;

    private readonly CheckBox _autoSnapshot;
    private readonly NumericUpDown _interval;
    private readonly NumericUpDown _keep;
    private readonly CheckBox _restoreAfterReboot;
    private readonly CheckBox _autoResume;
    private readonly CheckBox _pinTitles;
    private readonly CheckBox _sampleColors;
    private readonly CheckBox _restoreColors;
    private readonly CheckBox _notifications;
    private readonly CheckBox _startWithWindows;
    private readonly TextBox _windowName;
    private readonly TextBox _wslEnvironment;
    private readonly CheckBox _typeCd;

    /// <summary>The edited copy, valid once the dialog returns OK.</summary>
    public AppSettings Result => _settings;

    public SettingsForm(
        AppSettings settings,
        StartupManager startupManager,
        Icon icon,
        Action<AppSettings>? onSaveSettings = null,
        Func<Task>? onCheckForUpdates = null)
    {
        _settings = settings;
        _startupManager = startupManager;
        _icon = icon;
        _onSaveSettings = onSaveSettings;
        _onCheckForUpdates = onCheckForUpdates;

        Text = $"{AppInfo.DisplayName} Settings (v{Changelog.MarketingVersion})";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(720, 580);
        MinimumSize = new Size(680, 520);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _autoSnapshot = Check("Take a snapshot automatically", settings.AutoSnapshotEnabled);

        _interval = Number(settings.AutoSnapshotIntervalMinutes,
            AppSettings.MinSnapshotIntervalMinutes, AppSettings.MaxSnapshotIntervalMinutes);

        _keep = Number(settings.EffectiveKeepSnapshots, 1, 500);

        _restoreAfterReboot = Check("Put the tabs back automatically after a reboot",
            settings.RestoreAfterReboot);

        _autoResume = Check("Run each resume command instead of leaving it at the prompt",
            settings.AutoResume);

        _pinTitles = Check("Keep saved tab titles, even when the shell renames a tab",
            settings.PinTitles);

        _sampleColors = Check("Remember tab colours", settings.SampleTabColors);

        _restoreColors = Check("Put tab colours back when tabs are rebuilt", settings.RestoreTabColors);

        _notifications = Check("Show a notification after a snapshot or restore",
            settings.ShowBalloonNotifications);

        _startWithWindows = Check($"Start {AppInfo.DisplayName} with Windows", startupManager.IsEnabled);

        _windowName = new TextBox { Text = settings.RestoreWindowName, Width = 220 };

        _wslEnvironment = new TextBox
        {
            Text = (settings.RestoreWslEnvironment ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n"),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 630,
            Height = 58,
        };

        _typeCd = Check("Type a cd into each restored WSL tab", settings.TypeCdIntoWslTabs);

        _updatesTab = BuildUpdatesTab();
        _aboutTab = BuildAboutTab();
        _tabs = BuildTabs();
        _tabs.TabPages.Add(_updatesTab);
        _tabs.TabPages.Add(_aboutTab);

        Controls.Add(_tabs);
        Controls.Add(BuildButtons());

        _autoSnapshot.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();
    }

    public void SelectAboutTab()
    {
        _tabs.SelectedTab = _aboutTab;
    }

    public void SelectUpdatesTab()
    {
        _tabs.SelectedTab = _updatesTab;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        tabs.TabPages.Add(Page("Snapshots",
            _autoSnapshot,
            Row("Every", _interval, "minutes"),
            Row("Keep the newest", _keep, "snapshots"),
            Note("A snapshot is a few KB. Nothing is written when no tabs are open, so a good " +
                 "snapshot is never replaced by an empty one.")));

        tabs.TabPages.Add(Page("Restoring",
            _restoreAfterReboot,
            Note("Only on the first run after a reboot, and only when no terminal window is " +
                 "already open — restoring beside a live window would put both into the next " +
                 "snapshot."),
            _autoResume,
            Note("Off by default: a dozen agents and their MCP servers all starting at once, " +
                 "right after a reboot, is rarely what you want."),
            Row("Rebuild tabs into a new window named", _windowName, string.Empty),
            Note("Every restore opens its own window and the name carries the time it ran, so a " +
                 "restore never adds its tabs to the window an earlier one built.")));

        tabs.TabPages.Add(Page("Environment",
            Caption("Custom environment variables for restored WSL tabs, one NAME=value per line:"),
            _wslEnvironment,
            Note("Added only to the WSL tabs a restore opens. Tabs you open yourself are not affected."),
            _typeCd,
            Note("Runs cd into the tab's saved folder once its shell has started, in case the shell " +
                 "changed directory while starting up. Turn it off if yours does not.")));

        tabs.TabPages.Add(Page("Appearance",
            _pinTitles,
            Note("Otherwise a restored tab keeps its saved title until the shell or the agent " +
                 "renames it, which is usually what you want."),
            _sampleColors,
            Note("Windows Terminal exposes no API for tab colours, so the window is asked to " +
                 "render itself and the colour is read from the tab's own pixels — which works " +
                 "even while the terminal is behind other windows. A colour has to be read the " +
                 "same twice before it is kept, so a new one takes a snapshot longer to stick."),
            _restoreColors,
            Note("A restored colour is applied the way the colour picker applies one, so the " +
                 "picker's Reset clears it again. That takes a setTabColor action, which lives " +
                 "in Windows Terminal's settings.json for the second or two it is needed and is " +
                 "removed straight afterwards.")));

        tabs.TabPages.Add(Page("General",
            _notifications,
            _startWithWindows,
            Note("Autostart uses the current-user Run key. No elevation, no scheduled task.")));

        return tabs;
    }

    private TabPage BuildUpdatesTab()
    {
        var page = new TabPage("Updates") { UseVisualStyleBackColor = true, Padding = new Padding(20, 18, 20, 18) };

        var desc = new Label
        {
            Text = "Choose when Terminal Session Keeper checks GitHub for newer releases and updates.",
            AutoSize = true,
            Location = new Point(16, 14),
        };
        page.Controls.Add(desc);

        // Status Card
        var statusCard = new Panel
        {
            Location = new Point(16, 42),
            Size = new Size(656, 68),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            Padding = new Padding(12),
        };

        var statusIcon = new Label
        {
            Text = "✓",
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(27, 94, 32),
            Size = new Size(28, 28),
            Location = new Point(12, 18),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var statusTitle = new Label
        {
            Text = "Terminal Session Keeper is up to date",
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(48, 14),
        };

        var lastCheckText = _settings.LastUpdateCheckUtc.HasValue
            ? $"Last checked {_settings.LastUpdateCheckUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Not checked yet";

        var statusSubtitle = new Label
        {
            Text = $"Version {Changelog.MarketingVersion} is the latest stable version • {lastCheckText}",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(48, 36),
        };

        statusCard.Controls.AddRange(new Control[] { statusIcon, statusTitle, statusSubtitle });
        page.Controls.Add(statusCard);

        int y = 128;

        var freqLabel = new Label
        {
            Text = "Check for updates:",
            AutoSize = true,
            Location = new Point(16, y + 5),
            Font = new Font(Font.FontFamily, 9f, FontStyle.Regular),
        };

        var freqCombo = new ComboBox
        {
            Location = new Point(150, y + 2),
            Size = new Size(180, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        freqCombo.Items.AddRange(new object[]
        {
            UpdateCheckFrequency.Daily,
            UpdateCheckFrequency.Hourly,
            UpdateCheckFrequency.Weekly,
            UpdateCheckFrequency.Monthly,
            UpdateCheckFrequency.Never,
        });
        freqCombo.SelectedItem = _settings.CheckFrequency;
        freqCombo.SelectedIndexChanged += (_, _) =>
        {
            if (freqCombo.SelectedItem is UpdateCheckFrequency freq)
            {
                _settings.CheckFrequency = freq;
                _onSaveSettings?.Invoke(_settings);
            }
        };

        var checkNowBtn = new Button
        {
            Text = "Check for updates",
            Location = new Point(344, y),
            Size = new Size(140, 28),
        };
        checkNowBtn.Click += async (_, _) =>
        {
            checkNowBtn.Enabled = false;
            statusSubtitle.Text = "Checking GitHub for updates...";
            try
            {
                if (_onCheckForUpdates is not null)
                    await _onCheckForUpdates();
                statusSubtitle.Text = $"Version {Changelog.MarketingVersion} • Last checked {DateTime.Now:yyyy-MM-dd HH:mm}";
            }
            finally
            {
                if (!checkNowBtn.IsDisposed) checkNowBtn.Enabled = true;
            }
        };

        page.Controls.AddRange(new Control[] { freqLabel, freqCombo, checkNowBtn });

        y += 50;

        // Info Callout Panel
        var callout = new Panel
        {
            Location = new Point(16, y),
            Size = new Size(656, 84),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            Padding = new Padding(14),
        };

        var calloutTitle = new Label
        {
            Text = "Reliable automated updates:",
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(10, 12),
        };

        var calloutBody = new Label
        {
            Text = "Setup installers are downloaded from official GitHub releases and verified with SHA-256 before running. Scheduled background checks stay quiet and never interrupt terminal restoration.",
            ForeColor = SystemColors.WindowFrame,
            Location = new Point(10, 32),
            Size = new Size(630, 42),
        };

        callout.Controls.AddRange(new Control[] { calloutTitle, calloutBody });
        page.Controls.Add(callout);

        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("About") { UseVisualStyleBackColor = true, AutoScroll = true };

        var header = new FlowLayoutPanel
        {
            Location = new Point(16, 14),
            Size = new Size(656, 48),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        var iconBox = new PictureBox
        {
            Image = _icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(36, 36),
            Margin = new Padding(0, 2, 12, 0),
        };
        header.Controls.Add(iconBox);

        var titlePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };

        var title = new Label
        {
            Text = $"{AppInfo.DisplayName}  v{Changelog.MarketingVersion}",
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            AutoSize = true,
        };
        var subtitle = new Label
        {
            Text = $"Built {Changelog.MarketingDate} • by alexlvcom",
            ForeColor = SystemColors.GrayText,
            Font = new Font(Font.FontFamily, 8.5f),
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };
        titlePanel.Controls.Add(title);
        titlePanel.Controls.Add(subtitle);
        header.Controls.Add(titlePanel);
        page.Controls.Add(header);

        int y = 74;
        int labelX = 16;
        int textX = 104;
        int textWidth = 560;

        void AddPathRow(string label, string value, Action? openAction = null)
        {
            page.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(labelX, y + 3) });
            page.Controls.Add(new TextBox
            {
                Text = value,
                Location = new Point(textX, y),
                Size = new Size(openAction is not null ? textWidth - 80 : textWidth, 24),
                ReadOnly = true,
                TabStop = false
            });
            if (openAction is not null)
            {
                var btn = new Button { Text = "Open", Location = new Point(textX + textWidth - 72, y - 1), Size = new Size(72, 26) };
                btn.Click += (_, _) => openAction();
                page.Controls.Add(btn);
            }
            y += 34;
        }

        AddPathRow("Executable:", Application.ExecutablePath, () => OpenFolder(Path.GetDirectoryName(Application.ExecutablePath)!));
        AddPathRow("Settings:", AppPaths.SettingsFile, () => OpenFolder(Path.GetDirectoryName(AppPaths.SettingsFile)!));
        AddPathRow("Snapshots:", AppPaths.SnapshotDirectory, () => OpenFolder(AppPaths.SnapshotDirectory));
        AddPathRow("Logs:", AppPaths.LogDirectory, () => OpenFolder(AppPaths.LogDirectory));

        y += 10;

        var notesHeader = new Label
        {
            Text = $"Release notes for v{Changelog.MarketingVersion}:",
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(labelX, y),
        };
        page.Controls.Add(notesHeader);

        var fullHistoryLink = new LinkLabel
        {
            Text = "View full change history",
            AutoSize = true,
            Location = new Point(textX + textWidth - 140, y),
        };
        fullHistoryLink.LinkClicked += (_, _) => ShowFullHistory();
        page.Controls.Add(fullHistoryLink);

        y += 24;
        var notesText = new TextBox
        {
            Location = new Point(labelX, y),
            Size = new Size(textX + textWidth - labelX, 190),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 9f),
            Text = Changelog.GetReleaseNotes(Changelog.MarketingVersion),
            TabStop = false,
        };
        page.Controls.Add(notesText);
        y += 198;

        var copyButton = new Button
        {
            Text = "Copy details",
            Location = new Point(labelX, y),
            Size = new Size(110, 30),
        };
        copyButton.Click += (_, _) => CopyDetails();
        page.Controls.Add(copyButton);

        return page;
    }

    private void ShowFullHistory()
    {
        var history = new Form
        {
            Text = $"{AppInfo.DisplayName} Change History",
            Icon = _icon,
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(650, 480),
            ShowInTaskbar = false,
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 9f),
            Text = Changelog.FormatFull(),
            TabStop = false,
        };
        box.SelectionStart = 0;
        box.SelectionLength = 0;
        history.Controls.Add(box);
        history.ShowDialog(this);
    }

    private void CopyDetails()
    {
        var text = string.Join(Environment.NewLine, new[]
        {
            $"{AppInfo.DisplayName} v{Changelog.MarketingVersion}",
            $"Built: {Changelog.MarketingDate}",
            $"Executable: {Application.ExecutablePath}",
            $"Settings: {AppPaths.SettingsFile}",
            $"Snapshots: {AppPaths.SnapshotDirectory}",
            $"Logs: {AppPaths.LogDirectory}",
            "",
            $"Release notes for v{Changelog.MarketingVersion}:",
            Changelog.GetReleaseNotes(Changelog.MarketingVersion),
        });
        Clipboard.SetText(text);
        MessageBox.Show(this, "Diagnostic details copied to clipboard.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder: {ex.Message}", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static TabPage Page(string title, params Control[] contents)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(16, 14, 16, 8) };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var control in contents)
        {
            // An explanatory note is indented under the setting it explains.
            control.Margin = ReferenceEquals(control.Tag, NoteTag)
                ? new Padding(22, 0, 0, 10)
                : new Padding(0, 3, 0, 3);

            layout.Controls.Add(control);
        }

        page.Controls.Add(layout);
        return page;
    }

    private Control BuildButtons()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 12),
        };

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        save.Click += (_, _) => Apply();

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        buttons.Controls.AddRange(new Control[] { cancel, save });

        AcceptButton = save;
        CancelButton = cancel;

        return buttons;
    }

    private void SyncEnabled() => _interval.Enabled = _autoSnapshot.Checked;

    private static CheckBox Check(string text, bool value) =>
        new() { Text = text, Checked = value, AutoSize = true };

    private static NumericUpDown Number(int value, int minimum, int maximum) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            Width = 70,
        };

    /// <summary>Marks the explanatory labels, which are indented under what they explain.</summary>
    private static readonly object NoteTag = new();

    private static Label Note(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(630, 0),
            ForeColor = SystemColors.GrayText,
            Tag = NoteTag,
        };

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(630, 0) };

    private static Control Row(string before, Control field, string after)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };

        row.Controls.Add(new Label { Text = before, AutoSize = true, Margin = new Padding(22, 6, 6, 0) });

        field.Margin = new Padding(0, 3, 6, 0);
        row.Controls.Add(field);

        if (after.Length > 0)
        {
            row.Controls.Add(new Label { Text = after, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        }

        return row;
    }

    private void Apply()
    {
        _settings.AutoSnapshotEnabled = _autoSnapshot.Checked;
        _settings.AutoSnapshotIntervalMinutes = (int)_interval.Value;
        _settings.KeepSnapshots = (int)_keep.Value;
        _settings.RestoreAfterReboot = _restoreAfterReboot.Checked;
        _settings.AutoResume = _autoResume.Checked;
        _settings.PinTitles = _pinTitles.Checked;
        _settings.SampleTabColors = _sampleColors.Checked;
        _settings.RestoreTabColors = _restoreColors.Checked;
        _settings.ShowBalloonNotifications = _notifications.Checked;
        _settings.RestoreWslEnvironment = _wslEnvironment.Text.Replace("\r\n", "\n").Trim();
        _settings.TypeCdIntoWslTabs = _typeCd.Checked;

        var windowName = _windowName.Text.Trim();
        if (windowName.Length > 0) _settings.RestoreWindowName = windowName;

        // Autostart lives in the registry rather than in settings.json, so it is applied here
        // instead of being saved with the rest.
        if (_startWithWindows.Checked != _startupManager.IsEnabled
            && !_startupManager.TrySetEnabled(_startWithWindows.Checked))
        {
            MessageBox.Show(this, "Could not change the startup setting. See the log for details.",
                AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
