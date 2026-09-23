namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Operator configuration for the Gotify notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.gotify:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>name</em> of the environment variable holding the application token is
/// configured; the token itself travels the credential chain (environment).
///
/// <para>Gotify's token model is split: an <em>application</em> token can only
/// send (<c>POST /message</c>) and a <em>client</em> token can only read. This
/// provider sends, so it holds an application token — no client token exists
/// here to be confused with it.</para>
///
/// <example>
/// <code>
/// {
///   "CodeyBox": {
///     "Plugins": {
///       "codeybox.gotify": {
///         "Enabled": true,
///         "ServerUrl": "https://gotify.example.invalid",
///         "AppTokenEnvVar": "CODEYBOX_GOTIFY_APP_TOKEN",
///         "AgnesBaseUrl": "https://agnes.example.invalid"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </summary>
public sealed class GotifyPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the Gotify server, e.g.
    /// <c>https://gotify.example.invalid</c> (a path prefix such as
    /// <c>https://host/gotify</c> is honoured). Required when
    /// <see cref="Enabled"/> — notifications are skipped with a warning
    /// until a valid absolute http/https URL is configured.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Environment variable holding the Gotify <em>application</em>
    /// token (created under Apps in the Gotify UI). Never set the token
    /// directly in config.</summary>
    public string AppTokenEnvVar { get; set; } = "CODEYBOX_GOTIFY_APP_TOKEN";

    /// <summary>Permit a plain-http <see cref="ServerUrl"/>. Default false:
    /// the application token travels in a request header, so delivery to an
    /// http endpoint is refused with a warning unless the operator explicitly
    /// accepts the cleartext-token exposure of an internal deployment.</summary>
    public bool AllowPlainHttp { get; set; }

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// link on work-item notifications (Agnes steers; this integration only
    /// links). Empty omits the link.</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>Render the message body as markdown
    /// (<c>client::display.contentType = text/markdown</c>) so fields and
    /// answer links present natively in clients that support it. Default
    /// true; when false the body is sent as <c>text/plain</c> with raw
    /// answer URLs.</summary>
    public bool Markdown { get; set; } = true;

    /// <summary>Message priority for <see cref="CodeyBox.Core.NotificationSeverity.Information"/>
    /// notifications (Gotify 0–10). Default 2.</summary>
    public int InformationPriority { get; set; } = 2;

    /// <summary>Message priority for <see cref="CodeyBox.Core.NotificationSeverity.Warning"/>
    /// notifications (Gotify 0–10). Default 5.</summary>
    public int WarningPriority { get; set; } = 5;

    /// <summary>Message priority for <see cref="CodeyBox.Core.NotificationSeverity.Critical"/>
    /// notifications (Gotify 0–10). Default 8.</summary>
    public int CriticalPriority { get; set; } = 8;

    /// <summary>Per-call timeout in seconds for Gotify REST calls.
    /// Must be &gt;= 1. Default 15.</summary>
    public int PostTimeoutSeconds { get; set; } = 15;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 4000.</summary>
    public int MaxTextChars { get; set; } = 4000;

    /// <summary>Maximum structured fields rendered per message. Must be
    /// &gt;= 0. Default 10.</summary>
    public int MaxFields { get; set; } = 10;
}
