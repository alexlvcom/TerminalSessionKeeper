using TerminalSessionKeeper.Model;

namespace TerminalSessionKeeper.Tests;

/// <summary>Terse construction of the tab records the matcher and the restore builder work on.</summary>
internal static class TabBuilder
{
    public static TabRecord Wsl(
        string cwd,
        string? agent = null,
        string? sessionTitle = null,
        string? branch = null,
        string? distro = "Ubuntu",
        string? resume = null) =>
        new()
        {
            Kind = agent is null ? TabKinds.WslShell : TabKinds.WslAgent,
            Cwd = cwd,
            Agent = agent,
            SessionTitle = sessionTitle,
            GitBranch = branch,
            Distro = distro,
            ResumeCommand = resume,
            ProfileName = "Ubuntu",
            Launcher = "ubuntu.exe",
        };

    public static TabRecord Windows(
        string cwd,
        string? agent = null,
        string? sessionTitle = null,
        string? branch = null,
        string? resume = null,
        string profileName = "Windows PowerShell") =>
        new()
        {
            Kind = agent is null ? TabKinds.WinShell : TabKinds.WinAgent,
            Cwd = cwd,
            Agent = agent,
            SessionTitle = sessionTitle,
            GitBranch = branch,
            ResumeCommand = resume,
            ProfileName = profileName,
            Launcher = "powershell.exe",
        };

    public static TabRecord Raw(string commandLine, string? cwd = null) =>
        new()
        {
            Kind = TabKinds.Raw,
            CommandLine = commandLine,
            Cwd = cwd,
        };

    public static TabRecord Pinned(this TabRecord tab, string title)
    {
        tab.Title = title;
        tab.TitlePinned = true;
        return tab;
    }
}
