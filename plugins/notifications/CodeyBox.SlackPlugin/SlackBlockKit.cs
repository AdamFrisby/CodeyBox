using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.SlackPlugin;

/// <summary>
/// Pure Block Kit rendering for Slack notifications: severity, summary and
/// fields rendered as native blocks rather than a dumped text blob. All
/// decision logic lives here as input→output functions; the provider only
/// transports the result. Bounds come from <see cref="SlackPluginOptions"/>
/// so every cap is operator-configurable, never a literal at the sink.
/// </summary>
internal static class SlackBlockKit
{
    /// <summary>Action id carried by every CodeyBox answer button. The
    /// inbound parser only honours this id; anything else is not ours.</summary>
    public const string AnswerActionId = "codeybox_answer";

    /// <summary>Slack button <c>value</c> limit in characters.</summary>
    public const int MaxButtonValueChars = 2000;

    /// <summary>Slack header-block text limit in characters.</summary>
    public const int MaxHeaderChars = 150;

    /// <summary>Slack button-label limit in characters.</summary>
    public const int MaxLabelChars = 75;

    private static readonly JsonSerializerOptions CodecJson = new()
    {
        PropertyNamingPolicy = null,
    };

    /// <summary>Attachment colour token for a severity.</summary>
    public static string ColorFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "danger",
        NotificationSeverity.Warning => "warning",
        _ => "good",
    };

    /// <summary>Emoji prefix for a severity (Slack colon syntax).</summary>
    public static string EmojiFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => ":rotating_light:",
        NotificationSeverity.Warning => ":warning:",
        _ => ":information_source:",
    };

    /// <summary>Escape Slack mrkdwn control characters in untrusted text.</summary>
    public static string EscapeMrkdwn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>Truncate to a character budget, marking the cut.</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (maxChars < 1)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        const string marker = "… (truncated)";
        if (maxChars <= marker.Length)
            return text[..maxChars];
        return text[..(maxChars - marker.Length)] + marker;
    }

    /// <summary>Encode one offered action as the button <c>value</c>.
    /// Returns null when the binding does not fit Slack's value budget —
    /// the caller must keep the question answerable another way (answer /
    /// Agnes links) rather than posting a button that cannot resolve.</summary>
    public static string? EncodeButtonValue(string workItemId, string questionId, string answer, string correlationToken)
    {
        var value = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["w"] = workItemId,
                ["q"] = questionId,
                ["a"] = answer,
                ["c"] = correlationToken,
            },
            CodecJson);
        return value.Length > MaxButtonValueChars ? null : value;
    }

    /// <summary>Decode a button <c>value</c> back to its binding. Mirrors the
    /// contract the host-side inbound parser honours; both sides are pinned
    /// by the same recorded-shape test.</summary>
    public static bool TryDecodeButtonValue(string? value, out string workItemId, out string questionId, out string answer, out string correlationToken)
    {
        workItemId = questionId = answer = correlationToken = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaxButtonValueChars)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("w", out var w) || w.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("q", out var q) || q.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("a", out var a) || a.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String)
                return false;
            workItemId = w.GetString() ?? string.Empty;
            questionId = q.GetString() ?? string.Empty;
            answer = a.GetString() ?? string.Empty;
            correlationToken = c.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(workItemId)
                && !string.IsNullOrEmpty(questionId)
                && !string.IsNullOrEmpty(answer);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Build the <c>chat.postMessage</c> body for a notification.
    /// Returns the payload plus the work item id when one could be bound
    /// (used for thread routing and decision updates).</summary>
    public static (Dictionary<string, object?> Payload, string? WorkItemId) BuildMessage(
        Notification notification,
        SlackPluginOptions options)
    {
        var workItemId = BindWorkItemId(notification);
        var color = ColorFor(notification.Severity);
        var emoji = EmojiFor(notification.Severity);
        var body = Truncate(notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);

        var fallback = $"{emoji} [{notification.Severity}] {notification.Title}";
        if (!string.IsNullOrWhiteSpace(notification.AnswerUrl))
            fallback += $"\nAnswer here: {notification.AnswerUrl}";

        var blocks = new List<object?>();

        blocks.Add(new Dictionary<string, object?>
        {
            ["type"] = "header",
            ["text"] = new Dictionary<string, object?>
            {
                ["type"] = "plain_text",
                ["text"] = Truncate($"{emoji} {notification.Title}", MaxHeaderChars),
                ["emoji"] = true,
            },
        });

        blocks.Add(new Dictionary<string, object?>
        {
            ["type"] = "section",
            ["text"] = new Dictionary<string, object?>
            {
                ["type"] = "mrkdwn",
                ["text"] = EscapeMrkdwn(body),
            },
        });

        var fields = RenderFields(notification, options.MaxFields);
        if (fields.Count > 0)
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "section",
                ["fields"] = fields,
            });
        }

        var actionsRendered = RenderActions(blocks, notification, workItemId, options);

        RenderLinkButtons(blocks, notification, workItemId, options, actionsRendered);

        blocks.Add(new Dictionary<string, object?>
        {
            ["type"] = "context",
            ["elements"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mrkdwn",
                    ["text"] = EscapeMrkdwn($"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC"),
                },
            },
        });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = fallback,
            ["unfurl_links"] = false,
            ["unfurl_media"] = false,
            ["attachments"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["color"] = color,
                    ["fallback"] = fallback,
                    ["blocks"] = blocks,
                },
            },
        };
        return (payload, workItemId);
    }

    /// <summary>Build the <c>chat.update</c> blocks showing what was decided
    /// and by whom, for the visible loop-close after an answer lands.
    /// <paramref name="decisionSummary"/> already reads as the finished
    /// line (e.g. <c>Decided: … — by …</c>); it is escaped, never trusted.</summary>
    public static List<object?> BuildDecidedBlocks(string title, string decisionSummary, string conditionId)
    {
        var safeSummary = Truncate(decisionSummary, 3000);
        return
        [
            new Dictionary<string, object?>
            {
                ["type"] = "header",
                ["text"] = new Dictionary<string, object?>
                {
                    ["type"] = "plain_text",
                    ["text"] = Truncate($":white_check_mark: {title}", MaxHeaderChars),
                    ["emoji"] = true,
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "section",
                ["text"] = new Dictionary<string, object?>
                {
                    ["type"] = "mrkdwn",
                    ["text"] = $"*Decided:* {EscapeMrkdwn(safeSummary)}",
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "context",
                ["elements"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mrkdwn",
                        ["text"] = EscapeMrkdwn($"CodeyBox · {conditionId}"),
                    },
                },
            },
        ];
    }

    /// <summary>Deep link from a notification to the Agnes front end for the
    /// owning work item. Agnes steers; this integration only links. Returns
    /// null when no base URL is configured or no work item is bound.</summary>
    public static string? AgnesWorkItemUrl(SlackPluginOptions options, string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(options.AgnesBaseUrl) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        if (!Uri.TryCreate(options.AgnesBaseUrl, UriKind.Absolute, out var baseUri))
            return null;
        return $"{baseUri.ToString().TrimEnd('/')}/workitems/{Uri.EscapeDataString(workItemId)}";
    }

    private static string? BindWorkItemId(Notification notification)
    {
        if (notification.Actions is { Count: > 0 })
        {
            var first = notification.Actions[0];
            if (!string.IsNullOrWhiteSpace(first.WorkItemId))
                return first.WorkItemId;
        }
        if (!string.IsNullOrWhiteSpace(notification.CorrelationToken)
            && NotificationCorrelation.TryParse(notification.CorrelationToken, out var workItemId, out _))
            return workItemId;
        return null;
    }

    private static List<object> RenderFields(Notification notification, int maxFields)
    {
        var fields = new List<object>();
        if (notification.Fields is null || maxFields <= 0)
            return fields;
        foreach (var (key, value) in notification.Fields)
        {
            if (fields.Count >= maxFields)
                break;
            var name = Truncate(key, 100);
            var val = Truncate(value, 500);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            fields.Add(new Dictionary<string, object?>
            {
                ["type"] = "mrkdwn",
                ["text"] = $"*{EscapeMrkdwn(name)}*\n{EscapeMrkdwn(val)}",
            });
        }
        return fields;
    }

    private static bool RenderActions(
        List<object?> blocks,
        Notification notification,
        string? workItemId,
        SlackPluginOptions options)
    {
        if (notification.Actions is not { Count: > 0 })
            return false;
        if (options.ActionsMode != SlackActionsMode.Buttons)
            return false;
        if (options.MaxActions <= 0 || string.IsNullOrWhiteSpace(workItemId))
            return false;

        var buttons = new List<object>();
        var rendered = 0;
        foreach (var action in notification.Actions)
        {
            if (rendered >= options.MaxActions)
                break;
            if (!string.Equals(action.WorkItemId, workItemId, StringComparison.Ordinal))
                continue;
            var correlation = NotificationCorrelation.TokenFor(action.WorkItemId, action.QuestionId);            var value = EncodeButtonValue(action.WorkItemId, action.QuestionId, action.Value, correlation);
            if (value is null)
                continue;
            buttons.Add(new Dictionary<string, object?>
            {
                ["type"] = "button",
                ["action_id"] = AnswerActionId,
                ["text"] = new Dictionary<string, object?>
                {
                    ["type"] = "plain_text",
                    ["text"] = Truncate(action.Label, MaxLabelChars),
                    ["emoji"] = true,
                },
                ["value"] = value,
                ["style"] = rendered == 0 ? "primary" : null,
            });
            rendered++;
        }
        if (buttons.Count == 0)
            return false;

        foreach (var chunk in buttons.Chunk(5))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "actions",
                ["block_id"] = $"codeybox_actions_{blocks.Count}",
                ["elements"] = chunk,
            });
        }
        return true;
    }

    private static void RenderLinkButtons(
        List<object?> blocks,
        Notification notification,
        string? workItemId,
        SlackPluginOptions options,
        bool actionsRendered)
    {
        var links = new List<object>();
        if (!string.IsNullOrWhiteSpace(notification.AnswerUrl)
            && Uri.TryCreate(notification.AnswerUrl, UriKind.Absolute, out _))
        {
            links.Add(new Dictionary<string, object?>
            {
                ["type"] = "button",
                ["action_id"] = "codeybox_answer_link",
                ["text"] = new Dictionary<string, object?>
                {
                    ["type"] = "plain_text",
                    ["text"] = actionsRendered ? "Answer here" : "Answer in CodeyBox",
                    ["emoji"] = true,
                },
                ["url"] = notification.AnswerUrl,
            });
        }
        var agnesUrl = AgnesWorkItemUrl(options, workItemId);
        if (agnesUrl is not null)
        {
            links.Add(new Dictionary<string, object?>
            {
                ["type"] = "button",
                ["action_id"] = "codeybox_agnes_link",
                ["text"] = new Dictionary<string, object?>
                {
                    ["type"] = "plain_text",
                    ["text"] = "Open in Agnes",
                    ["emoji"] = true,
                },
                ["url"] = agnesUrl,
            });
        }
        if (links.Count == 0)
            return;
        foreach (var chunk in links.Chunk(5))
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "actions",
                ["block_id"] = $"codeybox_links_{blocks.Count}",
                ["elements"] = chunk,
            });
        }
    }
}
