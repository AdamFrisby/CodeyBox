namespace CodeyBox.TeamsPlugin;

/// <summary>
/// Operator configuration for the Teams notification plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.teams:</c>. Everything operational is a knob
/// here — no literals in source. Secrets are never set in config: only the
/// <em>names</em> of the environment variables holding the bot credentials
/// are configured; the credentials themselves travel the credential chain
/// (environment).
/// </summary>
public sealed class TeamsPluginOptions
{
    /// <summary>Master switch. Default false: the plugin is inert until an
    /// operator enables it (and allowlists it per the plugin gates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Environment variable holding the bot's Microsoft App ID
    /// (the Bot Framework token audience). Never set the ID in config.</summary>
    public string AppIdEnvVar { get; set; } = "CODEYBOX_TEAMS_APP_ID";

    /// <summary>Environment variable holding the bot's Microsoft App
    /// password (client secret used for connector token acquisition).
    /// Never set the secret in config.</summary>
    public string AppPasswordEnvVar { get; set; } = "CODEYBOX_TEAMS_APP_PASSWORD";

    /// <summary>Bot Framework service URL the bot was reached on, e.g.
    /// <c>https://smba.trafficmanager.net/teams/</c>. Must be an absolute
    /// https URL; activities post under it. The connector client re-checks
    /// this per call so a future caller is safe too.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>Conversation ID posted to when a notification carries no
    /// explicit recipient. Empty means "no default" — notifications without
    /// a resolvable conversation are skipped with a warning.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Public base URL of the Agnes front end, e.g.
    /// <c>https://agnes.example.invalid</c>. Supplies the "Open in Agnes"
    /// deep link (Agnes steers; this integration only links).</summary>
    public string AgnesBaseUrl { get; set; } = string.Empty;

    /// <summary>How offered actions render: <c>Buttons</c> posts native
    /// Adaptive Card <c>Action.Submit</c> controls (plus a custom-answer
    /// follow-up prompt) resolving through the verified inbound endpoint —
    /// requires the bot's messaging endpoint to reach
    /// <c>/webhooks/interactions/teams</c>; <c>Links</c> renders
    /// answer/Agnes links only, for outbound-only deployments where no
    /// messaging endpoint is reachable. Default <c>Buttons</c>.</summary>
    public TeamsActionsMode ActionsMode { get; set; } = TeamsActionsMode.Buttons;

    /// <summary>Per-call timeout in seconds for Bot Framework calls
    /// (token acquisition, activity post/update). Must be &gt;= 1 — an
    /// invalid value is overridden with the shared
    /// <see cref="Core.NotificationDelivery.DefaultPostTimeoutSeconds"/>
    /// default and a warning.</summary>
    public int PostTimeoutSeconds { get; set; } = Core.NotificationDelivery.DefaultPostTimeoutSeconds;

    /// <summary>Maximum characters kept from the notification body before
    /// truncation. Must be &gt;= 1. Default 4000 (keeps cards under the
    /// Teams card-size budget).</summary>
    public int MaxTextChars { get; set; } = 4000;

    /// <summary>Maximum structured fields rendered per card. Must be
    /// &gt;= 0. Default 10.</summary>
    public int MaxFields { get; set; } = 10;

    /// <summary>Maximum native submit buttons rendered per card. Must be
    /// &gt;= 0. Default 6. Surplus actions stay answerable via the
    /// answer/Agnes links.</summary>
    public int MaxActions { get; set; } = 6;

    /// <summary>How long posted-message identities live. Default 24 hours.</summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on tracked posted messages. Default 10 000.</summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>How offered <see cref="Core.NotificationAction"/> values render.</summary>
public enum TeamsActionsMode
{
    /// <summary>Native <c>Action.Submit</c> controls resolving through the
    /// verified inbound endpoint. Needs the bot's messaging endpoint to
    /// reach the host.</summary>
    Buttons,
    /// <summary>Link buttons to the answer URL / Agnes only. For
    /// deployments with no inbound exposure.</summary>
    Links,
}
