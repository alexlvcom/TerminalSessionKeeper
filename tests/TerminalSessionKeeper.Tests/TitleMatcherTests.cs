using TerminalSessionKeeper.Matching;
using TerminalSessionKeeper.Model;

namespace TerminalSessionKeeper.Tests;

public class TitleMatcherTests
{
    [Fact]
    public void A_leftover_tab_and_a_leftover_title_are_paired()
    {
        // "ABC-1022" has nothing in common with its folder or its session, and no branch to
        // reach it by either. Every tab has exactly one title, so once one of each is left the
        // pairing is forced and needs no guessing — this is what recovers a hand-renamed tab.
        var tabs = new[]
        {
            TabBuilder.Wsl("/home/me/source/docs-mcp"),
            TabBuilder.Wsl("/home/me/source/checkout-api"),
        };

        var result = TitleMatcher.Assign(tabs, new[] { "docs-mcp", "ABC-1022" });

        Assert.Equal("docs-mcp", tabs[0].Title);
        Assert.Equal("ABC-1022", tabs[1].Title);
        Assert.Empty(result.UnmatchedTitles);
    }

    [Fact]
    public void A_pinned_title_survives_the_match_that_claims_its_tab()
    {
        // The on-screen title still has to be consumed — otherwise it looks like a leftover and
        // spoils the forced pairing — but the user's pinned text is what stays.
        var pinned = TabBuilder.Wsl("/home/me/source/checkout-api").Pinned("Cart · release");
        var other = TabBuilder.Wsl("/home/me/source/docs-mcp");

        var result = TitleMatcher.Assign(new[] { pinned, other }, new[] { "checkout-api", "docs-mcp" });

        Assert.Equal("Cart · release", pinned.Title);
        Assert.True(pinned.TitlePinned);
        Assert.Equal("docs-mcp", other.Title);
        Assert.Empty(result.UnmatchedTitles);
    }

    [Fact]
    public void Two_leftovers_are_reported_rather_than_guessed_at()
    {
        // With more than one of each left over, display order is the only remaining signal, and
        // dragging a tab changes its position without changing what it is.
        var tabs = new[]
        {
            TabBuilder.Wsl("/home/me/source/alpha"),
            TabBuilder.Wsl("/home/me/source/beta"),
            TabBuilder.Wsl("/home/me/source/gamma"),
        };

        var result = TitleMatcher.Assign(tabs, new[] { "alpha", "XYZ-4471", "quick note" });

        Assert.Equal("alpha", tabs[0].Title);
        Assert.Null(tabs[1].Title);
        Assert.Null(tabs[2].Title);
        Assert.Equal(new[] { "XYZ-4471", "quick note" }, result.UnmatchedTitles);
    }

    [Fact]
    public void A_ticket_named_tab_finds_its_checkout_through_the_branch()
    {
        var cart = TabBuilder.Wsl("/home/me/source/checkout-api", branch: "ABC-1022-JWT-Alongside-Session-Auth");
        var other = TabBuilder.Wsl("/home/me/source/docs-mcp", branch: "master");

        TitleMatcher.Assign(new[] { other, cart }, new[] { "docs-mcp", "ABC-1022" });

        Assert.Equal("ABC-1022", cart.Title);
        Assert.Equal("docs-mcp", other.Title);
    }

    [Fact]
    public void An_untouched_agent_tab_title_outranks_everything_else()
    {
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api", agent: "claude", sessionTitle: "Fixing the cart totals");

        Assert.Equal(TitleMatcher.ScoreAgentFolderExact, TitleMatcher.Score(tab, "[Claude] checkout-api"));
        Assert.Equal(TitleMatcher.ScoreSessionTitle, TitleMatcher.Score(tab, "Fixing the cart totals"));
    }

    [Fact]
    public void A_drive_letter_alone_is_not_evidence()
    {
        // The "c" from a "C:\" working directory appears in half the window titles on a machine.
        var tab = TabBuilder.Windows(@"C:\");

        Assert.Equal(TitleMatcher.ScoreNone, TitleMatcher.Score(tab, "Docker Desktop"));
    }

    [Fact]
    public void A_branch_match_needs_a_title_token_with_substance()
    {
        // Every token of "a b" appears in the branch, but neither means anything.
        var tab = TabBuilder.Wsl("/home/me/source/thing", branch: "a-b-c-feature");

        Assert.Equal(TitleMatcher.ScoreNone, TitleMatcher.Score(tab, "a b"));
    }

    [Fact]
    public void A_raw_tab_is_recognised_by_its_command()
    {
        var tab = TabBuilder.Raw("ping 8.8.8.8 -t");

        Assert.Equal(TitleMatcher.ScoreCommandName, TitleMatcher.Score(tab, "ping"));
    }

    [Fact]
    public void Half_a_session_title_still_counts_for_something()
    {
        // A tab whose title was cut short still points at its session, as long as at least half
        // the session's own words survive in it.
        var tab = TabBuilder.Wsl("/home/me/source/thing", agent: "codex", sessionTitle: "payment retry loop");

        var score = TitleMatcher.Score(tab, "payment retry");

        Assert.InRange(score, TitleMatcher.ScorePartialSession, TitleMatcher.ScoreSessionTitle - 1);
    }

    [Fact]
    public void A_scrap_of_a_session_title_is_not_enough()
    {
        var tab = TabBuilder.Wsl("/home/me/source/thing", agent: "codex", sessionTitle: "rewrite the payment retry loop");

        Assert.Equal(TitleMatcher.ScoreNone, TitleMatcher.Score(tab, "payment"));
    }

    [Fact]
    public void An_unmatchable_title_is_left_for_the_user_when_no_tab_wants_it()
    {
        var tabs = new[] { TabBuilder.Wsl("/home/me/source/alpha") };

        var result = TitleMatcher.Assign(tabs, new[] { "alpha", "something else entirely", "and another" });

        Assert.Equal("alpha", tabs[0].Title);
        Assert.Equal(2, result.UnmatchedTitles.Count);
    }

    [Fact]
    public void No_tabs_means_every_title_is_unmatched()
    {
        var result = TitleMatcher.Assign(Array.Empty<TabRecord>(), new[] { "alpha" });

        Assert.Equal(new[] { "alpha" }, result.UnmatchedTitles);
    }

    [Fact]
    public void One_title_never_lands_on_two_tabs()
    {
        // Two tabs in the same directory: only one can hold the single on-screen title.
        var first = TabBuilder.Wsl("/home/me/source/alpha");
        var second = TabBuilder.Wsl("/home/me/source/alpha");

        TitleMatcher.Assign(new[] { first, second }, new[] { "alpha" });

        Assert.Equal("alpha", first.Title);
        Assert.Null(second.Title);
    }
}
