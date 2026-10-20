using TerminalSessionKeeper.Logging;
using TerminalSessionKeeper.Services;
using TerminalSessionKeeper.Settings;
using TerminalSessionKeeper.Ui;

namespace TerminalSessionKeeper.Tests;

public sealed class UpdateStatusTests
{
    private static readonly DateTime Checked = new(2026, 10, 20, 10, 25, 0, DateTimeKind.Utc);

    private static AppSettings LastCheck(UpdateCheckOutcome? outcome, string? newer = null, string? failure = null) =>
        new()
        {
            LastUpdateCheckUtc = Checked,
            LastUpdateOutcome = outcome,
            LastUpdateAvailableVersion = newer,
            LastUpdateFailure = failure,
        };

    [Fact]
    public void OnOpenARecordedNewerReleaseIsOfferedNotCalledLatest()
    {
        var status = UpdateStatus.FromLastCheck(LastCheck(UpdateCheckOutcome.Available, "1.4.1"), "1.4.0");

        Assert.Equal(UpdateState.Available, status.State);
        Assert.Equal("Version 1.4.1 is available", status.Title);
        Assert.DoesNotContain("latest", status.Title);
    }

    [Fact]
    public void OnOpenANewerReleaseThatIsNowRunningCountsAsCurrent()
    {
        var status = UpdateStatus.FromLastCheck(LastCheck(UpdateCheckOutcome.Available, "1.4.0"), "1.4.0");

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.Equal("You're running the latest version (v1.4.0)", status.Title);
    }

    [Fact]
    public void OnOpenOnlyACheckThatFoundNothingNewerSaysLatest()
    {
        var status = UpdateStatus.FromLastCheck(LastCheck(UpdateCheckOutcome.Current), "1.4.0");

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.Equal("You're running the latest version (v1.4.0)", status.Title);
    }

    [Fact]
    public void OnOpenAFailedCheckShowsItsReason()
    {
        var status = UpdateStatus.FromLastCheck(LastCheck(UpdateCheckOutcome.Failed, failure: "No such host is known."), "1.4.0");

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Equal("Could not check for updates", status.Title);
        Assert.Contains("No such host is known.", status.Detail);
    }

    [Fact]
    public void OnOpenACheckWithNoRecordedResultClaimsNothing()
    {
        // Settings written before 1.4.0 kept only the check time.
        var legacy = UpdateStatus.FromLastCheck(LastCheck(outcome: null), "1.4.0");
        var never = UpdateStatus.FromLastCheck(new AppSettings(), "1.4.0");

        Assert.Equal(UpdateState.NotChecked, legacy.State);
        Assert.DoesNotContain("latest", legacy.Title);
        Assert.Equal(UpdateState.NotChecked, never.State);
    }

    [Fact]
    public void TheLastResultSurvivesASaveAndLoad()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(NullLog.Instance, temp.File("settings.json"));
        store.Save(LastCheck(UpdateCheckOutcome.Available, "1.4.1"));

        var loaded = store.Load();
        Assert.Equal(UpdateCheckOutcome.Available, loaded.LastUpdateOutcome);
        Assert.Equal("1.4.1", loaded.LastUpdateAvailableVersion);
    }

    [Fact]
    public void ACurrentAppSaysItRunsTheLatestVersionWithTheVersion()
    {
        var status = UpdateStatus.Latest("1.4.0", new DateTime(2026, 10, 20, 13, 25, 0));

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.Equal("You're running the latest version (v1.4.0)", status.Title);
        Assert.Equal("Last checked 2026-10-20 13:25", status.Detail);
    }

    [Fact]
    public void AFailedCheckShowsItsReasonInTheCard()
    {
        var status = UpdateStatus.Failed("1.4.0", "No such host is known.");

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Equal("Could not check for updates", status.Title);
        Assert.Contains("v1.4.0", status.Detail);
        Assert.Contains("No such host is known.", status.Detail);
    }

    [Fact]
    public void NoMessageUsesTheOldInstallerWording()
    {
        // The shared standard words a current app as "the latest version", never installer wording.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "TerminalSessionKeeper")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var sources = Directory.EnumerateFiles(Path.Combine(directory!.FullName, "src", "TerminalSessionKeeper"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var path in sources)
            Assert.DoesNotContain("No newer installer", File.ReadAllText(path));
    }
}
