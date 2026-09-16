using TerminalSessionKeeper.Matching;

namespace TerminalSessionKeeper.Tests;

public class TitleTokenizerTests
{
    [Theory]
    [InlineData("ABC-1022", new[] { "abc", "1022" })]
    [InlineData("[Claude] checkout-api", new[] { "claude", "checkout", "api" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    [InlineData("C:\\projects\\Thing", new[] { "c", "projects", "thing" })]
    public void Everything_that_is_not_a_letter_or_digit_separates(string input, string[] expected) =>
        Assert.Equal(expected, TitleTokenizer.Tokenize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--- ...")]
    public void Nothing_meaningful_yields_no_tokens(string? input) =>
        Assert.Empty(TitleTokenizer.Tokenize(input));

    [Fact]
    public void Solid_drops_the_tokens_too_short_to_be_evidence() =>
        Assert.Equal(new[] { "1022", "cart" },
            TitleTokenizer.Solid(new[] { "sn", "1022", "c", "cart" }));
}
