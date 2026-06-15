using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Options-monitor cache that keeps serving the last successfully-created value
/// after a reload candidate fails validation.
/// </summary>
/// <remarks>
/// The stock <see cref="OptionsCache{TOptions}"/> removes the old value before
/// validating the reload candidate. If validation throws, the faulted lazy value
/// is cached and later <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> reads
/// rethrow until another reload happens. CodeyBox uses this cache only for
/// option roots where runtime consumers must keep operating on the last known
/// good value after a rejected config edit.
/// </remarks>
public sealed class RetainingOptionsMonitorCache<TOptions> : IOptionsMonitorCache<TOptions>
    where TOptions : class
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Action<TOptions>? _onSuccessfulCreate;

    public RetainingOptionsMonitorCache(Action<TOptions>? onSuccessfulCreate = null)
    {
        _onSuccessfulCreate = onSuccessfulCreate;
    }

    public RetainingOptionsMonitorCache(TOptions defaultValue, Action<TOptions>? onSuccessfulCreate = null)
    {
        _onSuccessfulCreate = onSuccessfulCreate;
        TryAdd(Options.DefaultName, defaultValue);
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
            entry.MarkRefreshPending();
    }

    public TOptions GetOrAdd(string? name, Func<TOptions> createOptions)
    {
        ArgumentNullException.ThrowIfNull(createOptions);

        var entry = _entries.GetOrAdd(Normalize(name), static _ => new Entry());
        return entry.GetOrCreate(createOptions, _onSuccessfulCreate);
    }

    public bool TryAdd(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = _entries.GetOrAdd(Normalize(name), static _ => new Entry());
        return entry.TryAdd(options);
    }

    public bool TryRemove(string? name)
    {
        return _entries.TryGetValue(Normalize(name), out var entry)
            && entry.MarkRefreshPending();
    }

    private static string Normalize(string? name) => name ?? Options.DefaultName;

    private sealed class Entry
    {
        private readonly Lock _gate = new();
        private TOptions? _value;
        private bool _hasValue;
        private bool _refreshPending = true;

        public TOptions GetOrCreate(Func<TOptions> createOptions, Action<TOptions>? onSuccessfulCreate)
        {
            lock (_gate)
            {
                if (_hasValue && !_refreshPending)
                    return _value!;

                try
                {
                    var next = createOptions();
                    _value = next;
                    _hasValue = true;
                    _refreshPending = false;
                    onSuccessfulCreate?.Invoke(next);
                    return next;
                }
                catch when (_hasValue)
                {
                    _refreshPending = false;
                    throw;
                }
            }
        }

        public bool TryAdd(TOptions options)
        {
            lock (_gate)
            {
                if (_hasValue)
                    return false;

                _value = options;
                _hasValue = true;
                _refreshPending = false;
                return true;
            }
        }

        public bool MarkRefreshPending()
        {
            lock (_gate)
            {
                if (!_hasValue)
                    return false;

                _refreshPending = true;
                return true;
            }
        }
    }
}
