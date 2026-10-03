using CodeyBox.Core;

namespace CodeyBox.DiscordPlugin;

/// <summary>
/// Pure Discord rendering for notifications: severity, summary and fields
/// rendered as a native embed with action-row buttons rather than a dumped
/// text blob. All decision logic lives here as input→output functions; the
/// provider only transports the result. Operator-configurable bounds come
/// from <see cref="DiscordPluginOptions"/>; the fixed presentation caps
/// (title, label lengths) are named constants — the field caps shared with
/// every provider via <see cref="NotificationRendering"/>.
///
/// <para>Answer buttons carry <see cref="DiscordButtonCodec"/> bindings
/// (Discord caps <c>custom_id</c> at 100 characters). A binding that does
/// not fit is skipped so the question stays answerable through the
/// answer/Agnes link buttons instead of an unresolvable button.</para>
/// </summary>
internal static class DiscordMessageBuilder
{
    /// <summary>Discord button-label limit in characters.</summary>
    public const int MaxLabelChars = 80;

    /// <summary>Discord embed-title limit in characters.</summary>
    public const int MaxTitleChars = 256;

    /// <summary>Discord thread-name limit in characters.</summary>
    public const int MaxThreadNameChars = 100;

    /// <summary>Maximum buttons per Discord action row.</summary>
    public const int MaxButtonsPerRow = 5;

    /// <summary>Maximum action rows per Discord message.</summary>
    public const int MaxActionRows = 5;

    /// <summary>Embed colour for a severity.</summary>
    public static int ColorFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 0xE01E5A,
        NotificationSeverity.Warning => 0xECB22E,
        _ => 0x2EB67D,
    };

    /// <summary>Unicode emoji prefix for a severity. Plain unicode — Discord
    /// does not render Slack-style colon tokens from bot embeds.</summary>
    public static string EmojiFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "🚨",
        NotificationSeverity.Warning => "⚠️",
        _ => "ℹ️",
    };

    /// <summary>Build the create-message body for a notification. Returns
    /// the payload plus the work item id when one could be bound (used for
    /// thread routing and decision updates).</summary>
    public static (Dictionary<string, object?> Payload, string? WorkItemId) BuildMessage(
        Notification notification,
        DiscordPluginOptions options)
    {
        var workItemId = NotificationBinding.WorkItemIdFor(notification);
        var emoji = EmojiFor(notification.Severity);
        var body = NotificationRendering.Truncate(notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);

        var answerLink = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var content = NotificationRendering.Truncate($"{emoji} [{notification.Severity}] {notification.Title}", options.MaxTextChars);
        if (answerLink is not null)
            content = NotificationRendering.Truncate($"{content}\nAnswer here: {answerLink}", options.MaxTextChars);

        var embed = new Dictionary<string, object?>
        {
            ["title"] = NotificationRendering.Truncate($"{emoji} {notification.Title}", MaxTitleChars),
            ["description"] = body,
            ["color"] = ColorFor(notification.Severity),
            ["timestamp"] = notification.Timestamp.UtcDateTime.ToString("o"),
            ["footer"] = new Dictionary<string, object?> { ["text"] = $"CodeyBox · {notification.ConditionId}" },
        };

        var fields = RenderFields(notification, options.MaxFields);
        if (fields.Count > 0)
            embed["fields"] = fields;

        var components = new List<object?>();
        var actionsRendered = RenderAnswerButtons(components, notification, workItemId, options);
        RenderLinkButtons(components, notification, workItemId, options, actionsRendered);
        // Discord rejects messages with more than five action rows: keep
        // the answer buttons (first rows) and drop surplus link rows — the
        // links they carry also appear inline in the message content.
        while (components.Count > MaxActionRows)
            components.RemoveAt(components.Count - 1);

        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["embeds"] = new object?[] { embed },
            // Never ping: notification text is untrusted and must not be
            // able to mention everyone, roles, or users.
            ["allowed_mentions"] = new Dictionary<string, object?> { ["parse"] = Array.Empty<string>() },
        };
        if (components.Count > 0)
            payload["components"] = components;
        return (payload, workItemId);
    }

    /// <summary>Build the edit-message body showing what was decided and by
    /// whom, for the visible loop-close after an answer lands. Buttons are
    /// removed so a decided question cannot be pressed again.
    /// <paramref name="decisionSummary"/> already reads as the finished
    /// line (e.g. <c>Decided: … — by …</c>); it is rendered, never trusted.</summary>
    public static Dictionary<string, object?> BuildDecidedPayload(string title, string decisionSummary, string conditionId)
    {
        var safeSummary = NotificationRendering.Truncate(decisionSummary, 2000);
        return new Dictionary<string, object?>
        {
            ["content"] = NotificationRendering.Truncate($"✅ {safeSummary}", 2000),
            ["embeds"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["title"] = NotificationRendering.Truncate($"✅ {title}", MaxTitleChars),
                    ["description"] = safeSummary,
                    ["color"] = 0x2EB67D,
                    ["footer"] = new Dictionary<string, object?> { ["text"] = $"CodeyBox · {conditionId}" },
                },
            },
            ["components"] = Array.Empty<object?>(),
            ["allowed_mentions"] = new Dictionary<string, object?> { ["parse"] = Array.Empty<string>() },
        };
    }

    /// <summary>Suggest a thread name for a work item's first notification.</summary>
    public static string ThreadNameFor(string title) =>
        NotificationRendering.Truncate(title, MaxThreadNameChars);

    private static List<object> RenderFields(Notification notification, int maxFields)
    {
        var fields = new List<object>();
        if (notification.Fields is null || maxFields <= 0)
            return fields;
        foreach (var (key, value) in notification.Fields)
        {
            if (fields.Count >= maxFields)
                break;
            var name = NotificationRendering.Truncate(key, NotificationRendering.MaxFieldNameChars);
            var val = NotificationRendering.Truncate(value, NotificationRendering.MaxFieldValueChars);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            fields.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["value"] = val,
                ["inline"] = true,
            });
        }
        return fields;
    }

    private static bool RenderAnswerButtons(
        List<object?> components,
        Notification notification,
        string? workItemId,
        DiscordPluginOptions options)
    {
        if (notification.Actions is not { Count: > 0 })
            return false;
        if (options.ActionsMode != DiscordActionsMode.Buttons)
            return false;
        if (options.MaxActions <= 0 || string.IsNullOrWhiteSpace(workItemId))
            return false;

        var buttons = new List<object?>();
        foreach (var action in notification.Actions)
        {
            if (buttons.Count >= options.MaxActions)
                break;
            if (!string.Equals(action.WorkItemId, workItemId, StringComparison.Ordinal))
                continue;
            var customId = DiscordButtonCodec.Encode(action.WorkItemId, action.QuestionId, action.Value);
            if (customId is null)
                continue;
            buttons.Add(new Dictionary<string, object?>
            {
                ["type"] = 2,
                ["style"] = buttons.Count == 0 ? 1 : 2,
                ["label"] = NotificationRendering.Truncate(action.Label, MaxLabelChars),
                ["custom_id"] = customId,
            });
        }
        if (buttons.Count == 0)
            return false;

        foreach (var chunk in buttons.Chunk(MaxButtonsPerRow))
        {
            components.Add(new Dictionary<string, object?>
            {
                ["type"] = 1,
                ["components"] = chunk,
            });
        }
        return true;
    }

    private static void RenderLinkButtons(
        List<object?> components,
        Notification notification,
        string? workItemId,
        DiscordPluginOptions options,
        bool actionsRendered)
    {
        var links = new List<object?>();
        var answerUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        if (answerUrl is not null)
        {
            links.Add(new Dictionary<string, object?>
            {
                ["type"] = 2,
                ["style"] = 5,
                ["label"] = NotificationRendering.Truncate(actionsRendered ? "Answer here" : "Answer in CodeyBox", MaxLabelChars),
                ["url"] = answerUrl,
            });
        }
        var agnesUrl = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (agnesUrl is not null)
        {
            links.Add(new Dictionary<string, object?>
            {
                ["type"] = 2,
                ["style"] = 5,
                ["label"] = "Open in Agnes",
                ["url"] = agnesUrl,
            });
        }
        foreach (var chunk in links.Chunk(MaxButtonsPerRow))
        {
            components.Add(new Dictionary<string, object?>
            {
                ["type"] = 1,
                ["components"] = chunk,
            });
        }
    }
}
