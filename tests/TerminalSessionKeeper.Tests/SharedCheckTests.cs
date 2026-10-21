using System.Net.Http;
using TerminalSessionKeeper.Services;
using Xunit;

public sealed class SharedCheckTests
{
    [Fact]
    public async Task ManualCheckJoinsRunningCheckInsteadOfBeingRefused()
    {
        var gate = new SharedCheck<string?>();
        var pending = new TaskCompletionSource<string?>();
        var runs = 0;

        var background = gate.RunAsync(() => { runs++; return pending.Task; });
        var manual = gate.RunAsync(() => { runs++; return Task.FromResult<string?>("second"); });

        Assert.True(gate.IsRunning);
        pending.SetResult("1.4.2");
        Assert.Equal("1.4.2", await background);
        Assert.Equal("1.4.2", await manual);
        Assert.Equal(1, runs);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task NextCheckRunsAfterSuccessFailureOrSynchronousCompletion()
    {
        var gate = new SharedCheck<string?>();

        Assert.Equal("sync", await gate.RunAsync(() => Task.FromResult<string?>("sync")));
        Assert.False(gate.IsRunning);

        await Assert.ThrowsAsync<HttpRequestException>(() => gate.RunAsync(async () =>
        {
            await Task.Yield();
            throw new HttpRequestException("offline");
        }));
        Assert.False(gate.IsRunning);

        Assert.Equal("again", await gate.RunAsync(async () => { await Task.Yield(); return "again"; }));
    }
}
