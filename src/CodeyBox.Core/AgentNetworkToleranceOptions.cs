namespace CodeyBox.Core;

/// <summary>
/// Network tolerance options for agent CLIs.
/// Bound under <c>CodeyBox:AgentNetworkTolerance</c>.
/// </summary>
public sealed class AgentNetworkToleranceOptions
{
    /// <summary>
    /// Default HTTP request retries for Codex CLI (resilient value: 8, vendor default: 4).
    /// </summary>
    public const int DefaultCodexRequestMaxRetries = 8;

    /// <summary>
    /// Default streaming reconnect attempts for Codex CLI (resilient value: 15, vendor default: 5).
    /// </summary>
    public const int DefaultCodexStreamMaxRetries = 15;

    /// <summary>
    /// HTTP request retries for Codex CLI (vendor default 4).
    /// </summary>
    public int? RequestMaxRetries { get; set; }

    /// <summary>
    /// Streaming reconnect attempts for Codex CLI (vendor default 5).
    /// </summary>
    public int? StreamMaxRetries { get; set; }

    /// <summary>
    /// Idle wait before treating a stream as lost in ms for Codex CLI (vendor default 300000).
    /// </summary>
    public int? StreamIdleTimeoutMs { get; set; }

    /// <summary>
    /// controls the HTTP request timeout in ms for Claude Code (vendor default 600000).
    /// Defaults to unset.
    /// </summary>
    public int? ApiTimeoutMs { get; set; }
}
