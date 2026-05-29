namespace CodeyBox.Notifications;

/// <summary>
/// Top-level notifications config bound from <c>CodeyBox:Notifications</c>.
/// Empty rules list = notifications disabled.
/// </summary>
public sealed class NotificationsOptions
{
    /// <summary>Master switch. Default true so the host starts without error;
    /// operators must set rules to activate any notifications.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SMTP configuration for the email provider. Set <c>Enabled</c> to
    /// <c>true</c> to activate the email notification path.
    /// </summary>
    public EmailProviderOptions Email { get; set; } = new();

    /// <summary>
    /// Notification rules. Each rule maps a condition → providers + recipients
    /// + severity override + debounce cooldown.
    /// </summary>
    public List<NotificationRuleOptions> Rules { get; set; } = [];
}

/// <summary>
/// SMTP provider configuration. Follows the existing <c>CODEYBOX_*</c>
/// secret-env-var pattern: <c>PasswordEnvVar</c> names an environment variable
/// holding the SMTP password (never hardcoded).
/// </summary>
public sealed class EmailProviderOptions
{
    /// <summary>Enable email notifications. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>SMTP server hostname.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>SMTP port. Default 587 (STARTTLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>SMTP sender address in "from" header.</summary>
    public string From { get; set; } = "codeybox@localhost";

    /// <summary>SMTP username for AUTH LOGIN.</summary>
    public string? User { get; set; }

    /// <summary>
    /// Environment variable holding the SMTP password.
    /// Never set the password directly in config.
    /// </summary>
    public string? PasswordEnvVar { get; set; }

    /// <summary>When true, skips certificate validation (dev only).</summary>
    public bool IgnoreCertificateErrors { get; set; }
}

/// <summary>
/// One notification rule mapping a condition to providers + tuning.
/// Edits take effect on the next sweep via IOptionsMonitor hot-reload.
/// </summary>
public sealed class NotificationRuleOptions
{
    /// <summary>
    /// Condition identifier, e.g. "queue_empty", "all_quotas_exhausted".
    /// Must match an <see cref="CodeyBox.Core.ICondition.Id"/>.
    /// </summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>Provider names to route this notification to (e.g. "email").</summary>
    public List<string> Providers { get; set; } = [];

    /// <summary>Recipient email addresses or channel identifiers.</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>
    /// Force a severity level on the notification (overrides the
    /// condition's default builder severity). Null = use builder default.
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// Minimum interval between consecutive firings of this condition
    /// while it remains true. Default 0 (no cooldown — edge-triggered only).
    /// Format: hh:mm:ss (TimeSpan).
    /// </summary>
    public string Cooldown { get; set; } = "00:00:00";

    /// <summary>
    /// Stall threshold for the <c>orchestrator_stall</c> condition
    /// (minutes). Ignored for other conditions.
    /// </summary>
    public int StallThresholdMinutes { get; set; } = 15;
}
