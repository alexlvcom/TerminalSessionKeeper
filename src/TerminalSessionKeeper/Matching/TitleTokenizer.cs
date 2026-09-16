namespace TerminalSessionKeeper.Matching;

/// <summary>
/// Reduces a title, folder name or branch to comparable words. Everything that is not a letter
/// or a digit is a separator, so "ABC-1022", "SN_1022" and "SN 1022" are the same three
/// characters' worth of meaning.
/// </summary>
public static class TitleTokenizer
{
    /// <summary>
    /// Tokens shorter than this carry no evidence. The "c" from a "C:\" working directory
    /// appears in half the window titles on a machine and would otherwise outrank a real match.
    /// </summary>
    public const int SolidTokenLength = 3;

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    public static HashSet<string> TokenSet(string? text) =>
        new(Tokenize(text), StringComparer.Ordinal);

    public static IReadOnlyList<string> Solid(IEnumerable<string> tokens) =>
        tokens.Where(token => token.Length >= SolidTokenLength).ToList();
}
