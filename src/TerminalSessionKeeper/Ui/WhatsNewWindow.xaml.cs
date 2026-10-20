using System.IO;
using System.Windows;
using AlexLV.WindowsUi;

namespace TerminalSessionKeeper.Ui;

/// <summary>Current-version highlights, acknowledged locally only after rendering.</summary>
public partial class WhatsNewWindow : ThemedWindow
{
    public WhatsNewWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.DisplayName} What's New";
        TitleVersion = $"v{Changelog.MarketingVersion}";
        Heading.Text = $"What’s new in {AppInfo.DisplayName} {Changelog.MarketingVersion}";
        var entry = Changelog.Current;
        var main = entry?.Sections.Where(s => string.Equals(s.Heading, "Main changes", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entry is not null && main is { Length: > 0 })
            entry = new ChangelogEntry(entry.Version, entry.Date, main);
        ReleaseNotesView.Fill(Changes, entry);
        Loaded += (_, _) => ContinueButton.Focus();
    }

    public static void Schedule()
    {
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            System.Windows.Forms.Application.Idle -= onIdle;
            ShowForCurrentRelease();
        };
        System.Windows.Forms.Application.Idle += onIdle;
    }

    public static void ShowForCurrentRelease(Window? owner = null)
    {
        var state = new WhatsNewState();
        if (!state.ShouldShow(Changelog.MarketingVersion)) return;
        try
        {
            var window = new WhatsNewWindow { Owner = owner };
            var recorded = false;
            window.ContentRendered += (_, _) =>
            {
                if (recorded) return;
                recorded = true;
                try { state.MarkShown(Changelog.MarketingVersion); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Debug.WriteLine(ex.Message); }
            };
            WinFormsHosting.ShowModeless(window);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not show release notes: {ex.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void History_Click(object sender, RoutedEventArgs e) => new HistoryWindow { Owner = this }.ShowDialog();
}
