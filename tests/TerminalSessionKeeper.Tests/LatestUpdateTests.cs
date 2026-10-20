using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TerminalSessionKeeper.Services;
using Xunit;
public sealed class LatestUpdateTests
{
    [Fact]
    public async Task OpenOfferUsesLatestReleaseAndFailsClosed()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var version = "1.0.1";
        var payload = new byte[] { 1, 2, 3 };
        var assetRequests = new List<string>();
        var fail = false;
        using var service = new UpdateService(new Handler(request =>
        {
            var isFeed = request.RequestUri!.Host == "api.github.com";
            if (isFeed)
            {
                Check(request.Headers.CacheControl?.NoCache == true, "Release request could use stale cache");
                if (fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var digest = Convert.ToHexString(SHA256.HashData(payload));
                var url = $"https://github.com/alexlvcom/TerminalSessionKeeper/releases/download/v{version}/{UpdateService.InstallerName}";
                var json = JsonSerializer.Serialize(new { tag_name = "v" + version, draft = false, prerelease = false, assets = new[] { new { name = UpdateService.InstallerName, browser_download_url = url, digest = "sha256:" + digest } } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            assetRequests.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var offered = (await service.CheckAsync(new Version(1, 0, 0), default))!;
        version = "1.0.3"; payload = new byte[] { 7, 8, 9 };
        var latest = await service.RefreshAsync(offered);
        Check(latest.Version == new Version(1, 0, 3), "Intermediate release selected");
        Check(assetRequests.Count == 0, "Refresh downloaded before resolving latest release");
        var cache = Path.Combine(Path.GetTempPath(), "LatestUpdateTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = await service.DownloadAsync(latest, cache, default);
            Check(File.ReadAllBytes(path).SequenceEqual(payload), "Wrong release installer");
            Check(assetRequests.Single().Contains("1.0.3"), "Downloaded original offer");
            fail = true;
            try { await service.RefreshAsync(offered); throw new Exception("Refresh failure fell back to old release"); }
            catch (HttpRequestException) { }
            fail = false; version = "1.0.0";
            try { await service.RefreshAsync(offered); throw new Exception("Withdrawn offer was accepted"); }
            catch (InvalidDataException) { }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
