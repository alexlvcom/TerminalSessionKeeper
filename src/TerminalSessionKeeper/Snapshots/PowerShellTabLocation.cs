using TerminalSessionKeeper.Services;

namespace TerminalSessionKeeper.Snapshots;

/// <summary>PowerShell's Set-Location does not update the process PEB directory.</summary>
public static class PowerShellTabLocation
{
    public static string? Read(string? wtSession, string launcher)
    {
        if (!string.Equals(launcher, "pwsh.exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(launcher, "powershell.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(wtSession)) return null;

        var id = wtSession.Trim('{', '}');
        if (!Guid.TryParse(id, out _)) return null;

        var path = Path.Combine(AppPaths.Root, "PowerShellTabs", id + ".txt");
        try
        {
            if (!File.Exists(path)) return null;
            var cwd = File.ReadAllText(path).Trim();
            return Path.IsPathFullyQualified(cwd) && Directory.Exists(cwd) ? cwd : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
