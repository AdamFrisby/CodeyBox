using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Notifications;

/// <summary>
/// Delivers notifications to Slack and/or Discord via incoming webhooks.
/// Webhook URLs are loaded from environment variables (never hardcoded),
/// following the existing <c>CODEYBOX_*</c> secret-env-var pattern.
/// Unconfigured providers are safe no-ops.
/// </summary>
public sealed class ChatNotificationProvider : INotificationProvider
{
    private readonly Func<ChatProviderOptions> _optsAccessor;
    private readonly HttpClient _http;
    private readonly ILogger<ChatNotificationProvider> _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name => "chat";

    public ChatNotificationProvider(
        ChatProviderOptions opts,
        HttpClient http,
        ILogger<ChatNotificationProvider> log)
        : this(() => opts, http, log)
    {
    }

    public ChatNotificationProvider(
        Func<ChatProviderOptions> optsAccessor,
        HttpClient http,
        ILogger<ChatNotificationProvider> log)
    {
        _optsAccessor = optsAccessor;
        _http = http;
        _log = log;
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = _optsAccessor();
        if (!opts.Enabled || opts.Webhooks.Count == 0)
            return;

        foreach (var webhook in opts.Webhooks)
        {
            var url = ResolveUrl(webhook);
            if (url is null)
                continue;

            try
            {
                var payload = webhook.Platform switch
                {
                    ChatPlatform.Slack => BuildSlackPayload(notification, webhook),
                    ChatPlatform.Discord => BuildDiscordPayload(notification, webhook),
                    _ => throw new InvalidOperationException($"Unknown chat platform: {webhook.Platform}"),
                };
                var json = JsonSerializer.Serialize(payload, JsonOptions);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning(
                        "ChatNotificationProvider: {Platform} webhook returned {Status} for condition {Condition}",
                        webhook.Platform, (int)response.StatusCode, notification.ConditionId);
                }
                else
                {
                    _log.LogInformation(
                        "ChatNotificationProvider: posted {Platform} notification {Condition} ({Severity})",
                        webhook.Platform, notification.ConditionId, notification.Severity);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "ChatNotificationProvider: {Platform} delivery failed for condition {Condition}",
                    webhook.Platform, notification.ConditionId);
            }
        }
    }

    private string? ResolveUrl(ChatWebhookOptions webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.UrlEnvVar))
        {
            _log.LogWarning(
                "ChatNotificationProvider: {Platform} webhook has no UrlEnvVar configured; skipping",
                webhook.Platform);
            return null;
        }
        var url = Environment.GetEnvironmentVariable(webhook.UrlEnvVar);
        if (string.IsNullOrWhiteSpace(url))
        {
            _log.LogWarning(
                "ChatNotificationProvider: {Platform} UrlEnvVar '{EnvVar}' is configured but not set in environment",
                webhook.Platform, webhook.UrlEnvVar);
            return null;
        }
        return url;
    }

    // ── Payload builders ─────────────────────────────────────────────────────

    internal static object BuildSlackPayload(Notification n, ChatWebhookOptions webhook)
    {
        var color = SlackColor(n.Severity);
        var emoji = SeverityEmoji(n.Severity);
        var fallbackText = $"{emoji} [{n.Severity}] {n.Title}";

        var fields = new List<object>();
        if (n.Fields is not null)
        {
            foreach (var (key, value) in n.Fields)
            {
                fields.Add(new
                {
                    title = key,
                    value,
                    @short = true,
                });
            }
        }

        var attachment = new Dictionary<string, object?>
        {
            ["color"] = color,
            ["title"] = n.Title,
            ["text"] = n.Body ?? n.Summary,
            ["ts"] = n.Timestamp.ToUnixTimeSeconds(),
            ["footer"] = $"CodeyBox · {n.ConditionId}",
        };
        if (fields.Count > 0)
            attachment["fields"] = fields;

        var payload = new Dictionary<string, object?>
        {
            ["text"] = fallbackText,
            ["attachments"] = new[] { attachment },
        };
        if (!string.IsNullOrWhiteSpace(webhook.Username))
            payload["username"] = webhook.Username;
        return payload;
    }

    internal static object BuildDiscordPayload(Notification n, ChatWebhookOptions webhook)
    {
        var color = DiscordColor(n.Severity);
        var emoji = SeverityEmoji(n.Severity);
        var content = $"{emoji} **[{n.Severity}]** {n.Title}";

        var embedFields = new List<object>();
        if (n.Fields is not null)
        {
            foreach (var (key, value) in n.Fields)
            {
                embedFields.Add(new
                {
                    name = key,
                    value,
                    inline = true,
                });
            }
        }

        var embed = new Dictionary<string, object?>
        {
            ["title"] = n.Title,
            ["description"] = n.Body ?? n.Summary,
            ["color"] = color,
            ["timestamp"] = n.Timestamp.UtcDateTime.ToString("o"),
            ["footer"] = new { text = $"CodeyBox · {n.ConditionId}" },
        };
        if (embedFields.Count > 0)
            embed["fields"] = embedFields;
        if (!string.IsNullOrWhiteSpace(webhook.Username))
            embed["author"] = new { name = webhook.Username };

        return new
        {
            content,
            embeds = new[] { embed },
        };
    }

    // ── Severity mappings ────────────────────────────────────────────────────

    /// <summary>Slack attachment colour token: "good" / "warning" / "danger".</summary>
    internal static string SlackColor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "danger",
        NotificationSeverity.Warning => "warning",
        _ => "good",
    };

    /// <summary>Discord embed colour as a 24-bit RGB integer.</summary>
    internal static int DiscordColor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 0xE01E5A, // red
        NotificationSeverity.Warning => 0xECB22E,  // amber
        _ => 0x2EB67D,                              // green
    };

    internal static string SeverityEmoji(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => ":rotating_light:",
        NotificationSeverity.Warning => ":warning:",
        _ => ":information_source:",
    };
}
