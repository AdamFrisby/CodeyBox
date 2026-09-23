using CodeyBox.Core;

namespace CodeyBox.SlackPlugin;

/// <summary>
/// Operator configuration for the Slack notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.slack:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>name</em> of the environment variable holding the bot token is
/// configured; the token itself travels the credential chain (environment).
///
/// <example>
/// <code>
/// {
///   "CodeyBox": {
///     "Plugins": {
///       "codeybox.slack": {
///         "Enabled": true,
///         "BotTokenEnvVar": "CODEYBOX_SLACK_BOT_TOKEN",
///         "DefaultChannel": "C012345",
///         "AgnesBaseUrl": "https://agnes.example.invalid"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </summary>
public sealed class SlackPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Environment variable holding the Slack bot token
    /// (<c>xoxb-…</c>). Never set the token directly in config.</summary>
    public string BotTokenEnvVar { get; set; } = "CODEYBOX_SLACK_BOT_TOKEN";

    /// <summary>Channel ID used when a notification carries no explicit
    /// recipient. Empty means "no default" — notifications without a
    /// resolvable channel are skipped with a warning.</summary>
    public string DefaultChannel { get; set; } = string.Empty;

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// deep link (Agnes steers; this integration only links).</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>How offered actions render: <c>Buttons</c> posts native
    /// Block Kit buttons (requires inbound exposure of
    /// <c>/webhooks/interactions/slack</c> to Slack); <c>Links</c> renders
    /// answer/Agnes links only, for outbound-only deployments where no
    /// Request URL is reachable. Default <c>Buttons</c>.</summary>
    public SlackActionsMode ActionsMode { get; set; } = SlackActionsMode.Buttons;

    /// <summary>Post follow-up notifications for the same work item as
    /// threaded replies so a long-running item reads as one conversation.
    /// Default true.</summary>
    public bool ThreadByWorkItem { get; set; } = true;

    /// <summary>Per-call timeout in seconds for Slack Web API calls.
    /// Must be &gt;= 1 — an invalid value is overridden with the shared
    /// <see cref="NotificationDelivery.DefaultPostTimeoutSeconds"/> default
    /// and a warning.</summary>
    public int PostTimeoutSeconds { get; set; } = NotificationDelivery.DefaultPostTimeoutSeconds;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 3000 (Slack section text limit).</summary>
    public int MaxTextChars { get; set; } = 3000;

    /// <summary>Maximum structured fields rendered per message. Must be
    /// &gt;= 0. Default 10 (Slack section field limit).</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>Maximum action buttons rendered per message. Must be
    /// &gt;= 0. Default 10. Surplus actions stay answerable via the
    /// answer/Agnes links.</summary>
    public int MaxActions { get; set; } = 10;

    /// <summary>How long thread-root and message-identity entries live.
    /// Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked threads/messages. Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>How offered <see cref="CodeyBox.Core.NotificationAction"/> values render.</summary>
public enum SlackActionsMode
{
    /// <summary>Native Block Kit buttons resolving through the verified
    /// inbound endpoint. Needs Slack to reach the host.</summary>
    Buttons,
    /// <summary>Link buttons to the answer URL / Agnes only. For
    /// deployments with no inbound exposure.</summary>
    Links,
}
