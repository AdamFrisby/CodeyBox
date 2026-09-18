namespace CodeyBox.Notifications;

/// <summary>
/// Hot-reloadable options for the generic inbound interaction endpoint
/// (<c>POST /interactions/{provider}</c>). Bound from
/// <c>CodeyBox:Notifications:Interactions</c>. Disabled by default so a
/// deployment using only outbound providers is never forced to expose or
/// configure an inbound path.
/// </summary>
public sealed class InteractionsOptions
{
    /// <summary>Master switch for the inbound interaction endpoint.
    /// Default false: the endpoint refuses everything until an operator
    /// enables it and configures at least one provider.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-provider verification and authorisation settings.</summary>
    public List<InteractionProviderOptions> Providers { get; set; } = [];
}

/// <summary>
/// Verification and authorisation settings for one interaction provider.
/// Secrets are NEVER set directly in config: <see cref="SigningSecretEnvVar"/>
/// names an environment variable holding the signing secret.
/// </summary>
public sealed class InteractionProviderOptions
{
    /// <summary>Provider name as it appears in <c>/interactions/{provider}</c>.
    /// Matched exactly (case-insensitive); unknown names are refused.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Verification scheme: "hmac-sha256" (generic) or "slack-v0".</summary>
    public string Scheme { get; set; } = "hmac-sha256";

    /// <summary>Environment variable holding the provider signing secret.
    /// Never set the secret directly in config.</summary>
    public string? SigningSecretEnvVar { get; set; }

    /// <summary>Request header carrying the signature. Defaults per scheme
    /// (<c>X-CodeyBox-Signature</c>, or <c>X-Slack-Signature</c> for slack-v0).</summary>
    public string? SignatureHeader { get; set; }

    /// <summary>Request header carrying the sender timestamp used for replay
    /// protection. Defaults per scheme (<c>X-CodeyBox-Timestamp</c>, or
    /// <c>X-Slack-Request-Timestamp</c> for slack-v0).</summary>
    public string? TimestampHeader { get; set; }

    /// <summary>Maximum age of a signed interaction before it is rejected as
    /// a replay. Default 5 minutes.</summary>
    public TimeSpan ReplayWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional exact-match allowlist of channel/workspace IDs the
    /// interaction must originate from. Empty means any channel on the
    /// verified provider may act (connecting the integration is the grant).</summary>
    public List<string> AllowedChannels { get; set; } = [];

    /// <summary>Optional exact-match allowlist of platform user IDs. Empty
    /// means any member of the connected channel may act; setting it narrows
    /// the grant to named users. Compared by exact ordinal equality.</summary>
    public List<string> AllowedUsers { get; set; } = [];
}
