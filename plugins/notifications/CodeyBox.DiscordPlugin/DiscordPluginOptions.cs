using CodeyBox.Core;

namespace CodeyBox.DiscordPlugin;

/// <summary>
/// Operator configuration for the Discord notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.discord:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>name</em> of the environment variable holding the bot token is
/// configured; the token itself travels the credential chain (environment).
///
/// <example>
/// <code>
/// {
///   "CodeyBox": {
///     "Plugins": {
///       "codeybox.discord": {
///         "Enabled": true,
///         "BotTokenEnvVar": "CODEYBOX_DISCORD_BOT_TOKEN",
///         "DefaultChannelId": "123456789012345678",
///         "AgnesBaseUrl": "https://agnes.example.invalid"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </summary>
public sealed class DiscordPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Environment variable holding the Discord bot token.
    /// Never set the token directly in config.</summary>
    public string BotTokenEnvVar { get; set; } = "CODEYBOX_DISCORD_BOT_TOKEN";

    /// <summary>Channel ID used when a notification carries no explicit
    /// recipient. Empty means "no default" — notifications without a
    /// resolvable channel are skipped with a warning.</summary>
    public string DefaultChannelId { get; set; } = string.Empty;

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// deep link (Agnes steers; this integration only links).</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>How offered actions render: <c>Buttons</c> posts native
    /// buttons resolving through the verified inbound endpoint (requires
    /// the Interactions Endpoint URL to be reachable by Discord);
    /// <c>Links</c> renders answer/Agnes link buttons only, for
    /// outbound-only deployments with no inbound exposure.
    /// Default <c>Buttons</c>.</summary>
    public DiscordActionsMode ActionsMode { get; set; } = DiscordActionsMode.Buttons;

    /// <summary>Post follow-up notifications for the same work item into a
    /// thread rooted at the first message, so a long-running item reads as
    /// one conversation. Default true.</summary>
    public bool ThreadByWorkItem { get; set; } = true;

    /// <summary>Per-call timeout in seconds for Discord REST calls.
    /// Must be &gt;= 1 — an invalid value is overridden with the shared
    /// <see cref="NotificationDelivery.DefaultPostTimeoutSeconds"/> default
    /// and a warning.</summary>
    public int PostTimeoutSeconds { get; set; } = NotificationDelivery.DefaultPostTimeoutSeconds;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 2000 (Discord content limit).</summary>
    public int MaxTextChars { get; set; } = 2000;

    /// <summary>Maximum structured fields rendered per message. Must be
    /// &gt;= 0. Default 10 (Discord allows 25 per embed).</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>Maximum answer buttons rendered per message. Must be
    /// &gt;= 0. Default 5 (one Discord action row). Surplus actions stay
    /// answerable via the answer/Agnes links.</summary>
    public int MaxActions { get; set; } = 5;

    /// <summary>How long thread-root and message-identity entries live.
    /// Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked threads/messages. Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>How offered <see cref="CodeyBox.Core.NotificationAction"/> values render.</summary>
public enum DiscordActionsMode
{
    /// <summary>Native buttons resolving through the verified inbound
    /// endpoint. Needs Discord to reach the host's Interactions Endpoint URL.</summary>
    Buttons,
    /// <summary>Link buttons to the answer URL / Agnes only. For
    /// deployments with no inbound exposure.</summary>
    Links,
}
