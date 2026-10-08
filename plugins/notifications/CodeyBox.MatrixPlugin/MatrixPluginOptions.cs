using CodeyBox.Core;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Operator configuration for the Matrix notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.matrix:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>name</em> of the environment variable holding the access token is
/// configured; the token itself travels the credential chain (environment).
///
/// <example>
/// <code>
/// {
///   "CodeyBox": {
///     "Plugins": {
///       "codeybox.matrix": {
///         "Enabled": true,
///         "HomeserverUrl": "https://matrix.example.invalid",
///         "AccessTokenEnvVar": "CODEYBOX_MATRIX_ACCESS_TOKEN",
///         "DefaultRoomId": "!codeybox:example.invalid",
///         "AllowedRoomIds": ["!codeybox:example.invalid"],
///         "AgnesBaseUrl": "https://agnes.example.invalid"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </summary>
public sealed class MatrixPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the Matrix homeserver, e.g.
    /// <c>https://matrix.example.invalid</c> (a path prefix behind a reverse
    /// proxy is honoured). Required when <see cref="Enabled"/> — must be a
    /// clean absolute http/https URL with no query, fragment, or user-info.</summary>
    public string HomeserverUrl { get; set; } = string.Empty;

    /// <summary>Environment variable holding the Matrix access token (the
    /// user/device token that authorises Client-Server API calls). Never set
    /// the token directly in config.</summary>
    public string AccessTokenEnvVar { get; set; } = "CODEYBOX_MATRIX_ACCESS_TOKEN";

    /// <summary>Room ID posted to when a notification carries no explicit
    /// recipient, e.g. <c>!codeybox:example.invalid</c>. Empty means "no
    /// default" — notifications without a resolvable room are skipped with a
    /// warning. The room must also be allowlisted (see
    /// <see cref="AllowedRoomIds"/>).</summary>
    public string DefaultRoomId { get; set; } = string.Empty;

    /// <summary>Room IDs this plugin may send to. A notification targets the
    /// first non-empty recipient, else <see cref="DefaultRoomId"/> — and the
    /// resolved room must exactly match an entry here (or
    /// <see cref="DefaultRoomId"/> when this list is empty). Exact-match
    /// only: untrusted notification content can never widen the set. The
    /// plugin performs no room creation, join, invite, or membership change —
    /// every room must already exist with the sender joined.</summary>
    public List<string> AllowedRoomIds { get; set; } = [];

    /// <summary>Permit a plain-http <see cref="HomeserverUrl"/>. Default
    /// false: the access token travels in an <c>Authorization</c> header, so
    /// delivery to an http endpoint is refused with a warning unless the
    /// operator explicitly accepts the cleartext-token exposure.</summary>
    public bool AllowPlainHttp { get; set; }

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// link on work-item notifications (Agnes steers; this integration only
    /// links). Empty omits the link.</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>Render an HTML <c>formatted_body</c> alongside the plain-text
    /// <c>body</c> (Matrix <c>org.matrix.custom.html</c>). Default true; when
    /// false only the plain-text body is sent with raw answer URLs.</summary>
    public bool UseHtml { get; set; } = true;

    /// <summary>Post follow-up notifications for the same work item as thread
    /// replies (<c>m.thread</c> relation) so a long-running item reads as one
    /// conversation. Default true.</summary>
    public bool ThreadByWorkItem { get; set; } = true;

    /// <summary>Per-call timeout in seconds for Matrix Client-Server API
    /// calls. Must be &gt;= 1 — an invalid value is overridden with the
    /// shared <see cref="NotificationDelivery.DefaultPostTimeoutSeconds"/>
    /// default and a warning.</summary>
    public int PostTimeoutSeconds { get; set; } = NotificationDelivery.DefaultPostTimeoutSeconds;

    /// <summary>How many times a rate-limited send (<c>M_LIMIT_EXCEEDED</c>)
    /// is retried with the <em>same</em> transaction ID before giving up.
    /// Must be &gt;= 0; values above 5 are clamped to 5. Default 2.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Upper bound in seconds on the delay honoured from a
    /// <c>retry_after_ms</c> hint before retrying. Must be &gt;= 1 — an
    /// invalid value falls back to 30 seconds. Default 30.</summary>
    public int MaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 4000.</summary>
    public int MaxTextChars { get; set; } = 4000;

    /// <summary>Maximum structured fields rendered per message. Must be
    /// &gt;= 0. Default 10.</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>How long thread-root and event-identity entries live.
    /// Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked threads/events. Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}
