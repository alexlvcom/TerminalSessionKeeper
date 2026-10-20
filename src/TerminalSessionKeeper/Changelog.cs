using System.Reflection;
using System.Text.RegularExpressions;

namespace TerminalSessionKeeper;

/// <summary>A heading group of one release ("Main changes", "Other updates"); untitled when the entry has none.</summary>
public sealed record ChangelogSection(string? Heading, IReadOnlyList<string> Bullets);

/// <summary>One release from CHANGELOG.md.</summary>
public sealed class ChangelogEntry(string version, string date, IReadOnlyList<ChangelogSection> sections)
{
    public string Version { get; } = version;
    public string Date { get; } = date;
    public IReadOnlyList<ChangelogSection> Sections { get; } = sections;

    /// <summary>Every bullet of the release, in order.</summary>
    public string[] Notes => Sections.SelectMany(s => s.Bullets).ToArray();

    public void Deconstruct(out string version, out string date, out string[] notes)
    {
        version = Version;
        date = Date;
        notes = Notes;
    }
}

/// <summary>
/// Version info and release notes. CHANGELOG.md, embedded at build time, is the only history and
/// the assembly is the only version: the title bar, About, the tray tooltip and the update check
/// all read from here. Up to 1.3.2 a second, hand-maintained history in this file decided the
/// version too, and nothing but care kept it in step with the csproj and CHANGELOG.md.
/// </summary>
public static class Changelog
{
    private static readonly Lazy<ChangelogEntry[]> Parsed = new(() => Parse(ReadEmbedded()));

    /// <summary>Newest first.</summary>
    public static ChangelogEntry[] Entries => Parsed.Value;

    public const string Author = "Alex LV";
    public const string AuthorEmail = "alex@alexlv.com";
    public const string Copyright = "Copyright © 2026 Alex LV";

    /// <summary>The running release, e.g. "1.4.0", from the assembly's informational version.</summary>
    public static string MarketingVersion
    {
        get
        {
            var informational = typeof(Changelog).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational) ? Entries[0].Version : informational.Split('+')[0];
        }
    }

    /// <summary>The release whose notes About shows: the running version, not merely the newest heading.</summary>
    public static ChangelogEntry? Current => Find(MarketingVersion);

    public static ChangelogEntry? Find(string version) => Entries.FirstOrDefault(e => e.Version == version);

    public static string GetReleaseNotes(string version)
    {
        var entry = Find(version);
        if (entry is null) return "No release notes available for this version.";

        var lines = new List<string>();
        foreach (var section in entry.Sections)
        {
            if (section.Heading is not null)
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add(section.Heading);
            }

            lines.AddRange(section.Bullets.Select(b => "• " + b));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Full build version (four parts).</summary>
    public static string BuildVersion => typeof(Changelog).Assembly.GetName().Version?.ToString() ?? MarketingVersion;

    /// <summary>Timestamp this executable was built.</summary>
    public static DateTime BuildDate
    {
        get
        {
            try
            {
                // Environment.ProcessPath is the only reliable source in a single-file publish,
                // where Assembly.Location is empty.
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return File.GetLastWriteTime(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall through to the sentinel.
            }

            return DateTime.MinValue;
        }
    }

    /// <summary>What the app is for.</summary>
    public static readonly string[] Description =
    {
        "Saves the open Windows Terminal tabs — including the live claude and codex",
        "conversations running inside them — and rebuilds the window after a restart,",
        "with each tab back in its own directory and its resume command waiting at the",
        "prompt.",
    };

    /// <summary>What the app touches, surfaced for transparency.</summary>
    public static readonly string[] SystemNotes =
    {
        @"Reads:   the process tree, and each tab's environment and working directory",
        @"         claude and codex session state, and .git/HEAD for the branch name",
        @"Writes:  %LocalAppData%\TerminalSessionKeeper  (snapshots, settings, log)",
        @"         HKCU\...\CurrentVersion\Run           (only while 'Start with Windows' is on)",
        "         Nothing machine-wide. No elevation. No scheduled task. Nothing in your dotfiles.",
    };

    /// <summary>Everything except the version header: description, system notes and every release.</summary>
    public static string FormatBody()
    {
        var lines = new List<string>();
        lines.AddRange(Description);
        lines.Add("");
        lines.AddRange(SystemNotes);
        lines.Add("");
        lines.Add("What's changed");
        lines.Add("──────────────");

        foreach (var entry in Entries)
        {
            lines.Add("");
            lines.Add($"v{entry.Version}  ({entry.Date})");
            foreach (var section in entry.Sections)
            {
                if (section.Heading is not null) lines.Add("  " + section.Heading);
                foreach (var bullet in section.Bullets) lines.Add("  • " + bullet);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The version header plus <see cref="FormatBody"/>, so a pasted copy names its exact build.</summary>
    public static string FormatFull()
    {
        var header = new[]
        {
            $"{AppInfo.DisplayName}  v{MarketingVersion}",
            $"Build {BuildVersion}" +
                (BuildDate == DateTime.MinValue ? "" : $"   (built {BuildDate:yyyy-MM-dd HH:mm})"),
            $"{Author} <{AuthorEmail}>",
            Copyright,
            "",
        };

        return string.Join(Environment.NewLine, header) + FormatBody();
    }

    private static readonly Regex VersionHeading = new(@"^##\s+\[?(?<v>\d+\.\d+\.\d+)\]?\s*[-—–]\s*(?<d>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    /// <summary>Parses Keep a Changelog headings (<c>## [1.4.0] - 2026-10-20</c>), <c>###</c> sections and <c>-</c> bullets.</summary>
    public static ChangelogEntry[] Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? version = null, date = null, heading = null;
        var sections = new List<ChangelogSection>();
        var bullets = new List<string>();

        void FlushSection()
        {
            if (bullets.Count > 0) sections.Add(new ChangelogSection(heading, bullets.ToArray()));
            bullets.Clear();
        }

        void FlushEntry()
        {
            FlushSection();
            if (version is not null) entries.Add(new ChangelogEntry(version, date!, sections.ToArray()));
            sections.Clear();
            heading = null;
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = VersionHeading.Match(line);
            if (match.Success)
            {
                FlushEntry();
                version = match.Groups["v"].Value;
                date = match.Groups["d"].Value;
                continue;
            }

            if (version is null) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushSection();
                heading = line[4..].Trim();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(line[2..].Trim().Replace("`", ""));
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && bullets.Count > 0 && line.Trim().Length > 0)
            {
                bullets[^1] += " " + line.Trim();
            }
        }

        FlushEntry();
        return entries.ToArray();
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md")
            ?? throw new InvalidOperationException("CHANGELOG.md is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
