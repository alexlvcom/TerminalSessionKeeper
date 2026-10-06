using System.Diagnostics;
using System.Drawing;
using TerminalSessionKeeper.Model;
using TerminalSessionKeeper.Snapshots;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// The saved snapshots, and what is in each one.
///
/// This is where restoring an older snapshot belongs, rather than in a tray submenu: picking one
/// out of a list of bare timestamps is guesswork, and seeing its tabs first is the whole point.
/// The files themselves are plain JSON and hand-editable — "Open folder" is how you get at one
/// to fix a title the matcher could not work out.
/// </summary>
public sealed class SnapshotsForm : Form
{
    private readonly SnapshotStore _store;
    private readonly Func<SnapshotRecord, string> _preview;
    private readonly Action<SnapshotRecord, string> _restore;

    private readonly ListBox _list;
    private readonly TextBox _detail;
    private readonly Button _restoreButton;
    private readonly Button _previewButton;
    private readonly SplitContainer _split;

    private sealed record Entry(SavedSnapshot Saved, int? TabCount)
    {
        public override string ToString()
        {
            var stamp = Saved.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss");
            var tabs = TabCount switch
            {
                null => "  ?",
                1 => "  1 tab",
                var n => $"{n,3} tabs",
            };
            return $"{stamp}  {tabs}";
        }
    }

    public SnapshotsForm(
        SnapshotStore store,
        Func<SnapshotRecord, string> preview,
        Action<SnapshotRecord, string> restore,
        Icon icon)
    {
        _store = store;
        _preview = preview;
        _restore = restore;

        Text = $"{AppInfo.DisplayName} — Snapshots";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 460);
        ClientSize = new Size(960, 620);
        ShowInTaskbar = true;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => RestoreSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            RestoreSelected();
        };

        _detail = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = SystemColors.Window,
            TabStop = false,
        };

        // The splitter is sized to the list's contents in FitList once the form has a real size;
        // a fixed pixel distance set here is lost to DPI scaling and the default control width.
        var split = _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };

        split.Panel1.Controls.Add(_list);
        split.Panel1.Controls.Add(new Label
        {
            Text = "Saved snapshots",
            Dock = DockStyle.Top,
            Padding = new Padding(6, 6, 6, 4),
            AutoSize = false,
            Height = 26,
        });

        split.Panel2.Controls.Add(_detail);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();

        _restoreButton = new Button { Text = "Restore this one", AutoSize = true };
        _restoreButton.Click += (_, _) => RestoreSelected();

        _previewButton = new Button { Text = "Preview restore", AutoSize = true };
        _previewButton.Click += (_, _) => PreviewSelected();

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Reload();

        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => Copy();

        var folder = new Button { Text = "Open folder", AutoSize = true };
        folder.Click += (_, _) => OpenFolder();

        buttons.Controls.AddRange(new Control[]
        {
            close, _restoreButton, _previewButton, refresh, copy, folder,
        });

        Controls.Add(split);
        Controls.Add(buttons);

        CancelButton = close;

        Reload();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitList();
    }

    /// <summary>Widens the list so every entry — timestamp and tab count — shows without dragging.</summary>
    private void FitList()
    {
        var widest = TextRenderer.MeasureText("Saved snapshots", Font).Width;
        foreach (var item in _list.Items)
        {
            widest = Math.Max(widest, TextRenderer.MeasureText(item.ToString(), _list.Font).Width);
        }

        var wanted = widest + SystemInformation.VerticalScrollBarWidth + LogicalToDeviceUnits(16);
        var max = Math.Max(_split.Panel1MinSize, _split.Width / 2);
        _split.SplitterDistance = Math.Clamp(wanted, _split.Panel1MinSize, max);
    }

    public void Reload()
    {
        var selected = _list.SelectedIndex;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var saved in _store.List())
        {
            _list.Items.Add(new Entry(saved, _store.Load(saved.Path)?.TabCount));
        }
        _list.EndUpdate();

        if (IsHandleCreated) FitList();

        if (_list.Items.Count == 0)
        {
            _detail.Text = "No snapshots yet." + Environment.NewLine + Environment.NewLine +
                           "Use \"Snapshot now\" from the tray menu, with the tabs you want saved open.";
            _restoreButton.Enabled = false;
            _previewButton.Enabled = false;
            return;
        }

        _restoreButton.Enabled = true;
        _previewButton.Enabled = true;
        _list.SelectedIndex = selected >= 0 && selected < _list.Items.Count ? selected : 0;
    }

    private SavedSnapshot? Selected =>
        _list.SelectedItem is Entry entry ? entry.Saved : null;

    private SnapshotRecord? LoadSelected()
    {
        var saved = Selected;
        return saved is null ? null : _store.Load(saved.Path);
    }

    private void ShowSelected()
    {
        var saved = Selected;
        if (saved is null) return;

        var snapshot = _store.Load(saved.Path);
        _detail.Text = SnapshotFormatter.Format(snapshot, saved.Path);
        _detail.SelectionStart = 0;
        _detail.SelectionLength = 0;
    }

    private void PreviewSelected()
    {
        var snapshot = LoadSelected();
        if (snapshot is null) return;

        _detail.Text = _preview(snapshot);
        _detail.SelectionStart = 0;
        _detail.SelectionLength = 0;
    }

    private void RestoreSelected()
    {
        var saved = Selected;
        var snapshot = LoadSelected();
        if (saved is null || snapshot is null) return;

        _restore(snapshot, saved.Name);
    }

    private void Copy()
    {
        try
        {
            if (_detail.Text.Length > 0) Clipboard.SetText(_detail.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            // Another process can hold the clipboard open; not worth interrupting the user for.
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            Process.Start(new ProcessStartInfo { FileName = _store.Directory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "Could not open the snapshots folder.", AppInfo.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
