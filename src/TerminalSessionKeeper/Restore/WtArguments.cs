using TerminalSessionKeeper.Model;

namespace TerminalSessionKeeper.Restore;

/// <summary>
/// Composes the <c>wt.exe</c> command line for one tab, and the line that gets typed into it
/// once its shell is up.
/// </summary>
public static class WtArguments
{
    /// <summary>
    /// <c>wt -w &lt;window&gt; new-tab …</c> for a tab, or null when the record does not carry
    /// enough to rebuild it.
    /// </summary>
    public static IReadOnlyList<string>? ForTab(TabRecord tab, string windowName, bool pinTitles)
    {
        var arguments = new List<string> { "-w", windowName, "new-tab" };

        if (!string.IsNullOrWhiteSpace(tab.ProfileName))
        {
            arguments.Add("-p");
            arguments.Add(tab.ProfileName);
        }

        if (!string.IsNullOrWhiteSpace(tab.Title))
        {
            arguments.Add("--title");
            arguments.Add(tab.Title);

            // The shell's own title updates outrank --title, so a title worth keeping exactly —
            // one the user pinned, or a restore the user asked to freeze — needs the terminal
            // told to ignore them. It is the wrong default: an agent should stay free to rename
            // its own tab once the session resumes.
            if (pinTitles || tab.TitlePinned) arguments.Add("--suppressApplicationTitle");
        }

        if (!string.IsNullOrWhiteSpace(tab.Color))
        {
            arguments.Add("--tabColor");
            arguments.Add(tab.Color);
        }

        switch (tab.Kind)
        {
            case TabKinds.WslAgent:
            case TabKinds.WslShell:
                // Overriding the command line is the only reliable way to put a WSL tab in a
                // Linux directory; --startingDirectory speaks Windows paths. No shell is named,
                // so the distro starts the user's login shell rather than one guessed at here.
                arguments.Add("wsl.exe");
                arguments.Add("-d");
                arguments.Add(string.IsNullOrWhiteSpace(tab.Distro) ? "Ubuntu" : tab.Distro);

                if (!string.IsNullOrWhiteSpace(tab.Cwd))
                {
                    arguments.Add("--cd");
                    arguments.Add(tab.Cwd);
                }

                return arguments;

            case TabKinds.WinAgent:
            case TabKinds.WinShell:
                // Deliberately left as the profile's own command line. Windows Terminal hands a
                // tab the caller's environment only when the command line is overridden, so not
                // overriding it is what guarantees the tab gets the clean environment a fresh
                // shell would.
                if (!string.IsNullOrWhiteSpace(tab.Cwd))
                {
                    arguments.Add("--startingDirectory");
                    arguments.Add(tab.Cwd);
                }

                return arguments;

            case TabKinds.Raw:
                if (string.IsNullOrWhiteSpace(tab.CommandLine)) return null;

                if (!string.IsNullOrWhiteSpace(tab.Cwd))
                {
                    arguments.Add("--startingDirectory");
                    arguments.Add(tab.Cwd);
                }

                arguments.AddRange(tab.CommandLine.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                return arguments;

            default:
                return null;
        }
    }

    /// <summary>
    /// The one line typed into a restored tab and submitted before the resume command is left
    /// waiting, or null when the tab needs nothing.
    ///
    /// Only a WSL tab needs it, and only for its directory: a .zshrc that restores the last
    /// working directory runs after <c>wsl --cd</c> has applied and would otherwise drag every
    /// restored tab into the same folder. The title is not set here — a title command has to be
    /// submitted, and the prompt that follows is precisely when a themed shell renames the tab
    /// after its folder again, so the shell always won. It goes through the console instead;
    /// see <c>ConsoleInjector.SetTitle</c>.
    /// </summary>
    public static string? SetupCommand(TabRecord tab)
    {
        if (!TabKinds.IsWsl(tab.Kind) || string.IsNullOrWhiteSpace(tab.Cwd)) return null;

        // The leading space keeps it out of zsh's history under HIST_IGNORE_SPACE, so the user's
        // Up-arrow still lands on their own last command.
        return $" cd -- {PosixQuote(tab.Cwd)}";
    }

    /// <summary>
    /// The title to re-apply once the tab has settled, or null when it needs none.
    ///
    /// A pinned title is already frozen by --suppressApplicationTitle. Otherwise only a title
    /// the shell would not arrive at by itself is worth re-applying: a tab still called after
    /// its folder — or "[Claude] &lt;folder&gt;" — gets there on its own.
    /// </summary>
    public static string? TitleToReapply(TabRecord tab, bool pinTitles)
    {
        if (pinTitles || tab.TitlePinned) return null;
        return NeedsTitleReapplied(tab) ? tab.Title : null;
    }

    public static bool NeedsTitleReapplied(TabRecord tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Title)) return false;

        var folder = tab.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return true;

        var titleTokens = Matching.TitleTokenizer.Tokenize(tab.Title);
        var folderTokens = Matching.TitleTokenizer.Tokenize(folder);

        if (titleTokens.SequenceEqual(folderTokens, StringComparer.Ordinal)) return false;

        foreach (var prefix in new[] { "claude", "codex", "junie" })
        {
            var shellTitle = new List<string> { prefix };
            shellTitle.AddRange(folderTokens);
            if (titleTokens.SequenceEqual(shellTitle, StringComparer.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>Single quotes, with the one escape POSIX shells allow inside them.</summary>
    public static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Single quotes; PowerShell escapes an embedded quote by doubling it.</summary>
    public static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// A readable rendering of a composed argument list, for the dry run and the log. The real
    /// launch passes the list to ProcessStartInfo.ArgumentList, which quotes correctly — a
    /// hand-joined string is exactly how "-p Windows PowerShell" once became "-p Windows".
    /// </summary>
    public static string Describe(IEnumerable<string> arguments) =>
        "wt.exe " + string.Join(' ', arguments.Select(argument =>
            argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
                ? "\"" + argument.Replace("\"", "\\\"") + "\""
                : argument));
}
