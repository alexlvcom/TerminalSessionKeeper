using TerminalSessionKeeper.Restore;

namespace TerminalSessionKeeper.Tests;

public class WtArgumentsTests
{
    [Fact]
    public void A_wsl_tab_is_rebuilt_through_wsl_exe_with_its_linux_directory()
    {
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api", agent: "claude",
            resume: "claude --resume 043c02a0-a42b-4441-84d2-754721971e63");
        tab.Title = "checkout-api";

        var arguments = WtArguments.ForTab(tab, "tskrestore", pinTitles: false);

        Assert.NotNull(arguments);
        Assert.Equal(new[]
        {
            "-w", "tskrestore", "new-tab",
            "-p", "Ubuntu",
            "--title", "checkout-api",
            "wsl.exe", "-d", "Ubuntu", "--cd", "/home/me/source/checkout-api",
        }, arguments);
    }

    [Fact]
    public void A_windows_tab_keeps_its_profiles_own_command_line()
    {
        // Windows Terminal hands a tab the caller's environment only when the command line is
        // overridden, so leaving it alone is what gets the tab a clean one.
        var tab = TabBuilder.Windows(@"C:\projects\TerminalSessionKeeper");
        tab.Title = "TerminalSessionKeeper";

        var arguments = WtArguments.ForTab(tab, "tskrestore", pinTitles: false);

        Assert.NotNull(arguments);
        Assert.Equal(new[]
        {
            "-w", "tskrestore", "new-tab",
            "-p", "Windows PowerShell",
            "--title", "TerminalSessionKeeper",
            "--startingDirectory", @"C:\projects\TerminalSessionKeeper",
        }, arguments);
    }

    [Fact]
    public void A_multi_word_profile_stays_one_argument()
    {
        // Passing these as a joined string is what once turned -p "Windows PowerShell" into
        // -p Windows and opened the wrong profile.
        var tab = TabBuilder.Windows(@"C:\projects\Thing", profileName: "Windows PowerShell");

        var arguments = WtArguments.ForTab(tab, "tskrestore", pinTitles: false);

        Assert.NotNull(arguments);
        Assert.Contains("Windows PowerShell", arguments);
        Assert.Contains("\"Windows PowerShell\"", WtArguments.Describe(arguments));
    }

    [Fact]
    public void A_pinned_title_is_frozen_but_an_ordinary_one_is_not()
    {
        var pinned = TabBuilder.Wsl("/home/me/source/cart").Pinned("Cart · release");
        var ordinary = TabBuilder.Wsl("/home/me/source/cart");
        ordinary.Title = "cart";

        Assert.Contains("--suppressApplicationTitle", WtArguments.ForTab(pinned, "w", pinTitles: false)!);
        Assert.DoesNotContain("--suppressApplicationTitle", WtArguments.ForTab(ordinary, "w", pinTitles: false)!);

        // The agent should stay free to rename its own tab, which is why freezing is opt-in.
        Assert.Contains("--suppressApplicationTitle", WtArguments.ForTab(ordinary, "w", pinTitles: true)!);
    }

    [Fact]
    public void A_raw_tab_carries_its_command_back()
    {
        var tab = TabBuilder.Raw("ping 8.8.8.8 -t", @"C:\");

        var arguments = WtArguments.ForTab(tab, "tskrestore", pinTitles: false);

        Assert.NotNull(arguments);
        Assert.Equal(new[] { "ping", "8.8.8.8", "-t" }, arguments.TakeLast(3));
    }

    [Fact]
    public void A_raw_tab_with_no_command_cannot_be_rebuilt() =>
        Assert.Null(WtArguments.ForTab(TabBuilder.Raw(string.Empty), "w", pinTitles: false));

    [Fact]
    public void A_wsl_tab_is_told_to_cd_again_after_its_shell_starts()
    {
        // A .zshrc that restores the last working directory runs after wsl --cd has applied and
        // would otherwise drag every restored tab into the same folder.
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api");
        tab.Title = "checkout-api";

        var setup = WtArguments.SetupCommand(tab);

        Assert.Equal(" cd -- '/home/me/source/checkout-api'", setup);
    }

    [Fact]
    public void A_title_the_shell_would_not_produce_is_re_applied_without_a_command()
    {
        // Setting it through the shell never survived: the prompt after the title command is
        // exactly when a themed shell renames the tab after its folder again.
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api");
        tab.Title = "ABC-1022";

        Assert.Equal(" cd -- '/home/me/source/checkout-api'", WtArguments.SetupCommand(tab));
        Assert.Equal("ABC-1022", WtArguments.TitleToReapply(tab, pinTitles: false));
    }

    [Fact]
    public void A_windows_tab_needs_no_typed_setup_at_all()
    {
        // --startingDirectory already put it in the right place.
        var tab = TabBuilder.Windows(@"C:\projects\Thing");
        tab.Title = "XYZ-4471";

        Assert.Null(WtArguments.SetupCommand(tab));
        Assert.Equal("XYZ-4471", WtArguments.TitleToReapply(tab, pinTitles: false));
    }

    [Theory]
    [InlineData("checkout-api", false)]
    [InlineData("[Claude] checkout-api", false)]
    [InlineData("[Codex] checkout-api", false)]
    [InlineData("ABC-1022", true)]
    [InlineData("Fixing the cart totals", true)]
    public void Only_a_title_the_shell_would_not_reach_needs_help(string title, bool expected)
    {
        var tab = TabBuilder.Wsl("/home/me/source/checkout-api");
        tab.Title = title;

        Assert.Equal(expected, WtArguments.NeedsTitleReapplied(tab));
    }

    [Fact]
    public void A_frozen_title_is_never_re_applied()
    {
        var pinned = TabBuilder.Wsl("/home/me/source/cart").Pinned("Cart · release");
        var ordinary = TabBuilder.Wsl("/home/me/source/cart");
        ordinary.Title = "ABC-1022";

        // Already held by --suppressApplicationTitle; the cd is still needed either way.
        Assert.Null(WtArguments.TitleToReapply(pinned, pinTitles: false));
        Assert.Null(WtArguments.TitleToReapply(ordinary, pinTitles: true));
        Assert.Equal(" cd -- '/home/me/source/cart'", WtArguments.SetupCommand(pinned));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's here", @"'it'\''s here'")]
    public void Posix_quoting_survives_an_apostrophe(string input, string expected) =>
        Assert.Equal(expected, WtArguments.PosixQuote(input));

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's here", "'it''s here'")]
    public void Powershell_quoting_doubles_an_apostrophe(string input, string expected) =>
        Assert.Equal(expected, WtArguments.PowerShellQuote(input));
}
