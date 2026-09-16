namespace TerminalSessionKeeper.Services;

/// <summary>
/// Guarantees one tray icon per user session. A second launch signals the running instance to
/// show itself and then exits, so double-clicking the executable does not add another icon —
/// and, more importantly, two instances cannot snapshot or restore at the same time.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TerminalSessionKeeper.SingleInstance";
    private const string SignalName = @"Local\TerminalSessionKeeper.ActivateSignal";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private CancellationTokenSource? _listenerCancellation;

    public bool IsFirstInstance { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        _signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SignalName);
    }

    /// <summary>Called from a second instance to wake the first one.</summary>
    public void SignalExistingInstance() => _signal.Set();

    /// <summary>
    /// Starts a background listener that invokes <paramref name="onSignal"/> whenever another
    /// instance is launched. The callback is raised on a worker thread.
    /// </summary>
    public void StartListening(Action onSignal)
    {
        _listenerCancellation = new CancellationTokenSource();
        var token = _listenerCancellation.Token;

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _signal, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                // Blocking wait — no polling, so an idle app consumes no CPU.
                if (WaitHandle.WaitAny(handles) == 0) onSignal();
            }
        })
        {
            IsBackground = true,
            Name = "TerminalSessionKeeper.InstanceListener",
        };

        thread.Start();
    }

    public void Dispose()
    {
        _listenerCancellation?.Cancel();
        _listenerCancellation?.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned on this thread during shutdown; nothing to release.
            }
        }

        _signal.Dispose();
        _mutex.Dispose();
    }
}
