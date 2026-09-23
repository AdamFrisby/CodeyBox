namespace CodeyBox.NtfyPlugin;

/// <summary>
/// Operator configuration for the ntfy notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.ntfy:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>names</em> of the environment variables holding the publish token and
/// the interaction signing secret are configured; the secrets themselves
/// travel the credential chain (environment).
///
/// <example>
/// <code>
/// {
///   "CodeyBox": {
///     "Plugins": {
///       "codeybox.ntfy": {
///         "Enabled": true,
///         "BaseUrl": "https://ntfy.example.invalid",
///         "DefaultTopic": "codeybox-fleet-9f2c",
///         "TokenEnvVar": "CODEYBOX_NTFY_TOKEN",
///         "InteractionSecretEnvVar": "CODEYBOX_NTFY_INTERACTION_SECRET"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </summary>
public sealed class NtfyPluginOptions
{
    /// <summary>Publish timeout applied when <see cref="PostTimeoutSeconds"/>
    /// is configured below its 1-second minimum.</summary>
    public const int DefaultPostTimeoutSeconds = 15;

    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the ntfy server, e.g. <c>https://ntfy.sh</c> or a
    /// self-hosted instance. Publishing goes to this server's root URL with the
    /// topic carried in the JSON body. Must be an absolute HTTPS origin —
    /// HTTP is accepted only for loopback, since publishes carry the bearer
    /// token and MAC-signed button bodies.</summary>
    public string BaseUrl { get; set; } = "https://ntfy.sh";

    /// <summary>Topic used when a notification carries no explicit recipient.
    /// Empty means "no default" — notifications without a resolvable topic are
    /// skipped with a warning. A topic name is effectively a password on
    /// public servers: pick something unguessable.</summary>
    public string DefaultTopic { get; set; } = string.Empty;

    /// <summary>Environment variable holding the ntfy access token
    /// (<c>tk_…</c>) used to authenticate publishes to ACL-protected topics.
    /// Optional: unset means publish without an Authorization header, which is
    /// correct for open topics. Never set the token itself in config.</summary>
    public string TokenEnvVar { get; set; } = "CODEYBOX_NTFY_TOKEN";

    /// <summary>Environment variable holding the shared secret used to mint
    /// the HMAC signature carried by each action button. The inbound side
    /// resolves the same secret via the interaction provider's
    /// <c>SigningSecretEnvVar</c> — point both at this same variable.
    /// Never set the secret itself in config.</summary>
    public string InteractionSecretEnvVar { get; set; } = "CODEYBOX_NTFY_INTERACTION_SECRET";

    /// <summary>Public base URL of this CodeyBox host as reachable from the
    /// operator's <em>device</em> — the ntfy client invokes action buttons
    /// itself, so this must resolve on the phone/browser holding the
    /// subscription. Empty falls back to <c>CodeyBox:PublicBaseUrl</c>.
    /// HTTPS required (HTTP only for loopback); when no usable value exists,
    /// buttons degrade to answer links.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// link (Agnes steers; this integration only links).</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>How offered actions render: <c>Buttons</c> publishes native
    /// ntfy <c>http</c> actions that call back into the verified
    /// <c>/webhooks/interactions/ntfy</c> endpoint (requires the operator's
    /// device to reach that URL); <c>Links</c> renders answer/Agnes view links
    /// only, for deployments with no inbound path. Default <c>Buttons</c>.</summary>
    public NtfyActionsMode ActionsMode { get; set; } = NtfyActionsMode.Buttons;

    /// <summary>Per-call timeout in seconds for ntfy publish calls.
    /// Must be &gt;= 1. Default 15.</summary>
    public int PostTimeoutSeconds { get; set; } = DefaultPostTimeoutSeconds;

    /// <summary>Maximum UTF-8 bytes kept for the notification message body
    /// before truncation (ntfy measures the limit in bytes, so this bound is
    /// applied to the encoded length, not the char count). Must be &gt;= 1.
    /// Default 3800 — under ntfy's 4096-byte message ceiling even with
    /// multi-byte text.</summary>
    public int MaxMessageBytes { get; set; } = 3800;

    /// <summary>Maximum characters kept for the notification title.
    /// Must be &gt;= 1. Default 200 (ntfy's title limit is 1 KB).</summary>
    public int MaxTitleChars { get; set; } = 200;

    /// <summary>Maximum action buttons rendered per message. Must be
    /// &gt;= 0. Default 3 — ntfy accepts at most three actions; surplus
    /// answers stay answerable via the answer link.</summary>
    public int MaxActions { get; set; } = 3;

    /// <summary>Maximum structured fields rendered into the message body.
    /// Must be &gt;= 0. Default 10.</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>How long correlation-token → topic entries live for the
    /// decision-update path. Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked message entries. Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>How offered <see cref="CodeyBox.Core.NotificationAction"/> values render.</summary>
public enum NtfyActionsMode
{
    /// <summary>Native ntfy <c>http</c> actions resolving through the verified
    /// inbound endpoint. Needs the operator's device to reach the host.</summary>
    Buttons,
    /// <summary><c>view</c> actions to the answer URL / Agnes only. For
    /// deployments with no inbound exposure.</summary>
    Links,
}
