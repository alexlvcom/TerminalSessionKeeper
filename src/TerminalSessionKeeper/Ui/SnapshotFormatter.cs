using TerminalSessionKeeper.Model;

namespace TerminalSessionKeeper.Ui;

/// <summary>Renders a snapshot as the plain listing shown in "Show last snapshot".</summary>
public static class SnapshotFormatter
{
    public static string Format(SnapshotRecord? snapshot, string? path)
    {
        if (snapshot is null)
        {
            return "No snapshot has been taken yet." + Environment.NewLine + Environment.NewLine +
                   "Use \"Snapshot now\" from the tray menu, with the tabs you want saved open.";
        }

        var lines = new List<string>
        {
            $"Captured  {Local(snapshot.CapturedAt)}",
            $"Machine   {snapshot.Machine}\\{snapshot.User}",
            $"Tabs      {snapshot.TabCount}",
        };

        if (!string.IsNullOrEmpty(path)) lines.Add($"File      {path}");

        foreach (var window in snapshot.Windows)
        {
            lines.Add(string.Empty);
            lines.Add($"Window {window.ProcessId}" +
                      (string.IsNullOrWhiteSpace(window.MainTitle) ? string.Empty : $"  —  {window.MainTitle}"));
            lines.Add(new string('─', 66));

            var index = 0;
            foreach (var tab in window.Tabs)
            {
                index++;
                lines.Add($"{index,3}. {(string.IsNullOrWhiteSpace(tab.Title) ? "(untitled)" : tab.Title)}");
                lines.Add($"     {tab.Kind,-10} {tab.Cwd ?? "(directory unknown)"}");

                if (!string.IsNullOrEmpty(tab.GitBranch)) lines.Add($"     branch     {tab.GitBranch}");

                if (!string.IsNullOrEmpty(tab.ResumeCommand))
                {
                    lines.Add($"     resume     {tab.ResumeCommand}");
                }
                else if (!string.IsNullOrEmpty(tab.Agent))
                {
                    lines.Add($"     resume     {tab.Agent}: no resumable session found");
                }
            }
        }

        if (snapshot.UnmatchedTabTitles.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Tab titles that could not be attributed to a tab:");

            // Reported rather than guessed. Pin them by hand in overrides.json.
            foreach (var title in snapshot.UnmatchedTabTitles) lines.Add("  " + title);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Local(string capturedAt) =>
        DateTimeOffset.TryParse(capturedAt, out var parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : capturedAt;
}
