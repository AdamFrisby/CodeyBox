using CodeyBox.Core;

namespace CodeyBox.MattermostPlugin;

/// <summary>
/// Pure rendering of a <see cref="Notification"/> onto the Mattermost
/// <c>POST /api/v4/posts</c> body (<c>{channel_id, message, root_id, props}</c>).
/// Severity maps to an emoji marker in the heading; summary and fields render
/// as markdown rather than a dumped blob. All decision logic lives here as
/// input→output functions; the provider only transports the result.
/// Operator-configurable bounds come from <see cref="MattermostPluginOptions"/>;
/// the fixed presentation caps (heading length, field name/value lengths, the
/// platform post limit) are named constants — the field caps shared with every
/// provider via <see cref="NotificationRendering"/>.
///
/// <para>Mattermost is notification-only here: offered actions never become
/// interactive controls (no post action the host could verify backs them),
/// so a question stays answerable through the
/// <see cref="Notification.AnswerUrl"/> route, surfaced as in-body links.</para>
/// </summary>
internal static class MattermostMessageBuilder
{
    /// <summary>Mattermost truncates post messages past this length; the
    /// builder stays under it so the server never silently cuts an answer
    /// link. Platform constant (message max length), not an operator knob.</summary>
    public const int MattermostMaxMessageChars = 16383;

    /// <summary>Heading cap: a presentation bound so a runaway title cannot
    /// swamp the channel view.</summary>
    public const int MaxHeadingChars = 200;

    /// <summary>Characters that would terminate a markdown link destination
    /// (<c>[label](dest)</c>) early and let the remainder render as
    /// arbitrary markup. Canonical <see cref="Uri.AbsoluteUri"/> output is
    /// already percent-encoded, so only legal literal URI characters can
    /// appear — parens and angle brackets among them.</summary>
    private static readonly char[] MarkdownDestUnsafe = ['(', ')', '<', '>'];

    /// <summary>Emoji marker for a severity.</summary>
    public static string EmojiFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => ":rotating_light:",
        NotificationSeverity.Warning => ":warning:",
        _ => ":information_source:",
    };

    /// <summary>Escape markdown inline control characters in untrusted text
    /// so notification content cannot inject links or markup into the
    /// operator's client.</summary>
    public static string EscapeMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Build the <c>POST /api/v4/posts</c> body for a notification
    /// (before channel routing). Returns the payload plus the work item id
    /// when one could be bound (used for thread routing and dedup).</summary>
    public static (Dictionary<string, object?> Payload, string? WorkItemId) BuildMessage(
        Notification notification,
        MattermostPluginOptions options)
    {
        var workItemId = NotificationBinding.WorkItemIdFor(notification);
        var body = NotificationRendering.Truncate(
            notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);

        var message = new System.Text.StringBuilder();
        message.Append("## ").Append(EscapeMarkdown(NotificationRendering.Truncate(
            $"{EmojiFor(notification.Severity)} {notification.Title}", MaxHeadingChars)));
        message.Append("\n\n").Append(EscapeMarkdown(body));

        AppendFields(message, notification, options);
        AppendLinks(message, notification, options, workItemId);
        message.Append("\n\n---\n*").Append(EscapeMarkdown(
            $"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC")).Append('*');

        var text = message.ToString();
        if (text.Length > MattermostMaxMessageChars)
            text = NotificationRendering.Truncate(text, MattermostMaxMessageChars);

        var payload = new Dictionary<string, object?>
        {
            ["message"] = text,
            ["props"] = new Dictionary<string, object?>
            {
                ["codeybox_condition"] = notification.ConditionId,
                ["codeybox_work_item"] = workItemId,
            },
        };
        return (payload, workItemId);
    }

    /// <summary>Whether <paramref name="canonicalUrl"/> is safe to
    /// interpolate as a markdown link destination: canonical
    /// <see cref="Uri.AbsoluteUri"/> form with no character that would close
    /// the destination early.</summary>
    internal static bool IsMarkdownLinkSafe(string? canonicalUrl) =>
        canonicalUrl is not null && canonicalUrl.IndexOfAny(MarkdownDestUnsafe) < 0;

    /// <summary>Collapse line breaks so a field cannot break out of its
    /// bullet line and inject markdown block structure (fake headings,
    /// rules, list items) into the rendered post.</summary>
    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');

    private static void AppendFields(
        System.Text.StringBuilder message,
        Notification notification,
        MattermostPluginOptions options)
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
            lines.Add($"- **{EscapeMarkdown(name)}**: {EscapeMarkdown(val)}");
        }
        if (lines.Count > 0)
            message.Append("\n\n").Append(string.Join('\n', lines));
    }

    private static void AppendLinks(
        System.Text.StringBuilder message,
        Notification notification,
        MattermostPluginOptions options,
        string? workItemId)
    {
        // Emit the canonical AbsoluteUri, never the raw string: a URI is
        // percent-encoded form, so whitespace and markup characters cannot
        // smuggle into the destination verbatim.
        var answerUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var agnesUrl = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (answerUrl is null && agnesUrl is null)
            return;

        var links = new List<string>(2);
        if (IsMarkdownLinkSafe(answerUrl))
            links.Add($"[Answer in CodeyBox]({answerUrl})");
        if (IsMarkdownLinkSafe(agnesUrl))
            links.Add($"[Open in Agnes]({agnesUrl})");
        if (links.Count > 0)
            message.Append("\n\n").Append(string.Join(" · ", links));
    }
}
