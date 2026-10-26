namespace CompanionOS.Core.Voice;

/// <summary>Serializes every source of speech through Iris's single output device.</summary>
internal sealed class SpeechOutputArbiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
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
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
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

    /// <summary>Cancels active and queued speech before the voice engine is torn down.</summary>
    public void Cancel() => _shutdown.Cancel();

    /// <summary>Waits until the active speech operation has released the output chain.</summary>
    public async Task DrainAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }
}
