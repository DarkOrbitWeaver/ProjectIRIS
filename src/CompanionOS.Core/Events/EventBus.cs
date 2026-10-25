using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Events;

/// <summary>
/// The nervous system. Every part of her publishes here, every screen listens here.
///
/// Design choices worth knowing:
///   * Subscribers are stored per event type in an immutable array, so publishing is
///     allocation-free and never blocks on a lock while handlers run;
///   * A handler that throws is logged and skipped. One broken screen can never stop
///     her from thinking;
///   * Publishing is synchronous. Handlers must return fast, so screens post their
///     work to the UI thread instead of doing it inline.
/// </summary>
public sealed class EventBus
{
    private readonly object _subscriptionLock = new();
    private readonly Dictionary<Type, Delegate[]> _subscribers = new();

    /// <summary>Total number of events published since start, shown in diagnostics.</summary>
    private long _publishedCount;

    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>Subscribes to one event type. Dispose the result to stop listening.</summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : CompanionEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_subscriptionLock)
        {
            _subscribers.TryGetValue(typeof(TEvent), out Delegate[]? existing);
            Delegate[] updated = new Delegate[(existing?.Length ?? 0) + 1];
            existing?.CopyTo(updated, 0);
            updated[^1] = handler;
            _subscribers[typeof(TEvent)] = updated;
        }

        return new Subscription(() =>
        {
            lock (_subscriptionLock)
            {
                if (!_subscribers.TryGetValue(typeof(TEvent), out Delegate[]? current))
                {
                    return;
                }

                List<Delegate> remaining = new(current);
                remaining.Remove(handler);
                _subscribers[typeof(TEvent)] = remaining.ToArray();
            }
        });
    }

    /// <summary>Publishes one event to every current subscriber.</summary>
    public void Publish<TEvent>(TEvent payload) where TEvent : CompanionEvent
    {
        Interlocked.Increment(ref _publishedCount);

        Delegate[] handlers;
        lock (_subscriptionLock)
        {
            if (!_subscribers.TryGetValue(typeof(TEvent), out Delegate[]? existing) || existing.Length == 0)
            {
                return;
            }

            handlers = existing;
        }

        foreach (Delegate handler in handlers)
        {
            try
            {
                ((Action<TEvent>)handler)(payload);
            }
            catch (Exception exception)
            {
                CompanionLog.Error("EventBus", $"A subscriber to {typeof(TEvent).Name} threw.", exception);
            }
        }
    }

    /// <summary>Convenience so callers can fire events without holding a generic parameter.</summary>
    public void PublishNotice(string message, NoticeSeverity severity = NoticeSeverity.Info,
        string? actionLabel = null, Action? action = null)
    {
        Publish(new NoticeEvent
        {
            Message = message,
            Severity = severity,
            ActionLabel = actionLabel,
            Action = action
        });
    }

    /// <summary>Removes a subscription when disposed.</summary>
    private sealed class Subscription : IDisposable
    {
        private Action? _unsubscribe;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Action? action = _unsubscribe;
            _unsubscribe = null;
            action?.Invoke();
        }
    }
}
