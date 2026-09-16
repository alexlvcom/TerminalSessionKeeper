namespace TerminalSessionKeeper.Agents;

/// <summary>
/// The current branch of a Windows directory, read straight from .git/HEAD.
///
/// Tabs are very often renamed to the ticket in the branch name, which makes the branch one of
/// the strongest signals for matching a hand-renamed tab back to the directory it belongs to.
/// The WSL side does the same, inside the probe.
/// </summary>
public static class GitBranch
{
    /// <summary>Deep enough for any real checkout; a bound is what stops a symlink loop.</summary>
    private const int MaxDepth = 40;

    public static string? For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var current = path;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            string headPath;

            var marker = Path.Combine(current, ".git");

            if (Directory.Exists(marker))
            {
                headPath = Path.Combine(marker, "HEAD");
            }
            else if (File.Exists(marker))
            {
                // Worktree or submodule: the file points at the real git dir.
                var pointer = TryReadAllText(marker);
                if (pointer is null) return null;

                var gitDir = ParseGitDirPointer(pointer);
                if (gitDir is null) return null;

                if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(current, gitDir));
                headPath = Path.Combine(gitDir, "HEAD");
            }
            else
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) return null;
                current = parent;
                continue;
            }

            var head = TryReadAllText(headPath);
            if (head is null) return null;

            return ParseHead(head);
        }

        return null;
    }

    /// <summary>"ref: refs/heads/my-branch" yields "my-branch"; a detached HEAD yields null.</summary>
    public static string? ParseHead(string head)
    {
        var text = head.Trim();
        if (!text.StartsWith("ref:", StringComparison.Ordinal)) return null;

        var reference = text[4..].Trim();
        if (!reference.StartsWith("refs/", StringComparison.Ordinal)) return null;

        // refs/<kind>/<name>, where <name> may itself contain slashes.
        var parts = reference.Split('/', 3);
        return parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
    }

    public static string? ParseGitDirPointer(string pointer)
    {
        foreach (var line in pointer.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("gitdir:", StringComparison.Ordinal)) continue;

            var target = text[7..].Trim();
            if (target.Length > 0) return target;
        }

        return null;
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
