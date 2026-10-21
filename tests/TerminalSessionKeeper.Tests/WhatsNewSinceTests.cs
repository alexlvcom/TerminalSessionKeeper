using System.IO;
using TerminalSessionKeeper;
using TerminalSessionKeeper.Ui;
using Xunit;

public sealed class WhatsNewSinceTests
{
    private const string Log = "# Changelog\n\n## 1.4.2 - 2026-10-21\n\n### Main changes\n\n- Fix\n\n" +
                               "## 1.4.1 - 2026-10-20\n\n### Main changes\n\n- Highlights\n\n### Other updates\n\n- Detail\n\n" +
                               "## [1.4.0] - 2026-10-20\n\n### Main changes\n\n- Redesign\n";

    [Fact]
    public void UpgradeShowsEveryReleaseSinceThePreviousOneNewestFirst()
    {
        var entries = Changelog.Parse(Log);
        var upgrade = Changelog.MainChangesSince(entries, new Version(1, 4, 0), "1.4.2");
        Assert.Equal(new[] { "1.4.2", "1.4.1" }, upgrade.Select(e => e.Version));
        Assert.Equal(new[] { "Highlights" }, upgrade[1].Notes);

        var fresh = Changelog.MainChangesSince(entries, null, "1.4.2");
        Assert.Equal("1.4.2", Assert.Single(fresh).Version);
    }

    [Fact]
    public void PreviousReleaseComesFromTheRecordOrFromSettingsOfOlderReleases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WhatsNewSinceTests", Guid.NewGuid().ToString("N"));
        var seen = Path.Combine(directory, "last-seen-release.txt");
        var settings = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var state = new WhatsNewState(seen, settings);
            Assert.Null(state.PreviousRelease());
            File.WriteAllText(settings, "{ \"AutoSnapshotEnabled\": true }");
            Assert.Equal(new Version(1, 3, 2), state.PreviousRelease());
            File.WriteAllText(settings, "{ \"Theme\": 0 }");
            Assert.Equal(new Version(1, 4, 0), state.PreviousRelease());
            state.MarkShown("1.4.1");
            Assert.Equal(new Version(1, 4, 1), state.PreviousRelease());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
