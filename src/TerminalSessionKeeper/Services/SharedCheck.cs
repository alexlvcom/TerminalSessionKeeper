namespace TerminalSessionKeeper.Services;

/// <summary>
/// Runs one update check at a time. A caller that arrives while a check is in flight awaits that
/// same check and gets its result, instead of being refused.
/// </summary>
public sealed class SharedCheck<T>
{
    private Task<T>? _running;

    public bool IsRunning => _running is not null;

    public Task<T> RunAsync(Func<Task<T>> check)
    {
        if (_running is not null) return _running;
        var task = RunAndClearAsync(check);
        // A check that finished synchronously must not be remembered as running.
        if (!task.IsCompleted) _running = task;
        return task;
    }

    private async Task<T> RunAndClearAsync(Func<Task<T>> check)
    {
        try { return await check(); }
        finally { _running = null; }
    }
}
