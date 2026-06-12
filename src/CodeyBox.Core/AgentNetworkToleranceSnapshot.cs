using System;
using System.Collections.Generic;
using System.Threading;

namespace CodeyBox.Core;

/// <summary>
/// Shared, swappable holder for the current per-agent network tolerance options.
/// Registered as a DI singleton so every runner reads through the same
/// reference. The hot-reload coordinator updates this holder,
/// and subsequent agent runs pick up the new settings without a process restart.
/// </summary>
public sealed class AgentNetworkToleranceSnapshot
{
    private IReadOnlyDictionary<string, AgentNetworkToleranceOptions> _current;

    public AgentNetworkToleranceSnapshot(IReadOnlyDictionary<string, AgentNetworkToleranceOptions?> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = CopyTolerance(initial);
    }

    public IReadOnlyDictionary<string, AgentNetworkToleranceOptions> Current => Volatile.Read(ref _current);

    public void Replace(IReadOnlyDictionary<string, AgentNetworkToleranceOptions?> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, CopyTolerance(next));
    }

    public AgentNetworkToleranceOptions? GetTolerance(string agentKind)
    {
        if (Current.TryGetValue(agentKind, out var dict))
        {
            return dict;
        }
        return null;
    }

    private static Dictionary<string, AgentNetworkToleranceOptions> CopyTolerance(
        IReadOnlyDictionary<string, AgentNetworkToleranceOptions?> source)
    {
        var copy = new Dictionary<string, AgentNetworkToleranceOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            if (kvp.Value != null)
            {
                copy[kvp.Key] = kvp.Value.Clone();
            }
        }
        AgentNetworkToleranceOptions.ApplyDocumentedDefaults(copy);
        return copy;
    }
}

/// <summary>
/// Typed per-agent network tolerance settings surfaced at
/// <c>CodeyBox:AgentNetworkTolerance:&lt;agent&gt;</c>. Null properties mean
/// "leave the vendor default alone" unless a documented CodeyBox default is
/// applied for that agent.
/// </summary>
public sealed class AgentNetworkToleranceOptions
{
    public const string CodexAgentKind = "codex";
    public const string ClaudeAgentKind = "claude";
    public const int MaximumCliNetworkTimeoutMs = 480 * 60 * 1000;

    /// <summary>
    /// CodeyBox default for Codex HTTP request retries. Vendor default is 4.
    /// </summary>
    public const int CodexDefaultRequestMaxRetries = 8;

    /// <summary>
    /// CodeyBox default for Codex streaming reconnect retries. Vendor default is 5.
    /// </summary>
    public const int CodexDefaultStreamMaxRetries = 15;

    /// <summary>
    /// Maximum retry count accepted by Codex CLI network retry knobs.
    /// </summary>
    public const int CodexMaximumRetries = 100;

    /// <summary>
    /// Maximum Codex stream-idle timeout. Matches the maximum work-attempt
    /// timeout accepted by the API so one CLI wait cannot outlive the dispatch
    /// window.
    /// </summary>
    public const int CodexMaximumStreamIdleTimeoutMs = MaximumCliNetworkTimeoutMs;

    /// <summary>
    /// Maximum Claude API timeout. Matches the maximum work-attempt timeout
    /// accepted by the API so one CLI request cannot outlive the dispatch window.
    /// </summary>
    public const int ClaudeMaximumApiTimeoutMs = MaximumCliNetworkTimeoutMs;

    /// <summary>HTTP request retry count. Used by Codex as request_max_retries.</summary>
    public int? RequestMaxRetries { get; set; }

    /// <summary>Streaming reconnect retry count. Used by Codex as stream_max_retries.</summary>
    public int? StreamMaxRetries { get; set; }

    /// <summary>Stream idle timeout in milliseconds. Used by Codex as stream_idle_timeout_ms when configured.</summary>
    public int? StreamIdleTimeoutMs { get; set; }

    /// <summary>
    /// Optional Codex model provider id for the injected provider overrides.
    /// When unset, the runner derives the provider from the effective model id,
    /// falling back to <c>openai</c>.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Optional Claude Code API timeout in milliseconds. Null deliberately
    /// leaves Claude's own API_TIMEOUT_MS default untouched.
    /// </summary>
    public int? ApiTimeoutMs { get; set; }

    public static AgentNetworkToleranceOptions CodexDefaults() => new()
    {
        RequestMaxRetries = CodexDefaultRequestMaxRetries,
        StreamMaxRetries = CodexDefaultStreamMaxRetries,
    };

    public static AgentNetworkToleranceOptions WithCodexDefaults(AgentNetworkToleranceOptions? configured)
    {
        var resolved = configured?.Clone() ?? new AgentNetworkToleranceOptions();
        resolved.RequestMaxRetries ??= CodexDefaultRequestMaxRetries;
        resolved.StreamMaxRetries ??= CodexDefaultStreamMaxRetries;
        return resolved;
    }

    public static Dictionary<string, AgentNetworkToleranceOptions?> DefaultByAgent() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CodexAgentKind] = CodexDefaults(),
        };

    public static bool IsValidCodexProviderId(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return false;

        foreach (var ch in providerId)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
                return false;
        }

        return true;
    }

    internal static void ApplyDocumentedDefaults(Dictionary<string, AgentNetworkToleranceOptions> tolerance)
    {
        tolerance[CodexAgentKind] = WithCodexDefaults(
            tolerance.TryGetValue(CodexAgentKind, out var codex) ? codex : null);
    }

    public AgentNetworkToleranceOptions Clone() => new()
    {
        RequestMaxRetries = RequestMaxRetries,
        StreamMaxRetries = StreamMaxRetries,
        StreamIdleTimeoutMs = StreamIdleTimeoutMs,
        Provider = Provider,
        ApiTimeoutMs = ApiTimeoutMs,
    };
}
