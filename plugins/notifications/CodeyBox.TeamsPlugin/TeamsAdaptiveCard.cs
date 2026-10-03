using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.TeamsPlugin;

/// <summary>
/// Pure Adaptive Card rendering for Teams notifications: severity, summary
/// and fields rendered as a native card rather than a dumped text blob. All
/// decision logic lives here as input→output functions; the provider only
/// transports the result. Operator-configurable bounds come from
/// <see cref="TeamsPluginOptions"/>; the fixed presentation caps (title,
/// label lengths) are named constants — the field caps shared with every
/// provider via <see cref="NotificationRendering"/>.
/// </summary>
internal static class TeamsAdaptiveCard
{
    /// <summary>Attachment content type marking an Adaptive Card body.</summary>
    public const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";

    /// <summary>Adaptive Card schema version this plugin renders.</summary>
    public const string CardVersion = "1.5";

    /// <summary>Submit-data budget in characters. A binding that does not
    /// fit is never posted as a button — the caller keeps the question
    /// answerable another way (answer / Agnes links) rather than posting a
    /// control that cannot resolve.</summary>
    public const int MaxSubmitDataChars = 1000;

    /// <summary>Card title limit in characters.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>Submit-button label limit in characters.</summary>
    public const int MaxLabelChars = 100;

    /// <summary>Input id for the custom-answer follow-up prompt. The submit
    /// inside the <c>Action.ShowCard</c> carries the question binding without
    /// an answer; the typed text arrives merged into the activity value under
    /// this key and the host-side parser resolves it.</summary>
    public const string CustomAnswerInputId = "codeybox_custom";

    private static readonly JsonSerializerOptions CodecJson = new()
    {
        PropertyNamingPolicy = null,
    };

    /// <summary>Container style token for a severity.</summary>
    public static string StyleFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "attention",
        NotificationSeverity.Warning => "warning",
        _ => "accent",
    };

    /// <summary>Severity badge prefix for a title.</summary>
    public static string BadgeFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "🚨",
        NotificationSeverity.Warning => "⚠️",
        _ => "ℹ️",
    };

    /// <summary>Encode one offered action as <c>Action.Submit</c> data.
    /// Returns null when the binding does not fit the submit-data budget.</summary>
    public static string? EncodeSubmitData(string workItemId, string questionId, string? answer, string correlationToken)
    {
        var data = new Dictionary<string, string>
        {
            ["w"] = workItemId,
            ["q"] = questionId,
            ["c"] = correlationToken,
        };
        if (answer is not null)
            data["a"] = answer;
        var json = JsonSerializer.Serialize(data, CodecJson);
        return json.Length > MaxSubmitDataChars ? null : json;
    }

    /// <summary>Decode <c>Action.Submit</c> data back to its binding. Mirrors
    /// the contract the host-side inbound parser honours; both sides are
    /// pinned by the same recorded-shape test. The answer may arrive either
    /// as the bound <c>a</c> value (button press) or as the typed
    /// <c>codeybox_custom</c> input (follow-up prompt).</summary>
    public static bool TryDecodeSubmitData(string? data, out string workItemId, out string questionId, out string answer, out string correlationToken)
    {
        workItemId = questionId = answer = correlationToken = string.Empty;
        if (string.IsNullOrEmpty(data) || data.Length > MaxSubmitDataChars + 4000)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!TryGetNonEmpty(root, "w", out workItemId)
                || !TryGetNonEmpty(root, "q", out questionId))
                return false;
            if (!TryGetNonEmpty(root, "c", out correlationToken))
                correlationToken = string.Empty;
            if (TryGetNonEmpty(root, "a", out var bound))
                answer = bound;
            else if (TryGetNonEmpty(root, CustomAnswerInputId, out var typed))
                answer = typed;
            else
                return false;
            return !string.IsNullOrEmpty(workItemId)
                && !string.IsNullOrEmpty(questionId)
                && !string.IsNullOrEmpty(answer);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetNonEmpty(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>Build the Adaptive Card body for a notification. Returns the
    /// card plus the work item id when one could be bound (used for decision
    /// updates).</summary>
    public static (Dictionary<string, object?> Card, string? WorkItemId) BuildCard(
        Notification notification,
        TeamsPluginOptions options)
    {
        var workItemId = NotificationBinding.WorkItemIdFor(notification);
        var body = NotificationRendering.Truncate(notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);
        var answerLink = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var agnesLink = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);

        var cardBody = new List<object?>();

        cardBody.Add(new Dictionary<string, object?>
        {
            ["type"] = "Container",
            ["style"] = StyleFor(notification.Severity),
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "TextBlock",
                    ["text"] = NotificationRendering.Truncate($"{BadgeFor(notification.Severity)} [{notification.Severity}] {notification.Title}", MaxTitleChars),
                    ["wrap"] = true,
                    ["size"] = "Large",
                    ["weight"] = "Bolder",
                },
            },
        });

        cardBody.Add(new Dictionary<string, object?>
        {
            ["type"] = "TextBlock",
            ["text"] = body,
            ["wrap"] = true,
        });

        var facts = RenderFacts(notification, options.MaxFields);
        if (facts.Count > 0)
        {
            cardBody.Add(new Dictionary<string, object?>
            {
                ["type"] = "FactSet",
                ["facts"] = facts,
            });
        }

        cardBody.Add(new Dictionary<string, object?>
        {
            ["type"] = "TextBlock",
            ["text"] = $"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC",
            ["wrap"] = true,
            ["isSubtle"] = true,
            ["size"] = "Small",
        });

        var actions = RenderActions(notification, workItemId, options, answerLink, agnesLink);

        var card = new Dictionary<string, object?>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = CardVersion,
            ["body"] = cardBody,
        };
        if (actions.Count > 0)
            card["actions"] = actions;
        return (card, workItemId);
    }

    /// <summary>Build the replacement card showing what was decided and by
    /// whom, for the visible loop-close after an answer lands.
    /// <paramref name="decisionSummary"/> already reads as the finished line
    /// (e.g. <c>Decided: … — by …</c>); it is truncated, never trusted.</summary>
    public static Dictionary<string, object?> BuildDecidedCard(string title, string decisionSummary, string conditionId)
    {
        var safeSummary = NotificationRendering.Truncate(decisionSummary, 4000);
        return new Dictionary<string, object?>
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = CardVersion,
            ["body"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "TextBlock",
                    ["text"] = NotificationRendering.Truncate($"✅ {title}", MaxTitleChars),
                    ["wrap"] = true,
                    ["size"] = "Large",
                    ["weight"] = "Bolder",
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "TextBlock",
                    ["text"] = safeSummary,
                    ["wrap"] = true,
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "TextBlock",
                    ["text"] = $"CodeyBox · {conditionId}",
                    ["wrap"] = true,
                    ["isSubtle"] = true,
                    ["size"] = "Small",
                },
            },
        };
    }

    private static List<object> RenderFacts(Notification notification, int maxFields)
    {
        var facts = new List<object>();
        if (notification.Fields is null || maxFields <= 0)
            return facts;
        foreach (var (key, value) in notification.Fields)
        {
            if (facts.Count >= maxFields)
                break;
            var name = NotificationRendering.Truncate(key, NotificationRendering.MaxFieldNameChars);
            var val = NotificationRendering.Truncate(value, NotificationRendering.MaxFieldValueChars);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            facts.Add(new Dictionary<string, object?>
            {
                ["title"] = name,
                ["value"] = val,
            });
        }
        return facts;
    }

    private static List<object?> RenderActions(
        Notification notification,
        string? workItemId,
        TeamsPluginOptions options,
        string? answerLink,
        string? agnesLink)
    {
        var actions = new List<object?>();
        var nativeRendered = 0;

        if (options.ActionsMode == TeamsActionsMode.Buttons && notification.Actions is { Count: > 0 })
        {
            var maxActions = Math.Max(0, options.MaxActions);
            foreach (var action in notification.Actions)
            {
                if (nativeRendered >= maxActions)
                    break;
                if (string.IsNullOrWhiteSpace(action.Label)
                    || string.IsNullOrWhiteSpace(action.WorkItemId)
                    || string.IsNullOrWhiteSpace(action.QuestionId)
                    || string.IsNullOrWhiteSpace(action.Value))
                    continue;
                var token = string.IsNullOrWhiteSpace(notification.CorrelationToken)
                    ? NotificationCorrelation.TokenFor(action.WorkItemId, action.QuestionId)
                    : notification.CorrelationToken;
                var data = EncodeSubmitData(action.WorkItemId, action.QuestionId, action.Value, token);
                if (data is null)
                    continue;
                var label = NotificationRendering.Truncate(action.Label, MaxLabelChars);
                actions.Add(new Dictionary<string, object?>
                {
                    ["type"] = "Action.Submit",
                    ["title"] = label,
                    ["data"] = JsonSerializer.Deserialize<Dictionary<string, string>>(data, CodecJson),
                });
                nativeRendered++;
            }

            if (nativeRendered > 0 && TrySingleQuestionBinding(notification, out var bindWorkItem, out var bindQuestion, out var bindToken))
            {
                var followUpData = EncodeSubmitData(bindWorkItem, bindQuestion, answer: null, bindToken);
                if (followUpData is not null)
                {
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "Action.ShowCard",
                        ["title"] = "Custom answer…",
                        ["card"] = new Dictionary<string, object?>
                        {
                            ["type"] = "AdaptiveCard",
                            ["body"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "Input.Text",
                                    ["id"] = CustomAnswerInputId,
                                    ["placeholder"] = "Type your answer…",
                                    ["isMultiline"] = true,
                                },
                            },
                            ["actions"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "Action.Submit",
                                    ["title"] = "Send answer",
                                    ["data"] = JsonSerializer.Deserialize<Dictionary<string, string>>(followUpData, CodecJson),
                                },
                            },
                        },
                    });
                }
            }
        }

        if (answerLink is not null)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Answer here",
                ["url"] = answerLink,
            });
        }
        if (agnesLink is not null)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = "Open in Agnes",
                ["url"] = agnesLink,
            });
        }
        return actions;
    }

    private static bool TrySingleQuestionBinding(Notification notification, out string workItemId, out string questionId, out string correlationToken)
    {
        workItemId = questionId = correlationToken = string.Empty;
        if (notification.Actions is not { Count: > 0 })
            return false;
        var first = notification.Actions[0];
        foreach (var action in notification.Actions)
        {
            if (!string.Equals(action.WorkItemId, first.WorkItemId, StringComparison.Ordinal)
                || !string.Equals(action.QuestionId, first.QuestionId, StringComparison.Ordinal))
                return false;
        }
        workItemId = first.WorkItemId;
        questionId = first.QuestionId;
        correlationToken = string.IsNullOrWhiteSpace(notification.CorrelationToken)
            ? NotificationCorrelation.TokenFor(workItemId, questionId)
            : notification.CorrelationToken;
        return !string.IsNullOrWhiteSpace(workItemId) && !string.IsNullOrWhiteSpace(questionId);
    }
}
