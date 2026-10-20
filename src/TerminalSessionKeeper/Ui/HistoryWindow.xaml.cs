using AlexLV.WindowsUi;
using System.Windows;

namespace TerminalSessionKeeper.Ui;

/// <summary>Every release from CHANGELOG.md, newest first.</summary>
public partial class HistoryWindow : ThemedWindow
{
    public HistoryWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.DisplayName} Change History";
        foreach (var entry in Changelog.Entries) ReleaseNotesView.Fill(Entries, entry, withVersionHeading: true);
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
