using TerminalSessionKeeper.Restore;
using TerminalSessionKeeper.Settings;

namespace TerminalSessionKeeper.Tests;

public class RestoreEnvironmentTests
{
    [Fact]
    public void Lines_are_read_as_name_and_value()
    {
        var variables = AppSettings.ParseEnvironment("KEEP_DIR=1\r\n\r\n# a comment\nGREETING = a=b c \n");

        Assert.Equal(new[] { ("KEEP_DIR", "1"), ("GREETING", "a=b c") }, variables);
    }

    [Theory]
    [InlineData("NO_EQUALS_SIGN")]
    [InlineData("=value")]
    [InlineData("1ST=x")]
    [InlineData("A:B=x")]
    [InlineData("A/u=x")]
    [InlineData("WITH SPACE=x")]
    public void A_name_wslenv_could_not_carry_is_dropped(string line) =>
        Assert.Empty(AppSettings.ParseEnvironment(line));

    [Fact]
    public void A_name_given_twice_keeps_the_last_value() =>
        Assert.Equal(new[] { ("KEEP_DIR", "2") }, AppSettings.ParseEnvironment("KEEP_DIR=1\nKEEP_DIR=2"));

    [Fact]
    public void Nothing_configured_leaves_wslenv_alone()
    {
        var environment = RestoreService.ComposeEnvironment(new AppSettings(), inheritedWslEnv: "USERPROFILE/p");

        Assert.False(environment.ContainsKey("WSLENV"));
        Assert.Equal("1", environment["TERMINAL_SESSION_KEEPER_RESTORE"]);
    }

    [Fact]
    public void Configured_variables_are_set_and_named_in_wslenv()
    {
        var settings = new AppSettings { RestoreWslEnvironment = "KEEP_DIR=1\nMODE=restore" };

        var environment = RestoreService.ComposeEnvironment(settings, inheritedWslEnv: "USERPROFILE/p");

        Assert.Equal("1", environment["KEEP_DIR"]);
        Assert.Equal("restore", environment["MODE"]);
        Assert.Equal("USERPROFILE/p:KEEP_DIR:MODE", environment["WSLENV"]);
    }

    [Fact]
    public void A_name_already_in_wslenv_is_not_added_twice()
    {
        var settings = new AppSettings { RestoreWslEnvironment = "KEEP_DIR=1" };

        var environment = RestoreService.ComposeEnvironment(settings, inheritedWslEnv: "KEEP_DIR/u:");

        Assert.Equal("KEEP_DIR/u", environment["WSLENV"]);
    }
}
