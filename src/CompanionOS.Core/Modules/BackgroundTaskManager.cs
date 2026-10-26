using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Modules;

public enum BackgroundTaskState
{
    Queued,
    Running,
    CancelRequested,
    Completed,
    Failed,
    Cancelled
}

public sealed record BackgroundTaskSnapshot(
    string Id,
    string ModuleId,
    string ModuleName,
    string ToolName,
    BackgroundTaskState State,
    string CurrentStep,
    double? Progress,
    double? Total,
    string? Result,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The narrow task controls made available to Iris's own task tools.</summary>
public interface IBackgroundTaskAccess
{
    BackgroundTaskSnapshot? Find(string taskId);
    IReadOnlyList<BackgroundTaskSnapshot> ListRecent(int maximumEntries = 8);
    bool Cancel(string taskId, out string message);
}

/// <summary>
/// Runs explicitly background-capable module tools outside the assistant turn. The
/// small queue prevents an unbounded pile of child processes while keeping a task's
/// state, progress, cancellation and result available after the originating turn ends.
/// </summary>
public sealed class BackgroundTaskManager : IBackgroundTaskAccess, IAsyncDisposable
{
    private const int MaximumRetainedTasks = 32;
    private const int MaximumQueuedTasks = 4;
    private const int MaximumConcurrentTasks = 2;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly SemaphoreSlim _slots = new(MaximumConcurrentTasks, MaximumConcurrentTasks);
    private readonly EventBus _events;
    private readonly IDisposable _progressSubscription;
    private bool _disposed;

    public BackgroundTaskManager(EventBus events)
    {
        _events = events;
        _progressSubscription = events.Subscribe<ModuleProgressEvent>(OnProgress);
    }

    public string Start(string moduleId, string moduleName, string toolName,
        Func<string, CancellationToken, Task<ToolResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int liveCount = _tasks.Values.Count(task => IsLive(task.Snapshot.State));
            if (liveCount >= MaximumQueuedTasks)
            {
                throw new InvalidOperationException("Iris already has four queued or running module tasks. Let one finish first.");
            }

            string id = "task-" + Guid.NewGuid().ToString("N")[..10];
            DateTimeOffset now = DateTimeOffset.Now;
            entry = new Entry(id, moduleId, moduleName, toolName, now);
            _tasks.Add(id, entry);
            _order.Enqueue(id);
            TrimHistory();
            Publish(entry.Snapshot);
            entry.Execution = Task.Run(() => RunAsync(entry, execute));
        }

        return entry.Id;
    }

    public BackgroundTaskSnapshot? Find(string taskId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out Entry? entry) ? entry.Snapshot : null;
        }
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListRecent(int maximumEntries = 8)
    {
        int maximum = Math.Clamp(maximumEntries, 1, MaximumRetainedTasks);
        lock (_gate)
        {
            return _order.Reverse()
                .Select(id => _tasks.GetValueOrDefault(id)?.Snapshot)
                .Where(snapshot => snapshot is not null)
                .Take(maximum)
                .Cast<BackgroundTaskSnapshot>()
                .ToList();
        }
    }

    public bool Cancel(string taskId, out string message)
    {
        BackgroundTaskSnapshot? updated = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out Entry? entry))
            {
                message = "I could not find that background task.";
                return false;
            }
            if (!IsLive(entry.Snapshot.State))
            {
                message = $"Task {entry.Id} is already {entry.Snapshot.State.ToString().ToLowerInvariant()}.";
                return false;
            }

            cancellation = entry.Cancellation;
            updated = entry.Snapshot with
            {
                State = BackgroundTaskState.CancelRequested,
                CurrentStep = "Stopping",
                UpdatedAt = DateTimeOffset.Now
            };
            entry.Snapshot = updated;
            message = $"Asked {entry.Snapshot.ModuleName} to stop task {entry.Id}.";
        }
        cancellation.Cancel();
        Publish(updated);
        return true;
    }

    private async Task RunAsync(Entry entry,
        Func<string, CancellationToken, Task<ToolResult>> execute)
    {
        bool acquired = false;
        try
        {
            await _slots.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            SetState(entry, BackgroundTaskState.Running, "Working", null);
            ToolResult result = await execute(entry.Id, entry.Cancellation.Token).ConfigureAwait(false);
            bool cancelled = entry.Cancellation.IsCancellationRequested;
            SetState(entry,
                cancelled ? BackgroundTaskState.Cancelled :
                    result.Succeeded ? BackgroundTaskState.Completed : BackgroundTaskState.Failed,
                cancelled ? "Stopped" : result.Succeeded ? "Done" : "Failed",
                cancelled ? "Cancelled by the user." : Clip(result.DisplayText ?? result.ModelText));
        }
        catch (OperationCanceledException)
        {
            SetState(entry, BackgroundTaskState.Cancelled, "Stopped", "Cancelled by the user.");
        }
        catch (Exception exception)
        {
            CompanionLog.Error("BackgroundTasks", $"Task '{entry.Id}' failed.", exception);
            SetState(entry, BackgroundTaskState.Failed, "Failed", Clip(exception.Message));
        }
        finally
        {
            if (acquired)
            {
                _slots.Release();
            }
        }
    }

    private void OnProgress(ModuleProgressEvent progress)
    {
        BackgroundTaskSnapshot? updated = null;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(progress.TurnId, out Entry? entry) ||
                !string.Equals(entry.Snapshot.ModuleId, progress.ModuleId, StringComparison.OrdinalIgnoreCase) ||
                !IsLive(entry.Snapshot.State))
            {
                return;
            }

            updated = entry.Snapshot with
            {
                CurrentStep = Clip(progress.Message) ?? entry.Snapshot.CurrentStep,
                Progress = progress.Progress,
                Total = progress.Total,
                UpdatedAt = DateTimeOffset.Now
            };
            entry.Snapshot = updated;
        }
        Publish(updated);
    }

    private void SetState(Entry entry, BackgroundTaskState state, string step, string? result)
    {
        BackgroundTaskSnapshot updated;
        lock (_gate)
        {
            updated = entry.Snapshot with
            {
                State = state,
                CurrentStep = step,
                Result = result,
                UpdatedAt = DateTimeOffset.Now
            };
            entry.Snapshot = updated;
            TrimHistory();
        }
        Publish(updated);
    }

    private void TrimHistory()
    {
        while (_tasks.Count > MaximumRetainedTasks && _order.Count > 0)
        {
            string oldest = _order.Peek();
            if (_tasks.TryGetValue(oldest, out Entry? entry) && IsLive(entry.Snapshot.State))
            {
                break;
            }
            _order.Dequeue();
            _tasks.Remove(oldest);
        }
    }

    private void Publish(BackgroundTaskSnapshot snapshot) =>
        _events.Publish(new BackgroundTaskChangedEvent { Task = snapshot });

    private static bool IsLive(BackgroundTaskState state) =>
        state is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.CancelRequested;

    private static string? Clip(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= 1000 ? value : value[..1000];

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        CancellationTokenSource[] cancellations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            cancellations = _tasks.Values.Where(entry => IsLive(entry.Snapshot.State))
                .Select(entry => entry.Cancellation).ToArray();
            running = _tasks.Values.Select(entry => entry.Execution).Where(task => task is not null)
                .Cast<Task>().ToArray();
        }

        _progressSubscription.Dispose();
        foreach (CancellationTokenSource cancellation in cancellations)
        {
            cancellation.Cancel();
        }
        bool stopped = false;
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            stopped = true;
        }
        catch (TimeoutException)
        {
            CompanionLog.Warning("BackgroundTasks", "Some module tasks did not stop within five seconds.");
        }
        catch (OperationCanceledException)
        {
            // Shutdown already requested cancellation for each task.
        }
        if (stopped)
        {
            foreach (Entry entry in _tasks.Values)
            {
                entry.Cancellation.Dispose();
            }
            _slots.Dispose();
        }
    }

    private sealed class Entry(string id, string moduleId, string moduleName, string toolName, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Execution { get; set; }
        public BackgroundTaskSnapshot Snapshot { get; set; } = new(
            id, moduleId, moduleName, toolName, BackgroundTaskState.Queued,
            "Queued", null, null, null, startedAt, startedAt);
    }
}
