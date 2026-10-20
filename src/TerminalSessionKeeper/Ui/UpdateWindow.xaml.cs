using AlexLV.WindowsUi;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Ui;

/// <summary>
/// Offers a newer release: Update downloads, verifies and opens setup; Later dismisses it. A
/// timed-out or failed download keeps the window open so Update can be tried again.
/// </summary>
public partial class UpdateWindow : ThemedWindow
{
    private readonly AppUpdate _update;
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _cancellation;

    public UpdateWindow(AppUpdate update, UpdateService updates, CancellationToken appShutdown)
    {
        InitializeComponent();
        _update = update;
        _updates = updates;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(appShutdown);
        Title = $"Update {AppInfo.DisplayName}";
        TitleVersion = $"v{Changelog.MarketingVersion}";
        Heading.Text = $"Version {update.Version.ToString(3)} is available";
        Message.Text = $"You have {Changelog.MarketingVersion}. Update downloads and verifies the installer, then opens it. " +
                       $"Setup closes {AppInfo.DisplayName} and restarts it after updating.";
        Loaded += (_, _) => UpdateButton.Focus();
        Closing += (_, _) => _cancellation.Cancel();
        Closed += (_, _) => _cancellation.Dispose();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        Message.Text = "Downloading and verifying the installer…";
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = "Starting download";
        SetIcon("", "Accent");

        // A fresh timeout for each attempt, so a retry is not cut short by an earlier one.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        attempt.CancelAfter(TimeSpan.FromMinutes(10));
        var progress = new Progress<(long Received, long? Total)>(p =>
        {
            if (p.Total is > 0 and var total)
            {
                Progress.Value = (double)p.Received / total;
                ProgressText.Text = $"{p.Received / 1048576.0:0.0} of {total / 1048576.0:0.0} MB";
            }
            else
            {
                ProgressText.Text = $"{p.Received / 1048576.0:0.0} MB";
            }
        });

        try
        {
            var cache = Path.Combine(AppPaths.Root, "Updates");
            var installer = await _updates.DownloadAsync(_update, cache, attempt.Token, progress);
            if (!IsLoaded) return;
            ProgressText.Text = "Verified with SHA-256. Opening setup…";
            _ = Process.Start(new ProcessStartInfo { FileName = installer, UseShellExecute = true })
                ?? throw new InvalidOperationException("Windows could not open the installer.");
            Close();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (!IsLoaded) return;
            Fail("The download timed out. Check your connection and click Update to try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                      or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            if (!IsLoaded) return;
            Fail($"Update failed. Please try again.\n\n{ex.Message}");
        }
    }

    private void Fail(string message)
    {
        Message.Text = message;
        Progress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        SetIcon("", "Danger");
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = "Try again";
    }

    private void SetIcon(string glyph, string brush)
    {
        StateIcon.Text = glyph;
        StateIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();
}
