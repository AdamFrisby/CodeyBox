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

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
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
    }
}

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);
