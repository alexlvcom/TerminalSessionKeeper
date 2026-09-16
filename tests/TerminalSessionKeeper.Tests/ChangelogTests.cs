using System.Text.RegularExpressions;

namespace TerminalSessionKeeper.Tests;

public class ChangelogTests
{
    [Fact]
    public void The_newest_entry_matches_the_version_in_the_csproj()
    {
        // About shows the changelog's version; the binary carries the csproj's. They have to be
        // the same number, or a bug report names a build that does not exist.
        var csproj = FindCsproj();
        var text = File.ReadAllText(csproj);

        var prefix = Regex.Match(text, @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;

        Assert.Equal(prefix, Changelog.MarketingVersion);
    }

    [Fact]
    public void Every_entry_is_dated_and_says_something()
    {
        foreach (var (version, date, notes) in Changelog.Entries)
        {
            Assert.Matches(@"^\d+\.\d+\.\d+$", version);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", date);
            Assert.NotEmpty(notes);
        }
    }

    [Fact]
    public void Entries_run_newest_first()
    {
        var dates = Changelog.Entries.Select(entry => DateTime.Parse(entry.Date)).ToList();

        Assert.Equal(dates.OrderByDescending(date => date), dates);
    }

    [Fact]
    public void The_clipboard_copy_identifies_the_build_it_came_from()
    {
        var text = Changelog.FormatFull();

        Assert.Contains(Changelog.MarketingVersion, text);
        Assert.Contains(Changelog.BuildVersion, text);
    }

    private static string FindCsproj()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "src", "TerminalSessionKeeper", "TerminalSessionKeeper.csproj");

            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find TerminalSessionKeeper.csproj from " + AppContext.BaseDirectory);
    }
}
