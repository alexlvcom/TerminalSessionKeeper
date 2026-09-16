using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Terminal;

/// <summary>What the WSL-side probe reports for one pane.</summary>
public sealed class WslTabRecord
{
    public string? WtSession { get; set; }
    public string? WtProfileId { get; set; }
    public int ShellPid { get; set; }
    public string? Shell { get; set; }
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public string? Agent { get; set; }
    public int? AgentPid { get; set; }
    public string? SessionId { get; set; }
    public string? SessionTitle { get; set; }
    public string? ResumeCommand { get; set; }
}

/// <summary>
/// Runs the Python probe inside a distro and returns its records keyed by WT_SESSION.
///
/// The probe has to run inside WSL: /proc is the only place a shell's environment, working
/// directory and open file handles can be read, and claude's live session id comes from an
/// open handle. The script travels inside this executable and is staged into the distro on
/// each run, so there is nothing for the user to install and nothing to keep in sync.
/// </summary>
public sealed class WslProbe
{
    private const string ResourceName = "TerminalSessionKeeper.Terminal.snapshot-agents.py";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ILog _log;
    private readonly Lazy<string?> _script;

    public WslProbe(ILog log)
    {
        _log = log;
        _script = new Lazy<string?>(ReadEmbeddedScript);
    }

    public IReadOnlyDictionary<string, WslTabRecord> Probe(string distro)
    {
        var empty = new Dictionary<string, WslTabRecord>(StringComparer.Ordinal);

        var script = _script.Value;
        if (string.IsNullOrEmpty(script))
        {
            _log.Warn("The WSL probe is missing from this build; WSL tabs will not be captured.");
            return empty;
        }

        var output = RunProbe(distro, script);
        if (string.IsNullOrWhiteSpace(output)) return empty;

        List<WslTabRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<WslTabRecord>>(output, JsonOptions);
        }
        catch (JsonException ex)
        {
            _log.Warn($"The WSL probe returned something that is not JSON. {ex.Message}");
            return empty;
        }

        if (records is null) return empty;

        var map = new Dictionary<string, WslTabRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(record.WtSession)) map[record.WtSession] = record;
        }

        _log.Debug($"WSL probe on {distro} reported {map.Count} pane(s).");
        return map;
    }

    private string? RunProbe(string distro, string script)
    {
        // Base64 keeps the payload to [A-Za-z0-9+/=], so nothing here depends on quoting
        // surviving wsl.exe's own parsing. It also sidesteps the CRLF a piped hand-off adds:
        // a stray carriage return inside the script would be a syntax error, and the same
        // trick is why a session id never comes back with a literal ^M on the end.
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        // Staging under /tmp keeps it off /mnt/c: a Windows path would make every read in the
        // probe cross the 9p bridge, and a SQLite database on that path cannot be opened at all
        // while codex holds it in WAL mode.
        const string stage =
            "d=\"/tmp/.terminalsessionkeeper-$(id -u)\" && mkdir -p \"$d\" && chmod 700 \"$d\" && " +
            "printf %s \"$TSK_PROBE\" | base64 -d > \"$d/probe.py\" && exec python3 \"$d/probe.py\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The probe's own bytes, not wsl.exe's UTF-16 chatter.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(distro);
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(stage);

        // Passed through the environment rather than inlined into the command, so the script
        // never has to survive a second round of shell parsing.
        startInfo.Environment["WSLENV"] = AppendToWslEnv(Environment.GetEnvironmentVariable("WSLENV"));
        startInfo.Environment["TSK_PROBE"] = payload;

        return Wsl.Run(startInfo, _log, timeoutMs: 60_000);
    }

    /// <summary>WSLENV is what carries a Windows variable into the distro; ':'-separated.</summary>
    private static string AppendToWslEnv(string? existing)
    {
        const string entry = "TSK_PROBE";
        if (string.IsNullOrWhiteSpace(existing)) return entry;

        var parts = existing.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Split('/')[0] == entry) ? existing : existing + ":" + entry;
    }

    private string? ReadEmbeddedScript()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return null;

            using var reader = new StreamReader(stream, new UTF8Encoding(false));

            // The probe runs under Linux python3, where a CRLF line ending is a syntax error
            // waiting to happen; normalise regardless of how the file was checked out.
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
        catch (Exception ex) when (ex is IOException or FileLoadException or NotSupportedException)
        {
            _log.Warn($"Could not read the embedded WSL probe. {ex.Message}");
            return null;
        }
    }
}
