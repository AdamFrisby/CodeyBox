using CodeyBox.Core;

namespace CodeyBox.MattermostPlugin;

/// <summary>
/// Operator configuration for the Mattermost notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.mattermost:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>name</em> of the environment variable holding the notification token is
/// configured; the token itself travels the credential chain (environment).
///
/// <para>The configured token is notification-only (a Mattermost personal
/// access token used solely for <c>POST /api/v4/posts</c>). It is deliberately
/// isolated from any future inbound authorization: this plugin exposes no
/// inbound route and holds no signing secret, so a future approval-callback
/// credential would be a separate env var consumed by separate code.</para>
/// </summary>
public sealed class MattermostPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the Mattermost server, e.g.
    /// <c>https://mattermost.example.invalid</c> (a path prefix such as
    /// <c>https://host/mattermost</c> is honoured). Required when
    /// <see cref="Enabled"/> — notifications are skipped with a warning
    /// until a valid absolute http/https base URL is configured.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Environment variable holding the Mattermost personal access
    /// token used for outbound posts. The token needs only
    /// <c>create_post</c> scope on the target channel. Never set the token
    /// directly in config.</summary>
    public string TokenEnvVar { get; set; } = "CODEYBOX_MATTERMOST_TOKEN";

    /// <summary>Permit a plain-http <see cref="ServerUrl"/>. Default false:
    /// the bearer token travels in the request header, so delivery to an
    /// http endpoint is refused with a warning unless the operator explicitly
    /// accepts the cleartext-token exposure of an internal deployment.</summary>
    public bool AllowPlainHttp { get; set; }

    /// <summary>Channel ID posted to when a notification carries no explicit
    /// recipient (the 26-character channel ID from Mattermost's channel
    /// menu, not the display name — the plugin never calls channel lookup
    /// or membership APIs). Empty means "no default" — notifications without
    /// a resolvable channel are skipped with a warning.</summary>
    public string DefaultChannelId { get; set; } = string.Empty;

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// link on work-item notifications (Agnes steers; this integration only
    /// links). Empty omits the link.</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>Post follow-up notifications for the same work item as
    /// threaded replies (<c>root_id</c> set to the first post's ID) so a
    /// long-running item reads as one conversation. Default true.</summary>
    public bool ThreadByWorkItem { get; set; } = true;

    /// <summary>Per-call timeout in seconds for Mattermost REST calls.
    /// Must be &gt;= 1 — an invalid value is overridden with the shared
    /// <see cref="NotificationDelivery.DefaultPostTimeoutSeconds"/> default
    /// and a warning.</summary>
    public int PostTimeoutSeconds { get; set; } = NotificationDelivery.DefaultPostTimeoutSeconds;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 4000 (well under Mattermost's
    /// 16383-character post limit).</summary>
    public int MaxTextChars { get; set; } = 4000;

    /// <summary>Maximum structured fields rendered per post. Must be
    /// &gt;= 0. Default 10.</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>Maximum bounded retries after an HTTP 429 rate-limit
    /// response, each waiting at most <see cref="MaxRateLimitWaitSeconds"/>.
    /// Clamped to 0–3. Default 1: one polite retry, then the notification
    /// is dropped with a warning rather than spamming a limited server.</summary>
    public int RateLimitMaxRetries { get; set; } = 1;

    /// <summary>Upper bound in seconds on a single 429 back-off wait (the
    /// server's <c>Retry-After</c> is honoured up to this cap; a larger ask
    /// is treated as "retry budget exceeded" and the notification is
    /// dropped with a warning). Must be &gt;= 1. Default 30.</summary>
    public int MaxRateLimitWaitSeconds { get; set; } = 30;

    /// <summary>How long thread-root, dedup, and ambiguity entries live.
    /// Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked thread roots / posted messages.
    /// Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}
