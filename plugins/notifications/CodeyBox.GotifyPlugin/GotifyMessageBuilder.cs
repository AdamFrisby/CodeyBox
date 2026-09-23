using System.Text;
using CodeyBox.Core;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Pure rendering of a <see cref="Notification"/> onto the Gotify
/// <c>POST /message</c> body (<c>{title, message, priority, extras}</c>).
/// Severity maps to the message priority plus an emoji marker in the title;
/// summary and fields render as markdown (or plain text) rather than a
/// dumped blob. All decision logic lives here as input→output functions;
/// the provider only transports the result. Bounds come from
/// <see cref="GotifyPluginOptions"/> so every cap is operator-configurable.
///
/// <para>Gotify cannot carry an authenticated interaction — messages offer
/// no answer buttons and no callback scheme — so offered actions never
/// become interactive controls here. A question stays answerable through
/// the <see cref="Notification.AnswerUrl"/> route, surfaced both as an
/// in-body link and as the notification's click target
/// (<c>client::notification.click.url</c>).</para>
/// </summary>
internal static class GotifyMessageBuilder
{
    /// <summary>Gotify message priorities run 0–10; severities map onto
    /// operator-configured values and are clamped into this range.</summary>
    public const int MinPriority = 0;
    public const int MaxPriority = 10;

    /// <summary>Title cap. Gotify does not document a title limit; this is a
    /// presentation bound so a runaway title cannot swamp a push preview.</summary>
    public const int MaxTitleChars = 200;

    /// <summary>Emoji marker for a severity (unicode — Gotify clients render
    /// UTF-8 directly, unlike Slack's colon syntax).</summary>
    public static string EmojiFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "\U0001F6A8",   // rotating light
        NotificationSeverity.Warning => "⚠️",           // warning sign
        _ => "ℹ️",                                       // information
    };

    /// <summary>Map a severity onto the configured Gotify priority, clamped
    /// to the platform's 0–10 range.</summary>
    public static int PriorityFor(NotificationSeverity severity, GotifyPluginOptions options) =>
        Math.Clamp(severity switch
        {
            NotificationSeverity.Critical => options.CriticalPriority,
            NotificationSeverity.Warning => options.WarningPriority,
            _ => options.InformationPriority,
        }, MinPriority, MaxPriority);

    /// <summary>Escape markdown inline control characters in untrusted text
    /// so notification content cannot inject links or markup into the
    /// operator's client.</summary>
    public static string EscapeMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
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

    /// <summary>Whether <paramref name="url"/> is safe to emit as a rendered
    /// link / click target: an absolute http(s) URI only. The sink carries
    /// its own guard so a malformed or non-web scheme
    /// (<c>javascript:</c>, <c>file:</c>) can never reach a client.</summary>
    public static bool IsSafeLink(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Deep link from a notification to the Agnes front end for the
    /// owning work item. Agnes steers; this integration only links. Returns
    /// null when no base URL is configured or no work item is bound.</summary>
    public static string? AgnesWorkItemUrl(GotifyPluginOptions options, string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(options.AgnesBaseUrl) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        if (!IsSafeLink(options.AgnesBaseUrl))
            return null;
        return $"{options.AgnesBaseUrl.TrimEnd('/')}/workitems/{Uri.EscapeDataString(workItemId)}";
    }

    /// <summary>Build the <c>POST /message</c> JSON body for a
    /// notification.</summary>
    public static Dictionary<string, object?> BuildMessage(
        Notification notification,
        GotifyPluginOptions options)
    {
        var workItemId = BindWorkItemId(notification);
        var body = Truncate(notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);

        var message = new StringBuilder();
        if (options.Markdown)
            message.Append(EscapeMarkdown(body));
        else
            message.Append(body);

        AppendFields(message, notification, options);
        AppendLinks(message, notification, options, workItemId);
        AppendFooter(message, notification, options);

        var extras = new Dictionary<string, object?>
        {
            ["client::display"] = new Dictionary<string, object?>
            {
                ["contentType"] = options.Markdown ? "text/markdown" : "text/plain",
            },
        };
        var clickUrl = IsSafeLink(notification.AnswerUrl)
            ? notification.AnswerUrl
            : AgnesWorkItemUrl(options, workItemId);
        if (clickUrl is not null)
        {
            extras["client::notification"] = new Dictionary<string, object?>
            {
                ["click"] = new Dictionary<string, object?> { ["url"] = clickUrl },
            };
        }

        return new Dictionary<string, object?>
        {
            ["title"] = Truncate($"{EmojiFor(notification.Severity)} {notification.Title}", MaxTitleChars),
            ["message"] = message.ToString(),
            ["priority"] = PriorityFor(notification.Severity, options),
            ["extras"] = extras,
        };
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

    private static void AppendFields(StringBuilder message, Notification notification, GotifyPluginOptions options)
    {
        if (notification.Fields is null || options.MaxFields <= 0)
            return;
        var lines = new List<string>(Math.Min(notification.Fields.Count, options.MaxFields));
        foreach (var (key, value) in notification.Fields)
        {
            if (lines.Count >= options.MaxFields)
                break;
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var name = Truncate(key, 100);
            var val = Truncate(value, 500);
            lines.Add(options.Markdown
                ? $"- **{EscapeMarkdown(name)}**: {EscapeMarkdown(val)}"
                : $"{name}: {val}");
        }
        if (lines.Count > 0)
            message.Append("\n\n").Append(string.Join('\n', lines));
    }

    private static void AppendLinks(
        StringBuilder message,
        Notification notification,
        GotifyPluginOptions options,
        string? workItemId)
    {
        var answerUrl = IsSafeLink(notification.AnswerUrl) ? notification.AnswerUrl : null;
        var agnesUrl = AgnesWorkItemUrl(options, workItemId);
        if (answerUrl is null && agnesUrl is null)
            return;

        message.Append("\n\n");
        if (options.Markdown)
        {
            var links = new List<string>(2);
            if (answerUrl is not null)
                links.Add($"[Answer in CodeyBox]({answerUrl})");
            if (agnesUrl is not null)
                links.Add($"[Open in Agnes]({agnesUrl})");
            message.Append(string.Join(" · ", links));
            return;
        }
        if (answerUrl is not null)
            message.Append("Answer here: ").Append(answerUrl);
        if (agnesUrl is not null)
        {
            if (answerUrl is not null)
                message.Append('\n');
            message.Append("Open in Agnes: ").Append(agnesUrl);
        }
    }

    private static void AppendFooter(StringBuilder message, Notification notification, GotifyPluginOptions options)
    {
        var footer = $"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC";
        if (options.Markdown)
            message.Append("\n\n---\n*").Append(EscapeMarkdown(footer)).Append('*');
        else
            message.Append("\n\n").Append(footer);
    }
}
