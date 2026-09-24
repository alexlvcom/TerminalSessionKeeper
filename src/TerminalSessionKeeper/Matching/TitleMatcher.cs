using TerminalSessionKeeper.Model;

namespace TerminalSessionKeeper.Matching;

/// <summary>
/// Attributes the titles seen on screen to the tabs they belong to.
///
/// Display order is not creation order — tabs get dragged, and a tab running <c>ping</c> can sit
/// first while its process started second — so titles are matched on content. Every tab has
/// exactly one title, and that is what makes the last step sound: once a single tab and a single
/// title are left over, their pairing is forced and needs no guessing. With more than one left
/// over, display order would be the only signal, and dragging makes it worthless, so those are
/// reported instead.
/// </summary>
public static class TitleMatcher
{
    /// <summary>Prefixes a shell puts in front of the folder name for an agent tab.</summary>
    private static readonly string[] AgentPrefixes = { "claude", "codex", "junie" };

    public const int ScoreNone = 0;

    /// <summary>Partial folder-name overlap; the weakest thing still worth acting on.</summary>
    public const int ScorePartialFolder = 20;

    public const int ScoreWholeFolderSubset = 40;

    /// <summary>The tab is named after the executable Windows Terminal launched in it.</summary>
    public const int ScoreCommandName = 45;

    public const int ScorePartialSession = 50;

    /// <summary>
    /// The title fits entirely inside the branch name: "ABC-1022" belongs to the checkout on
    /// ABC-1022-JWT-Alongside-Session-Auth. This is what recovers a tab renamed to its ticket.
    /// </summary>
    public const int ScoreBranch = 75;

    public const int ScoreSessionTitle = 80;

    /// <summary>The untouched shell title, "&lt;folder&gt;".</summary>
    public const int ScoreFolderExact = 90;

    /// <summary>The untouched shell title for an agent tab, "[Claude] &lt;folder&gt;".</summary>
    public const int ScoreAgentFolderExact = 100;

    /// <param name="UnmatchedTitles">Titles that found no tab.</param>
    /// <param name="TabToTitle">
    /// Which on-screen tab each record was paired with, as record index to title index. Anything
    /// else read off the same on-screen tab — its colour, for one — has to follow this pairing
    /// rather than position, for exactly the reason the titles do.
    /// </param>
    public sealed record Result(
        IReadOnlyList<string> UnmatchedTitles,
        IReadOnlyDictionary<int, int> TabToTitle);

    /// <summary>
    /// Assigns <paramref name="titles"/> to <paramref name="tabs"/>, writing each winner into
    /// <see cref="TabRecord.Title"/>, and returns the titles that found no home.
    /// </summary>
    public static Result Assign(IReadOnlyList<TabRecord> tabs, IReadOnlyList<string> titles)
    {
        var pairing = new Dictionary<int, int>();

        if (tabs.Count == 0 || titles.Count == 0)
        {
            return new Result(titles.ToList(), pairing);
        }

        var pairs = new List<(int Tab, int Title, int Score)>();

        for (var tab = 0; tab < tabs.Count; tab++)
        {
            for (var title = 0; title < titles.Count; title++)
            {
                var score = Score(tabs[tab], titles[title]);
                if (score > ScoreNone) pairs.Add((tab, title, score));
            }
        }

        var usedTabs = new HashSet<int>();
        var usedTitles = new HashSet<int>();

        // The direct child processes are supplied in creation order, which is also the tab
        // strip order until tabs are dragged. When every strong identity match is at that same
        // index, use the order for otherwise indistinguishable shells (notably several pwsh
        // processes whose PEB directories all remain System32 after Set-Location). Weak folder
        // overlaps must not steal a neighbour's title in this case.
        if (tabs.Count == titles.Count && tabs.Count > 1)
        {
            var strong = pairs.Where(pair => pair.Score >= ScoreBranch).ToList();
            var anchored = strong.Where(pair => pair.Tab == pair.Title).ToList();
            if (anchored.Count >= 2
                && strong.All(pair => pair.Tab == pair.Title
                    || anchored.Any(anchor => anchor.Tab == pair.Tab && anchor.Score >= pair.Score)
                    || anchored.Any(anchor => anchor.Title == pair.Title && anchor.Score >= pair.Score)))
            {
                for (var index = 0; index < tabs.Count; index++)
                {
                    pairing[index] = index;
                    Apply(tabs[index], titles[index]);
                }

                return new Result(Array.Empty<string>(), pairing);
            }
        }

        // Best-first and greedy. OrderByDescending is stable, so equal scores fall back to the
        // order the pairs were generated in and the outcome is reproducible.
        foreach (var pair in pairs.OrderByDescending(pair => pair.Score))
        {
            if (usedTabs.Contains(pair.Tab) || usedTitles.Contains(pair.Title)) continue;

            usedTabs.Add(pair.Tab);
            usedTitles.Add(pair.Title);
            pairing[pair.Tab] = pair.Title;
            Apply(tabs[pair.Tab], titles[pair.Title]);
        }

        var leftoverTabs = Enumerable.Range(0, tabs.Count).Where(index => !usedTabs.Contains(index)).ToList();
        var leftoverTitles = Enumerable.Range(0, titles.Count).Where(index => !usedTitles.Contains(index)).ToList();

        if (leftoverTabs.Count == 1 && leftoverTitles.Count == 1)
        {
            pairing[leftoverTabs[0]] = leftoverTitles[0];
            Apply(tabs[leftoverTabs[0]], titles[leftoverTitles[0]]);
            leftoverTitles.Clear();
        }

        return new Result(leftoverTitles.Select(index => titles[index]).ToList(), pairing);
    }

    /// <summary>
    /// A title claimed by a pinned tab still consumes that tab — its on-screen title has to be
    /// accounted for, or it looks like a leftover and spoils the forced pairing at the end — but
    /// the user's pinned text is what survives.
    /// </summary>
    private static void Apply(TabRecord tab, string title)
    {
        if (!tab.TitlePinned) tab.Title = title;
    }

    /// <summary>How strongly a title belongs to a tab. Zero means no evidence at all.</summary>
    public static int Score(TabRecord tab, string? title)
    {
        var titleTokens = TitleTokenizer.Tokenize(title);
        if (titleTokens.Count == 0) return ScoreNone;

        var titleSet = new HashSet<string>(titleTokens, StringComparer.Ordinal);

        var folderTokens = TitleTokenizer.Tokenize(tab.FolderName);
        var sessionTokens = TitleTokenizer.Tokenize(tab.SessionTitle);

        // 1. The untouched shell title. A shell renders an agent tab as "[Claude] <folder>" and
        //    a plain one as "<folder>"; either is conclusive.
        if (folderTokens.Count > 0)
        {
            foreach (var prefix in AgentPrefixes)
            {
                if (SequenceEquals(titleTokens, Prepend(prefix, folderTokens))) return ScoreAgentFolderExact;
            }

            if (SequenceEquals(titleTokens, folderTokens)) return ScoreFolderExact;
        }

        // 2. The agent overwrote the title with its own conversation summary.
        if (sessionTokens.Count > 0)
        {
            var hits = sessionTokens.Count(titleSet.Contains);
            if (hits == sessionTokens.Count) return ScoreSessionTitle;
            if (hits >= (int)Math.Ceiling(sessionTokens.Count / 2.0)) return ScorePartialSession;
        }

        // 3. The git branch. A hand-renamed tab is usually named after the ticket in its branch,
        //    so a title whose every token appears in the branch belongs here. Checked in that
        //    direction — title within branch, not the reverse — and only when the title carries
        //    a token with enough substance to mean something.
        var branchTokens = TitleTokenizer.Tokenize(tab.GitBranch);
        if (branchTokens.Count > 0 && TitleTokenizer.Solid(titleTokens).Count > 0)
        {
            var branchSet = new HashSet<string>(branchTokens, StringComparer.Ordinal);
            if (titleTokens.All(branchSet.Contains)) return ScoreBranch;
        }

        // 4. A tab Windows Terminal launched directly is named after its executable.
        if (!string.IsNullOrWhiteSpace(tab.CommandLine))
        {
            // A cmd profile can run a command through /k or /c; the visible title often
            // names that command rather than cmd.exe itself.
            var commandWords = tab.CommandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var commandSwitch = Array.FindIndex(commandWords, word =>
                word.Equals("/k", StringComparison.OrdinalIgnoreCase)
                || word.Equals("/c", StringComparison.OrdinalIgnoreCase));
            if (titleTokens.Count == 1 && commandSwitch >= 0 && commandSwitch + 1 < commandWords.Length
                && TitleTokenizer.Tokenize(commandWords[commandSwitch + 1])
                    .SequenceEqual(titleTokens, StringComparer.Ordinal)) return ScoreBranch;

            var executable = tab.CommandLine
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(executable))
            {
                var leaf = executable[(executable.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
                var executableTokens = TitleTokenizer.Tokenize(leaf);
                if (executableTokens.Count > 0 && titleSet.Contains(executableTokens[0])) return ScoreCommandName;
            }
        }

        // 5. Partial folder overlap, on substantial tokens only.
        var solidFolderTokens = TitleTokenizer.Solid(folderTokens);
        if (solidFolderTokens.Count > 0)
        {
            var hits = solidFolderTokens.Count(titleSet.Contains);
            if (hits == solidFolderTokens.Count) return ScoreWholeFolderSubset;
            if (hits > 0) return ScorePartialFolder + hits;
        }

        return ScoreNone;
    }

    private static IReadOnlyList<string> Prepend(string first, IReadOnlyList<string> rest)
    {
        var combined = new List<string>(rest.Count + 1) { first };
        combined.AddRange(rest);
        return combined;
    }

    private static bool SequenceEquals(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
