using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AlexLV.WindowsUi;
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
public partial class SnapshotsWindow : ThemedWindow
{
    private readonly SnapshotStore _store;
    private readonly Func<SnapshotRecord, string> _preview;
    private readonly Action<SnapshotRecord, string> _restore;

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

    public SnapshotsWindow(
        SnapshotStore store,
        Func<SnapshotRecord, string> preview,
        Action<SnapshotRecord, string> restore)
    {
        InitializeComponent();
        _store = store;
        _preview = preview;
        _restore = restore;
        Title = $"{AppInfo.DisplayName} Snapshots";
        Reload();
        Loaded += (_, _) => List.Focus();
    }

    public void Reload()
    {
        var selected = List.SelectedIndex;
        List.Items.Clear();
        foreach (var saved in _store.List())
        {
            List.Items.Add(new Entry(saved, _store.Load(saved.Path)?.TabCount));
        }

        if (List.Items.Count == 0)
        {
            DetailHeader.Text = "DETAILS";
            Detail.Text = "No snapshots yet." + Environment.NewLine + Environment.NewLine +
                          "Use \"Snapshot now\" from the tray menu, with the tabs you want saved open.";
            RestoreButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            return;
        }

        RestoreButton.IsEnabled = true;
        PreviewButton.IsEnabled = true;
        List.SelectedIndex = selected >= 0 && selected < List.Items.Count ? selected : 0;
    }

    private SavedSnapshot? Selected => List.SelectedItem is Entry entry ? entry.Saved : null;

    private SnapshotRecord? LoadSelected()
    {
        var saved = Selected;
        return saved is null ? null : _store.Load(saved.Path);
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var saved = Selected;
        if (saved is null) return;

        DetailHeader.Text = "DETAILS";
        Detail.Text = SnapshotFormatter.Format(_store.Load(saved.Path), saved.Path);
        Detail.ScrollToHome();
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) RestoreSelected();
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RestoreSelected();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = LoadSelected();
        if (snapshot is null) return;

        DetailHeader.Text = "RESTORE PREVIEW (NOTHING IS RUN)";
        Detail.Text = _preview(snapshot);
        Detail.ScrollToHome();
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreSelected();

    private void RestoreSelected()
    {
        var saved = Selected;
        var snapshot = LoadSelected();
        if (saved is null || snapshot is null) return;

        _restore(snapshot, saved.Name);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Detail.Text.Length > 0) Clipboard.SetText(Detail.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another process can hold the clipboard open; not worth interrupting the user for.
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            Process.Start(new ProcessStartInfo { FileName = _store.Directory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageDialog.Show("Could not open the snapshots folder.", MessageKind.Info, owner: this);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
