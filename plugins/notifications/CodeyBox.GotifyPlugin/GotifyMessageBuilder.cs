using System.Text;
using CodeyBox.Core;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Pure rendering of a <see cref="Notification"/> onto the Gotify
/// <c>POST /message</c> body (<c>{title, message, priority, extras}</c>).
/// Severity maps to the message priority plus an emoji marker in the title;
/// summary and fields render as markdown (or plain text) rather than a
/// dumped blob. All decision logic lives here as input→output functions;
/// the provider only transports the result. Operator-configurable bounds
/// come from <see cref="GotifyPluginOptions"/>; the fixed presentation caps
/// (title length, field name/value lengths) are named constants — shared
/// with every provider via <see cref="NotificationRendering"/>.
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

    /// <summary>Characters that would terminate a markdown link destination
    /// (<c>[label](dest)</c>) early and let the remainder render as
    /// arbitrary markup. Canonical <see cref="Uri.AbsoluteUri"/> output is
    /// already percent-encoded, so only legal literal URI characters can
    /// appear — parens and angle brackets among them.</summary>
    private static readonly char[] MarkdownDestUnsafe = ['(', ')', '<', '>'];

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

    /// <summary>Build the <c>POST /message</c> JSON body for a
    /// notification.</summary>
    public static Dictionary<string, object?> BuildMessage(
        Notification notification,
        GotifyPluginOptions options)
    {
        var workItemId = NotificationBinding.WorkItemIdFor(notification);
        var body = NotificationRendering.Truncate(
            notification.Body ?? notification.Summary ?? notification.Title, options.MaxTextChars);

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
        var clickUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri
            ?? NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (clickUrl is not null)
        {
            extras["client::notification"] = new Dictionary<string, object?>
            {
                ["click"] = new Dictionary<string, object?> { ["url"] = clickUrl },
            };
        }

        return new Dictionary<string, object?>
        {
            ["title"] = NotificationRendering.Truncate(
                $"{EmojiFor(notification.Severity)} {notification.Title}", MaxTitleChars),
            ["message"] = message.ToString(),
            ["priority"] = PriorityFor(notification.Severity, options),
            ["extras"] = extras,
        };
    }

    /// <summary>Whether <paramref name="canonicalUrl"/> is safe to
    /// interpolate as a markdown link destination: canonical
    /// <see cref="Uri.AbsoluteUri"/> form with no character that would close
    /// the destination early.</summary>
    private static bool IsMarkdownLinkSafe(string? canonicalUrl) =>
        canonicalUrl is not null && canonicalUrl.IndexOfAny(MarkdownDestUnsafe) < 0;

    /// <summary>Collapse line breaks so a field cannot break out of its
    /// bullet line and inject markdown block structure (fake headings,
    /// rules, list items) into the rendered message.</summary>
    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ');

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
            var name = NotificationRendering.Truncate(SingleLine(key), NotificationRendering.MaxFieldNameChars);
            var val = NotificationRendering.Truncate(SingleLine(value), NotificationRendering.MaxFieldValueChars);
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
        // Emit the canonical AbsoluteUri, never the raw string: a URI is
        // percent-encoded form, so whitespace and markup characters cannot
        // smuggle into the destination verbatim.
        var answerUrl = NotificationLinks.SafeLinkUri(notification.AnswerUrl)?.AbsoluteUri;
        var agnesUrl = NotificationLinks.AgnesWorkItemUrl(options.AgnesBaseUrl, workItemId);
        if (answerUrl is null && agnesUrl is null)
            return;

        if (options.Markdown)
        {
            var links = new List<string>(2);
            if (IsMarkdownLinkSafe(answerUrl))
                links.Add($"[Answer in CodeyBox]({answerUrl})");
            if (IsMarkdownLinkSafe(agnesUrl))
                links.Add($"[Open in Agnes]({agnesUrl})");
            if (links.Count > 0)
                message.Append("\n\n").Append(string.Join(" · ", links));
            return;
        }
        message.Append("\n\n");
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
