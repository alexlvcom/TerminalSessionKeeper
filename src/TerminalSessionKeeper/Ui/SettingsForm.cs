using System.Drawing;
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

    public SettingsForm(AppSettings settings, StartupManager startupManager, Icon icon)
    {
        _settings = settings;
        _startupManager = startupManager;
        _icon = icon;

        Text = $"{AppInfo.DisplayName} — Settings";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(620, 300);
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
            Width = 548,
            Height = 58,
        };

        _typeCd = Check("Type a cd into each restored WSL tab", settings.TypeCdIntoWslTabs);

        Controls.Add(BuildTabs());
        Controls.Add(BuildButtons());

        _autoSnapshot.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();
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

        var about = new Button { Text = "About…", AutoSize = true };
        about.Click += (_, _) => new AboutForm(_icon).ShowDialog(this);

        var folder = new Button { Text = "Open data folder", AutoSize = true };
        folder.Click += (_, _) => OpenDataFolder();

        buttons.Controls.AddRange(new Control[] { cancel, save, about, folder });

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
            MaximumSize = new Size(548, 0),
            ForeColor = SystemColors.GrayText,
            Tag = NoteTag,
        };

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(548, 0) };

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

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "Could not open the data folder.", AppInfo.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
