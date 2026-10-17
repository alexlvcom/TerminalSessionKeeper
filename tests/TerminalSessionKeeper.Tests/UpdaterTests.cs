using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerminalSessionKeeper.Services;
using Xunit;

namespace TerminalSessionKeeper.Tests;

public class UpdaterTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("test installer payload");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload));
    private const string Root = "https://github.com/alexlvcom/TerminalSessionKeeper/releases/download/v1.4.0/";

    private static JsonElement Release(string tag = "v1.4.0", bool prerelease = false, string? hash = null, string name = UpdateService.InstallerName, string? url = null) =>
        JsonSerializer.SerializeToElement(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            assets = new object[]
            {
                new { name, browser_download_url = url ?? Root + name, digest = hash is null ? null : "sha256:" + hash },
                new { name = "SHA256SUMS.txt", browser_download_url = Root + "SHA256SUMS.txt", digest = (string?)null },
            }
        });

    [Fact]
    public void NextCheckFollowsConfiguredFrequency()
    {
        var now = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddHours(1), UpdateSchedule.NextCheck(now, UpdateCheckFrequency.Hourly));
        Assert.Equal(now.AddDays(1), UpdateSchedule.NextCheck(now, UpdateCheckFrequency.Daily));
        Assert.Equal(now.AddDays(7), UpdateSchedule.NextCheck(now, UpdateCheckFrequency.Weekly));
        Assert.Equal(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc), UpdateSchedule.NextCheck(now, UpdateCheckFrequency.Monthly));
        Assert.Equal(DateTime.MaxValue, UpdateSchedule.NextCheck(now, UpdateCheckFrequency.Never));
        Assert.Equal(UpdateCheckFrequency.Daily, UpdateSchedule.Normalize((UpdateCheckFrequency)99));
    }

    [Fact]
    public void ParseReleaseFiltersAppropriately()
    {
        var current = new Version(1, 3, 0);
        Assert.Equal(new Version(1, 4, 0), UpdateService.ParseRelease(Release(), current)?.Version);
        Assert.Null(UpdateService.ParseRelease(Release("v1.3.0"), current));
        Assert.Null(UpdateService.ParseRelease(Release("v1.2.3"), current));
        Assert.Null(UpdateService.ParseRelease(Release(prerelease: true), current));
        Assert.Null(UpdateService.ParseRelease(Release(name: "TerminalSessionKeeper.exe"), current));

        Assert.Throws<InvalidDataException>(() =>
            UpdateService.ParseRelease(Release(url: "https://example.com/setup.exe"), current));
    }

    [Fact]
    public async Task DownloadAndVerifySucceeds()
    {
        var current = new Version(1, 3, 0);
        var cache = Path.Combine(Path.GetTempPath(), "tsk-update-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new UpdateService(new FakeHandler(Payload, Digest));
            var update = (await service.CheckAsync(current, default))!;
            var path = await service.DownloadAsync(update, cache, default);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(Payload));

            var fallback = UpdateService.ParseRelease(Release(), current)!;
            var fallbackPath = await service.DownloadAsync(fallback, cache, default);
            Assert.True(File.Exists(fallbackPath));

            var bad = update with { Sha256 = new string('0', 64) };
            var before = Directory.GetFiles(cache, "*.exe", SearchOption.AllDirectories).Length;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(bad, cache, default));
            Assert.Equal(before, Directory.GetFiles(cache, "*.exe", SearchOption.AllDirectories).Length);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(update, cache, cts.Token));

            using var missing = new UpdateService(new FakeHandler(Payload, Digest, HttpStatusCode.NotFound));
            Assert.Null(await missing.CheckAsync(current, default));

            using var rateLimited = new UpdateService(new FakeHandler(Payload, Digest, HttpStatusCode.Forbidden));
            await Assert.ThrowsAsync<HttpRequestException>(() => rateLimited.CheckAsync(current, default));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    private sealed class FakeHandler(byte[] payload, string digest, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Headers.UserAgent.Count == 0) throw new Exception("Missing GitHub User-Agent");
            var url = request.RequestUri!.AbsoluteUri;
            var content = url.Contains("api.github.com")
                ? JsonSerializer.Serialize(new
                {
                    tag_name = "v1.4.0",
                    draft = false,
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = UpdateService.InstallerName,
                            browser_download_url = "https://github.com/alexlvcom/TerminalSessionKeeper/releases/download/v1.4.0/" + UpdateService.InstallerName,
                            digest = "sha256:" + digest
                        }
                    }
                })
                : digest + "  " + UpdateService.InstallerName;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = url.EndsWith(".exe") ? new ByteArrayContent(payload) : new StringContent(content)
            });
        }
    }
}
