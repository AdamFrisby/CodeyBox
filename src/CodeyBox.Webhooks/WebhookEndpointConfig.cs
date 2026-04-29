namespace CodeyBox.Webhooks;

/// <summary>
/// Configuration for a single outbound webhook endpoint.
/// </summary>
public sealed record WebhookEndpointConfig
{
    /// <summary>Human-readable label used in logs. Must be unique across endpoints.</summary>
    public required string Name { get; init; }

    /// <summary>HTTPS (or HTTP) URL to POST events to.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// Name of the environment variable that holds the HMAC-SHA256 signing secret.
    /// When unset, requests are sent unsigned (no X-CodeyBox-Signature header).
    /// The secret value must never appear in config — only the env-var name.
    /// </summary>
    public string? SecretEnvVar { get; init; }

    /// <summary>
    /// Optional allow-list of event names (e.g. "work_item.audit_passed").
    /// When absent or empty, ALL events are delivered to this endpoint.
    /// </summary>
    public IReadOnlyList<string> EventFilter { get; init; } = [];

    /// <summary>Maximum delivery attempts per event. Exponential back-off between retries.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Seconds to wait before the first retry. Doubles each subsequent attempt.</summary>
    public int InitialBackoffSeconds { get; init; } = 1;

    /// <summary>Per-request HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;
}
