using System.Text;
using CodeyBox.Core;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Pure rendering of a <see cref="Notification"/> onto the Matrix
/// <c>m.room.message</c> event body (<c>{msgtype, body, format,
/// formatted_body, m.relates_to}</c>). Severity maps to an emoji marker in
/// the text; summary and fields render as plain text with an HTML parallel
/// (<c>org.matrix.custom.html</c>) carrying real answer links rather than a
/// dumped blob. All decision logic lives here as input→output functions; the
/// provider only transports the result. Operator-configurable bounds come from
/// <see cref="MatrixPluginOptions"/>; the fixed presentation caps (field
/// name/value lengths) are shared with every provider via
/// <see cref="NotificationRendering"/>.
///
/// <para>Matrix cannot carry an authenticated interaction here — offered
/// actions never become interactive controls. A question stays answerable
/// through the <see cref="Notification.AnswerUrl"/> route, surfaced both as
/// a raw URL in the plain-text body and as an anchor in the HTML body.</para>
/// </summary>
internal static class MatrixMessageBuilder
{
    /// <summary>Matrix message type for plain human-readable text.</summary>
    public const string MsgTypeText = "m.text";

    /// <summary>HTML message format marker.</summary>
    public const string HtmlFormat = "org.matrix.custom.html";

    /// <summary>Thread relation type: a reply in the thread rooted at the
    /// referenced event.</summary>
    public const string ThreadRelationType = "m.thread";

    /// <summary>Emoji marker for a severity (unicode — Matrix clients render
    /// UTF-8 directly).</summary>
    public static string EmojiFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "\U0001F6A8",   // rotating light
        NotificationSeverity.Warning => "⚠️",           // warning sign
        _ => "ℹ️",                                       // information
    };

    /// <summary>Escape HTML element/attribute control characters in untrusted
    /// text so notification content cannot inject markup into clients that
    /// render <c>formatted_body</c>.</summary>
    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    /// <summary>Build the <c>m.room.message</c> event content for a
    /// notification. Returns the event content plus the work item id when one
    /// could be bound (used for thread routing and event mapping).</summary>
    public static (Dictionary<string, object?> Content, string? WorkItemId) BuildMessage(
        Notification notification,
        MatrixPluginOptions options,
        string? threadRootEventId)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(options);

        var workItemId = NotificationBinding.WorkItemIdFor(notification);
        var body = NotificationRendering.Truncate(
            notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);
        var title = $"{EmojiFor(notification.Severity)} {notification.Title}";

        var plain = new StringBuilder();
        plain.Append(title);
        plain.Append("\n\n").Append(body);
        AppendPlainFields(plain, notification, options);
        AppendPlainLinks(plain, notification, options, workItemId);
        plain.Append("\n\n").Append(
            $"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC");

        var content = new Dictionary<string, object?>
        {
            ["msgtype"] = MsgTypeText,
            ["body"] = plain.ToString(),
        };

        if (options.UseHtml)
        {
            var html = new StringBuilder();
            html.Append("<strong>").Append(EscapeHtml(title)).Append("</strong>");
            html.Append("<p>").Append(EscapeHtml(body).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            AppendHtmlFields(html, notification, options);
            AppendHtmlLinks(html, notification, options, workItemId);
            html.Append("<p><em>").Append(EscapeHtml(
                $"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC")).Append("</em></p>");
            content["format"] = HtmlFormat;
            content["formatted_body"] = html.ToString();
        }

        if (!string.IsNullOrWhiteSpace(threadRootEventId))
        {
            content["m.relates_to"] = new Dictionary<string, object?>
            {
                ["rel_type"] = ThreadRelationType,
                ["event_id"] = threadRootEventId,
            };
        }

        return (content, workItemId);
    }

    /// <summary>Collapse line breaks so a field cannot break out of its line
    /// and inject block structure into the rendered message.</summary>
    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');

    private static void AppendPlainFields(StringBuilder plain, Notification notification, MatrixPluginOptions options)
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
            var name = NotificationRendering.Truncate(SingleLine(key), NotificationRendering.MaxFieldNameChars);
            var val = NotificationRendering.Truncate(SingleLine(value), NotificationRendering.MaxFieldValueChars);
            lines.Add($"{name}: {val}");
        }
        if (lines.Count > 0)
            plain.Append("\n\n").Append(string.Join('\n', lines));
    }

    private static void AppendPlainLinks(
        StringBuilder plain,
        Notification notification,
        MatrixPluginOptions options,
        string? workItemId)
    {
        // Emit the canonical AbsoluteUri, never the raw string: a URI is
        // percent-encoded form, so whitespace and markup characters cannot
        // smuggle into the destination verbatim.
        var answerUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var agnesUrl = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (answerUrl is null && agnesUrl is null)
            return;
        plain.Append("\n\n");
        if (answerUrl is not null)
            plain.Append("Answer here: ").Append(answerUrl);
        if (agnesUrl is not null)
        {
            if (answerUrl is not null)
                plain.Append('\n');
            plain.Append("Open in Agnes: ").Append(agnesUrl);
        }
    }

    private static void AppendHtmlFields(StringBuilder html, Notification notification, MatrixPluginOptions options)
    {
        if (notification.Fields is null || options.MaxFields <= 0)
            return;
        var items = new List<string>(Math.Min(notification.Fields.Count, options.MaxFields));
        foreach (var (key, value) in notification.Fields)
        {
            if (items.Count >= options.MaxFields)
                break;
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var name = NotificationRendering.Truncate(SingleLine(key), NotificationRendering.MaxFieldNameChars);
            var val = NotificationRendering.Truncate(SingleLine(value), NotificationRendering.MaxFieldValueChars);
            items.Add($"<li><strong>{EscapeHtml(name)}</strong>: {EscapeHtml(val)}</li>");
        }
        if (items.Count > 0)
            html.Append("<ul>").Append(string.Join(string.Empty, items)).Append("</ul>");
    }

    private static void AppendHtmlLinks(
        StringBuilder html,
        Notification notification,
        MatrixPluginOptions options,
        string? workItemId)
    {
        var answerUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var agnesUrl = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (answerUrl is null && agnesUrl is null)
            return;
        var links = new List<string>(2);
        if (answerUrl is not null)
            links.Add($"<a href=\"{EscapeHtml(answerUrl)}\">Answer in CodeyBox</a>");
        if (agnesUrl is not null)
            links.Add($"<a href=\"{EscapeHtml(agnesUrl)}\">Open in Agnes</a>");
        html.Append("<p>").Append(string.Join(" · ", links)).Append("</p>");
    }
}
