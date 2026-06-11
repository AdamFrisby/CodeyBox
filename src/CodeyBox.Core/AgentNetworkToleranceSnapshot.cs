using System;
using System.Collections.Generic;
using System.Threading;

namespace CodeyBox.Core;

/// <summary>
/// Shared, swappable holder for the current per-agent network tolerance options.
/// Registered as a DI singleton so every runner reads through the same
/// reference. The hot-reload coordinator updates this holder via
/// <see cref="Replace"/>, and subsequent agent runs pick up the new
/// tolerance settings without a process restart.
/// </summary>
public sealed class AgentNetworkToleranceSnapshot
{
    private IReadOnlyDictionary<string, AgentNetworkToleranceOptions> _current;

    public AgentNetworkToleranceSnapshot(IReadOnlyDictionary<string, AgentNetworkToleranceOptions> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public IReadOnlyDictionary<string, AgentNetworkToleranceOptions> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyDictionary<string, AgentNetworkToleranceOptions> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }

    public int GetCodexRequestMaxRetries()
    {
        if (Current.TryGetValue("codex", out var opts) && opts.RequestMaxRetries.HasValue)
        {
            return opts.RequestMaxRetries.Value;
        }
        return AgentNetworkToleranceOptions.DefaultCodexRequestMaxRetries;
    }

    public int GetCodexStreamMaxRetries()
    {
        if (Current.TryGetValue("codex", out var opts) && opts.StreamMaxRetries.HasValue)
        {
            return opts.StreamMaxRetries.Value;
        }
        return AgentNetworkToleranceOptions.DefaultCodexStreamMaxRetries;
    }

    public int? GetCodexStreamIdleTimeoutMs()
    {
        if (Current.TryGetValue("codex", out var opts) && opts.StreamIdleTimeoutMs.HasValue)
        {
            return opts.StreamIdleTimeoutMs.Value;
        }
        return null;
    }

    public int? GetClaudeApiTimeoutMs()
    {
        if (Current.TryGetValue("claude", out var opts) && opts.ApiTimeoutMs.HasValue)
        {
            return opts.ApiTimeoutMs.Value;
        }
        return null;
    }
}
