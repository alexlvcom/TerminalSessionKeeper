using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalSessionKeeper.Services;

public sealed record AppUpdate(Version Version, Uri InstallerUrl, Uri? ChecksumsUrl, string? Sha256);

public sealed class UpdateService : IDisposable
{
    public const string InstallerName = "TerminalSessionKeeper-Setup.exe";
    private static readonly string[] DownloadPrefixes =
    [
        "https://github.com/alexlvcom/TerminalSessionKeeper/releases/download/",
        "https://github.com/alexlvcom/terminal-session-keeper/releases/download/"
    ];
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private readonly HttpClient _http;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TerminalSessionKeeper-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdate?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync("https://api.github.com/repos/alexlvcom/TerminalSessionKeeper/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseRelease(json.RootElement, current);
    }

    public static AppUpdate? ParseRelease(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version) || version <= current) return null;
        Uri? installer = null, checksums = null;
        string? digest = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != InstallerName && name != "SHA256SUMS.txt") continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url is null || !DownloadPrefixes.Any(p => url.StartsWith(p, StringComparison.Ordinal)) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !DownloadPrefixes.Any(p => uri.AbsoluteUri.StartsWith(p, StringComparison.Ordinal)))
                throw new InvalidDataException("The update asset is not hosted in this app's GitHub releases.");
            if (name == InstallerName)
            {
                installer = uri;
                if (asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var raw = value.GetString();
                    if (raw?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true) digest = raw[7..];
                }
            }
            else checksums = uri;
        }
        if (installer is null) return null;
        if (digest is not null && !IsSha256(digest)) throw new InvalidDataException("Invalid installer checksum.");
        if (digest is null && checksums is null) throw new InvalidDataException("This update has no installer checksum.");
        return new AppUpdate(version, installer, checksums, digest);
    }

    public async Task<string> DownloadAsync(AppUpdate update, string cacheRoot, CancellationToken cancellationToken)
    {
        var expected = update.Sha256;
        if (expected is null)
        {
            var sums = await _http.GetStringAsync(update.ChecksumsUrl!, cancellationToken);
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == InstallerName) expected = parts[0];
            }
        }
        if (expected is null || !IsSha256(expected)) throw new InvalidDataException("The update checksum is missing or invalid.");
        var directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, InstallerName);
        try
        {
            using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("Installer is too large.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("Installer is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            await using var file = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer checksum did not match. The installer was not started.");
            return path;
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch { }
            throw;
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public void Dispose() => _http.Dispose();
}
