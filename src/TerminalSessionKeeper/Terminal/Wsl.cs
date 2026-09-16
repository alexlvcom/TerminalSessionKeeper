using System.Diagnostics;
using System.Text;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Terminal;

/// <summary>Running wsl.exe and getting its output back intact.</summary>
public static class Wsl
{
    /// <summary>
    /// wsl.exe emits its own messages as UTF-16LE, but passes a child process's bytes through
    /// untouched. Which encoding to expect therefore depends on who is doing the talking:
    /// <c>wsl -l -q</c> is wsl.exe itself, <c>wsl -e python3</c> is the child.
    /// </summary>
    public static string? Run(string arguments, Encoding outputEncoding, ILog log, int timeoutMs = 30_000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };

        return Run(startInfo, log, timeoutMs);
    }

    public static string? Run(ProcessStartInfo startInfo, ILog log, int timeoutMs = 30_000)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;

            // Read before waiting: a full pipe buffer would deadlock the child against the wait.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                log.Warn("wsl.exe did not finish in time; giving up on the WSL side of this snapshot.");
                TryKill(process);
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                var error = stderr.GetAwaiter().GetResult().Trim();
                log.Warn($"wsl.exe exited {process.ExitCode}. {Shorten(error)}");
                return null;
            }

            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or ObjectDisposedException)
        {
            log.Warn($"Could not run wsl.exe. {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The distro a bare <c>wsl.exe</c> would start. Tabs opened from the default WSL profile
    /// carry no distro on their command line, so this is what they get.
    /// </summary>
    public static string DefaultDistro(ILog log)
    {
        var output = Run("-l -q", Encoding.Unicode, log, 10_000);
        if (string.IsNullOrWhiteSpace(output)) return "Ubuntu";

        var first = output
            .Split('\n', '\r')
            .Select(line => line.Replace("\0", string.Empty).Trim())
            .FirstOrDefault(line => line.Length > 0);

        return string.IsNullOrEmpty(first) ? "Ubuntu" : first;
    }

    /// <summary>
    /// The distro behind a WSL launcher process: "ubuntu.exe" means Ubuntu, and an explicit
    /// <c>-d</c>/<c>--distribution</c> on the command line beats the executable name.
    /// </summary>
    public static string DistroFor(string? processName, string? commandLine, string defaultDistro)
    {
        if (!string.IsNullOrEmpty(commandLine))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                commandLine, @"(?:--distribution|-d)\s+""?([^\s""]+)""?");
            if (match.Success) return match.Groups[1].Value;
        }

        if (!string.IsNullOrEmpty(processName) && processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var stem = processName[..^4];
            if (!string.Equals(stem, "wsl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(stem, "wslhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(stem, "bash", StringComparison.OrdinalIgnoreCase))
            {
                return char.ToUpperInvariant(stem[0]) + stem[1..];
            }
        }

        return defaultDistro;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException or AggregateException)
        {
            // Already gone, or not ours to kill.
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}
