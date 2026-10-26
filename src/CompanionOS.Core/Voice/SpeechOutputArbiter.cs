namespace CompanionOS.Core.Voice;

/// <summary>Serializes every source of speech through Iris's single output device.</summary>
internal sealed class SpeechOutputArbiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource _stopEpoch = new();
    private bool _isShutdown;

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = LinkCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await operation(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = LinkCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cancels current and queued speech; the next new request starts a fresh epoch.</summary>
    public void Stop()
    {
        CancellationTokenSource stoppedEpoch;
        lock (_stateLock)
        {
            if (_isShutdown) return;
            stoppedEpoch = _stopEpoch;
            _stopEpoch = new CancellationTokenSource();
        }
        CancelAndDispose(stoppedEpoch);
    }

    /// <summary>Cancels active and queued speech before the voice engine is torn down.</summary>
    public void Cancel()
    {
        CancellationTokenSource stoppedEpoch;
        lock (_stateLock)
        {
            if (_isShutdown) return;
            _isShutdown = true;
            stoppedEpoch = _stopEpoch;
        }
        _shutdown.Cancel();
        CancelAndDispose(stoppedEpoch);
    }

    /// <summary>Waits until the active speech operation has released the output chain.</summary>
    public async Task DrainAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private CancellationTokenSource LinkCancellation(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token, _stopEpoch.Token);
        }
    }

    private static void CancelAndDispose(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        finally { source.Dispose(); }
    }
}
