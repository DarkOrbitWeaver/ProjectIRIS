using System.Text;
using System.Threading;

namespace CompanionOS.Core.Diagnostics;

/// <summary>
/// The event diary: everything she does, as machine-readable facts.
///
/// <see cref="CompanionLog"/> is the readable story. This is the truth record. One JSON
/// line per event, appended in order, one file per day, so a session can be replayed
/// or diffed: every tool call with its duration and tier, every brain turn, every
/// synthesis, every permission decision, every crash. Fixing bugs and improving her
/// means reading this, not guessing.
///
/// Nothing here throws. A logger that can crash the app is worse than no logger.
/// </summary>
public static class IrisEvent
{
    private const int RememberedEvents = 1000;
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private static readonly object WriteGate = new();
    private static readonly Queue<EventRecord> Events = new();
    private static readonly AsyncLocal<string?> CorrelationId = new();
    private static RollingJsonLinesWriter? _writer;

    /// <summary>Raised for every event, so the diagnostics panel can live-tail it.</summary>
    public static event Action<EventRecord>? EventLogged;

    /// <summary>Points the diary at a folder. Call once from <see cref="CompanionLog.Configure"/>.</summary>
    public static void Configure(string directory)
    {
        try
        {
            lock (WriteGate)
            {
                _writer = new RollingJsonLinesWriter(directory, "iris", MaxFileBytes);
            }
        }
        catch (Exception failure)
        {
            System.Diagnostics.Debug.WriteLine($"Event log configuration failed: {failure.Message}");
        }
    }

    /// <summary>The event file currently being written.</summary>
    public static string FilePath
    {
        get { lock (WriteGate) { return _writer?.ActivePath ?? string.Empty; } }
    }

    /// <summary>The correlation ID flowing through the current async operation, if any.</summary>
    public static string? CurrentCorrelationId => CorrelationId.Value;

    /// <summary>
    /// Associates subsequent events in this async flow with one interaction. Nested scopes
    /// restore their parent when disposed, and the value flows across awaits and Task.Run.
    /// </summary>
    public static IDisposable BeginCorrelationScope(string? correlationId = null)
    {
        string? previous = CorrelationId.Value;
        string current = string.IsNullOrWhiteSpace(correlationId)
            ? previous ?? Guid.NewGuid().ToString("N")[..8]
            : correlationId.Trim();
        CorrelationId.Value = current;
        return new CorrelationScope(previous, current);
    }

    /// <summary>Logs one fact. <paramref name="data"/> keys/values are written raw; keep them short and snake_case.</summary>
    public static void Log(string area, string @event, LogLevel level = LogLevel.Info,
        string? message = null, double? durationMs = null, IReadOnlyDictionary<string, string?>? data = null)
    {
        EventRecord record = EventRecord.Capture(area, @event, level, message, durationMs, data,
            CorrelationId.Value);
        Write(record);
    }

    /// <summary>Logs and event immediately, polite about construction time.</summary>
    public static void Log(EventRecord record) => Write(record);

    /// <summary>
    /// Wraps one duration-measured action. Writes `event.begin`, then `event.done` with
    /// the elapsed time — or `event.fail` with the exception, which is rethrown so the
    /// real error handling downstream stays in charge.
    /// </summary>
    public static async Task<T> StepAsync<T>(string area, string @event,
        Func<Task<T>> action, IReadOnlyDictionary<string, string?>? data = null)
    {
        long started = StopWatchStart();
        Log(area, @event + ".begin", LogLevel.Trace, data: data);
        try
        {
            T result = await action().ConfigureAwait(false);
            Log(area, @event + ".done", LogLevel.Debug, durationMs: StopWatchStop(started), data: data);
            return result;
        }
        catch (Exception failure)
        {
            Log(area, @event + ".fail", LogLevel.Error, $"{failure.GetType().Name}: {failure.Message}",
                StopWatchStop(started), data);
            throw;
        }
    }

    /// <summary>Synchronous twin of <see cref="StepAsync{T}"/>.</summary>
    public static T Step<T>(string area, string @event, Func<T> action,
        IReadOnlyDictionary<string, string?>? data = null)
    {
        long started = StopWatchStart();
        Log(area, @event + ".begin", LogLevel.Trace, data: data);
        try
        {
            T result = action();
            Log(area, @event + ".done", LogLevel.Debug, durationMs: StopWatchStop(started), data: data);
            return result;
        }
        catch (Exception failure)
        {
            Log(area, @event + ".fail", LogLevel.Error, $"{failure.GetType().Name}: {failure.Message}",
                StopWatchStop(started), data);
            throw;
        }
    }

    /// <summary>Voids forget the result, not the event. This overload exists so `() => Do()` still compiles.</summary>
    public static void Step(string area, string @event, Action action,
        IReadOnlyDictionary<string, string?>? data = null)
    {
        Step<object?>(area, @event, () =>
        {
            action();
            return null;
        }, data);
    }

    /// <summary>Most recent events, newest last, for the diagnostics panel.</summary>
    public static IReadOnlyList<EventRecord> Recent(int maximumEvents = 200)
    {
        lock (WriteGate)
        {
            int skip = Math.Max(0, Events.Count - maximumEvents);
            return Events.Skip(skip).ToArray();
        }
    }

    private static void Write(EventRecord record)
    {
        if (record.CorrelationId is null && CorrelationId.Value is { } activeCorrelationId)
        {
            record = new EventRecord
            {
                Timestamp = record.Timestamp,
                Level = record.Level,
                Area = record.Area,
                Event = record.Event,
                Message = record.Message,
                DurationMs = record.DurationMs,
                CorrelationId = activeCorrelationId,
                Data = record.Data
            };
        }

        lock (WriteGate)
        {
            Events.Enqueue(record);
            while (Events.Count > RememberedEvents)
            {
                Events.Dequeue();
            }

            if (_writer is not null)
            {
                try
                {
                    _writer.Append(record.Timestamp, record.ToJsonLine());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Disk busy/full or a malformed oversized event: in-memory history remains.
                    System.Diagnostics.Debug.WriteLine($"Event log write failed: {exception.Message}");
                }
            }
        }

        try
        {
            EventLogged?.Invoke(record);
        }
        catch (Exception listenerFailure)
        {
            System.Diagnostics.Debug.WriteLine($"Event listener failed: {listenerFailure.Message}");
        }
    }

    private static long StopWatchStart() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static double StopWatchStop(long started) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private sealed class CorrelationScope(string? previous, string current) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 &&
                string.Equals(CorrelationId.Value, current, StringComparison.Ordinal))
            {
                CorrelationId.Value = previous;
            }
        }
    }
}

/// <summary>One fact about what she did. Immutable; serialises to exactly one JSON line.</summary>
public sealed class EventRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public LogLevel Level { get; init; }
    public string Area { get; init; } = string.Empty;
    public string Event { get; init; } = string.Empty;
    public string? Message { get; init; }
    public double? DurationMs { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string?>? Data { get; init; }

    public static EventRecord Capture(string area, string @event, LogLevel level, string? message,
        double? durationMs, IReadOnlyDictionary<string, string?>? data, string? correlationId = null) => new()
    {
        Level = level,
        Area = string.IsNullOrWhiteSpace(area) ? "App" : area,
        // Keep the event name machine-friendly without panicking on a stray space.
        Event = (@event ?? "unknown").Trim().Replace(' ', '_'),
        Message = string.IsNullOrWhiteSpace(message) ? null : message,
        DurationMs = durationMs is > 0 ? Math.Round(durationMs.Value, 1) : null,
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
        Data = data is { Count: > 0 } ? data : null
    };

    public string Format()
    {
        string stamp = Timestamp.ToString("HH:mm:ss.fff");
        string duration = DurationMs is null ? string.Empty : $" {DurationMs.Value:0.#} ms";
        return $"{stamp} {Level,-7} {Area,-12} {Event}{duration}" +
               (Message is null ? string.Empty : $" — {Message}");
    }

    /// <summary>One line, hand-serialised: her busiest code path must not need a JsonWriter.</summary>
    public string ToJsonLine()
    {
        StringBuilder json = new(160);
        json.Append("{\"ts\":\"").Append(Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz")).Append('"');
        json.Append(",\"level\":\"").Append(Level.ToString().ToLowerInvariant()).Append('"');
        json.Append(",\"area\":\"").Append(Escape(Area)).Append('"');
        json.Append(",\"event\":\"").Append(Escape(Event)).Append('"');
        if (CorrelationId is not null)
        {
            json.Append(",\"correlation_id\":\"").Append(Escape(CorrelationId)).Append('"');
        }

        if (Message is not null)
        {
            json.Append(",\"msg\":\"").Append(Escape(Message)).Append('"');
        }

        if (DurationMs is { } milliseconds)
        {
            json.Append(",\"ms\":").Append(milliseconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Data is { } data)
        {
            json.Append(",\"data\":{");
            bool first = true;
            foreach (KeyValuePair<string, string?> pair in data)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append('"').Append(Escape(pair.Key)).Append("\":");
                if (pair.Value is null)
                {
                    json.Append("null");
                }
                else
                {
                    json.Append('"').Append(Escape(pair.Value)).Append('"');
                }
            }

            json.Append('}');
        }

        json.Append('}');
        return json.ToString();
    }

    private static string Escape(string value)
    {
        int needed = value.Length;
        foreach (char c in value)
        {
            if (c is '"' or '\\' or '\n' or '\r' or '\t')
            {
                needed++;
            }
        }

        if (needed == value.Length)
        {
            return value;
        }

        StringBuilder escaped = new(needed);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }
}
