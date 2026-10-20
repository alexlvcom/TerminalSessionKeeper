using TerminalSessionKeeper.Ui;

namespace TerminalSessionKeeper.Tests;

public sealed class UpdateStatusTests
{
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
