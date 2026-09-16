using TerminalSessionKeeper.Agents;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Tests;

public class AgentPathsTests
{
    [Theory]
    // Windows tools record the extended-length form, and Windows paths are case-insensitive.
    [InlineData(@"\\?\C:\Users\Me\Projects\Thing", @"C:\Users\Me\Projects\Thing")]
    [InlineData(@"C:\Users\ME\PROJECTS\thing", @"c:\users\me\projects\Thing")]
    [InlineData(@"C:\Users\Me\Projects\Thing\", @"C:\Users\Me\Projects\Thing")]
    // Antigravity records a URI rather than a path.
    [InlineData("file:///C:/Users/Me/Projects/Thing", @"C:\Users\Me\Projects\Thing")]
    [InlineData("file:///home/me/source/cart", "/home/me/source/cart")]
    public void Recorded_directories_compare_the_way_the_platform_treats_them(string left, string right) =>
        Assert.Equal(AgentPaths.NormalizeDirectory(left), AgentPaths.NormalizeDirectory(right));

    [Fact]
    public void Linux_directories_stay_case_sensitive() =>
        Assert.NotEqual(
            AgentPaths.NormalizeDirectory("/home/me/Source"),
            AgentPaths.NormalizeDirectory("/home/me/source"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_normalises_to_nothing(string? value) =>
        Assert.Equal(string.Empty, AgentPaths.NormalizeDirectory(value));
}

public class CodexSessionsTests
{
    [Fact]
    public void The_title_after_the_request_marker_is_what_matters()
    {
        // codex stores the entire first user message in "title", preamble and all.
        const string title = "# Context\n\nSome pasted context.\n\n## My request:\n\nFix the retry loop\n";

        Assert.Equal("Fix the retry loop", CodexSessions.CleanTitle(title));
    }

    [Fact]
    public void Headings_bullets_and_fences_are_skipped()
    {
        Assert.Equal("the actual ask", CodexSessions.CleanTitle("# heading\n- a bullet\n`code`\nthe actual ask"));
    }

    [Fact]
    public void A_long_title_is_cut_to_fit_a_tab()
    {
        var cleaned = CodexSessions.CleanTitle(new string('x', 200));

        Assert.NotNull(cleaned);
        Assert.Equal(60, cleaned.Length);
        Assert.EndsWith("...", cleaned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# only a heading")]
    public void Nothing_usable_yields_no_title(string? title) => Assert.Null(CodexSessions.CleanTitle(title));

    [Fact]
    public void A_machine_without_codex_costs_codex_and_nothing_else()
    {
        // Codex support is one column of one kind of tab. Whatever is wrong with it — no codex
        // home, no SQLite engine — must never reach the snapshot as a failure.
        var sessions = new CodexSessions(NullLog.Instance,
            Path.Combine(Path.GetTempPath(), "tsk-no-codex-" + Guid.NewGuid().ToString("N")));

        Assert.Null(sessions.ThreadFor(@"C:\projects\Thing"));
        Assert.Null(sessions.ThreadFor(null));
    }
}

public class GitBranchTests
{
    [Theory]
    [InlineData("ref: refs/heads/master\n", "master")]
    [InlineData("ref: refs/heads/ABC-1022-JWT-Alongside-Session-Auth\n", "ABC-1022-JWT-Alongside-Session-Auth")]
    [InlineData("ref: refs/heads/feature/nested/name\n", "feature/nested/name")]
    public void A_symbolic_head_yields_the_branch(string head, string expected) =>
        Assert.Equal(expected, GitBranch.ParseHead(head));

    [Theory]
    [InlineData("9a0f3c1b2d4e5f60718293a4b5c6d7e8f9012345\n")]
    [InlineData("")]
    public void A_detached_head_has_no_branch(string head) => Assert.Null(GitBranch.ParseHead(head));

    [Fact]
    public void A_worktree_pointer_is_followed() =>
        Assert.Equal(@"C:\projects\thing\.git\worktrees\wt",
            GitBranch.ParseGitDirPointer("gitdir: C:\\projects\\thing\\.git\\worktrees\\wt\n"));

    [Fact]
    public void Something_that_is_not_a_pointer_is_not_followed() =>
        Assert.Null(GitBranch.ParseGitDirPointer("this is not a git file"));

    [Fact]
    public void A_directory_outside_any_repository_has_no_branch() =>
        Assert.Null(GitBranch.For(Path.GetTempPath()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_means_nothing_out(string? path) => Assert.Null(GitBranch.For(path));
}

public class ClaudeSessionsTests
{
    [Theory]
    [InlineData(@"C:\projects\TerminalSessionKeeper", "C--projects-TerminalSessionKeeper")]
    [InlineData("/home/me/source/checkout-api", "-home-me-source-checkout-api")]
    public void The_project_slug_replaces_everything_but_letters_and_digits(string cwd, string expected) =>
        Assert.Equal(expected, ClaudeSessions.ProjectSlug(cwd));

    [Fact]
    public void An_unknown_session_has_no_title() => Assert.Null(ClaudeSessions.SessionTitle(null));

    [Fact]
    public void An_unknown_directory_has_no_session() =>
        Assert.Null(new ClaudeSessions().SessionIdFor(@"C:\this\does\not\exist\anywhere"));
}
