using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// ILogger&lt;T&gt; that captures every Log call into an in-memory list so tests
/// can assert structured-logging templates and placeholder values. Both the
/// rendered message and the structured KV pairs from
/// <see cref="ILogger.Log{TState}"/>'s <c>state</c> argument are captured;
/// callers typically assert against <see cref="CapturedLogEntry.Properties"/>
/// because the message text rendering is sensitive to Microsoft.Extensions.Logging
/// formatter behaviour.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLogEntry> _entries = new();
    private readonly object _gate = new();

    // Released once per captured entry so callers can await new entries
    // (push-based) via WaitForEntryAsync instead of polling a wall-clock
    // deadline that competes for CPU with the code under test under a starved
    // ThreadPool. Existing Entries-polling callers are unaffected; the signal
    // is opt-in.
    private readonly SemaphoreSlim _entryAdded = new(0, int.MaxValue);

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    /// <summary>
    /// Awaits the first captured entry matching <paramref name="predicate"/>,
    /// waking on each new log call rather than polling. Throws
    /// <see cref="TimeoutException"/> if none arrives before
    /// <paramref name="timeout"/> — a backstop against a genuine no-log
    /// regression, not the mechanism that makes the wait succeed, so it does
    /// not reintroduce wall-clock flakiness under ThreadPool starvation.
    /// </summary>
    public async Task<CapturedLogEntry> WaitForEntryAsync(
        Func<CapturedLogEntry, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            lock (_gate)
            {
                foreach (var e in _entries)
                    if (predicate(e)) return e;
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                string dump;
                lock (_gate)
                    dump = string.Join("\n", _entries.Select(e => $"[{e.Level}] {e.Message}"));
                throw new TimeoutException(
                    $"Expected log entry not observed within {timeout}. Captured entries:\n{dump}");
            }
            // Cap the wait so a release that raced the snapshot above is still
            // re-checked promptly; the signal makes the common path immediate.
            var wait = remaining < TimeSpan.FromMilliseconds(250)
                ? remaining
                : TimeSpan.FromMilliseconds(250);
            await _entryAdded.WaitAsync(wait).ConfigureAwait(false);
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
        {
            for (var i = 0; i < kvs.Count; i++)
            {
                var kv = kvs[i];
                props[kv.Key] = kv.Value;
            }
        }
        var entry = new CapturedLogEntry(
            logLevel,
            formatter(state, exception),
            props,
            exception);
        lock (_gate) _entries.Add(entry);
        _entryAdded.Release();
    }
}

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);
