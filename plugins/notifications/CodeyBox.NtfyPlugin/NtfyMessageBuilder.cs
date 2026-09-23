using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.NtfyPlugin;

/// <summary>
/// Pure rendering for ntfy notifications: severity → priority/tag, summary and
/// fields rendered as a plain-text message (ntfy renders markdown on the web
/// app only — the body stays readable on mobile), offered actions as native
/// <c>http</c>/<c>view</c> action buttons, and the decision update as a
/// same-<c>sequence_id</c> publish that replaces the question notification in
/// place. All decision logic lives here as input→output functions; the
/// provider only transports the result. Bounds come from
/// <see cref="NtfyPluginOptions"/> so every cap is operator-configurable.
/// </summary>
internal static class NtfyMessageBuilder
{
    /// <summary>ntfy accepts at most three action buttons per message.</summary>
    public const int NtfyMaxActions = 3;

    /// <summary>Header each <c>http</c> action button carries the payload MAC
    /// in. The name is fixed by <see cref="InteractionContract"/>: the host's
    /// ntfy verifier reads this same header, so it is not operator-overridable.</summary>
    public const string SignatureHeader = InteractionContract.DefaultSignatureHeader;

    /// <summary>Value prefix on <see cref="SignatureHeader"/>.</summary>
    public const string SignaturePrefix = "sha256=";

    /// <summary>Identity recorded for a button press. ntfy delivers no
    /// per-press user identity — the client invokes the action with exactly
    /// the request we rendered — so the actor is whoever holds a device
    /// subscribed to the topic. The grant is the connected channel.</summary>
    public const string SubscriberUserId = "subscriber";

    /// <summary>Endpoint-relative path action buttons call back to.</summary>
    public const string InteractionsPath = InteractionContract.RoutePrefix + "/ntfy";

    /// <summary>Upper bound the host's inbound endpoint places on the answer
    /// field (<see cref="InteractionContract.MaxAnswerChars"/>). A button whose
    /// answer would exceed it can never resolve, so it is not rendered.</summary>
    public const int MaxAnswerChars = InteractionContract.MaxAnswerChars;

    /// <summary>Sequence-id budget in characters; derived ids stay URL-safe
    /// ([A-Za-z0-9_-]) so they can ride the ntfy path/header forms too.</summary>
    public const int MaxSequenceIdChars = 96;

    /// <summary>Character budget for one rendered action-button label.</summary>
    public const int MaxActionLabelChars = 64;

    /// <summary>Character budgets for a structured field's key and value when
    /// rendered into the plain-text message body.</summary>
    public const int MaxFieldKeyChars = 100;
    public const int MaxFieldValueChars = 500;

    /// <summary>ntfy priority for the decision republish: below default, since
    /// the resolved question is informational, not a prompt.</summary>
    public const int DecidedPriority = 2;

    // Payload keys are emitted verbatim from the dictionary literals in
    // BuildInteractionBody — naming policies never apply to dictionary keys.
    private static readonly JsonSerializerOptions CodecJson = new();

    /// <summary>ntfy priority for a severity: 5 urgent, 4 high, 3 default.</summary>
    public static int PriorityFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 5,
        NotificationSeverity.Warning => 4,
        _ => 3,
    };

    /// <summary>ntfy tag (emoji shortcode) for a severity.</summary>
    public static string TagFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "rotating_light",
        NotificationSeverity.Warning => "warning",
        _ => "information_source",
    };

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

    /// <summary>Truncate to a UTF-8 <em>byte</em> budget, marking the cut and
    /// never splitting a code point. ntfy bounds the message body in bytes,
    /// so the text must be measured the way the server will measure it —
    /// multi-byte text would otherwise sail past the ceiling.</summary>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        if (maxBytes < 1)
            return string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        const string marker = "… (truncated)";
        var markerBytes = Encoding.UTF8.GetByteCount(marker);
        return maxBytes <= markerBytes
            ? CutUtf8(text, maxBytes)
            : CutUtf8(text, maxBytes - markerBytes) + marker;
    }

    /// <summary>Longest prefix of <paramref name="text"/> whose UTF-8
    /// encoding fits <paramref name="maxBytes"/>.</summary>
    private static string CutUtf8(string text, int maxBytes)
    {
        var used = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
                break;
            used += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars];
    }

    /// <summary>Deterministic sequence id for a correlation token. Publishing
    /// a second message with the same sequence id updates the notification in
    /// place — that is how a landed decision replaces the question prompt.
    /// Returns null when no token is bound.</summary>
    public static string? SequenceIdFor(string? correlationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationToken))
            return null;
        var slug = new StringBuilder("codeybox-", capacity: MaxSequenceIdChars + 16);
        foreach (var ch in correlationToken.Trim())
        {
            slug.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }
        var id = slug.ToString();
        if (id.Length <= MaxSequenceIdChars)
            return id;
        // Truncated slugs could collide; pin the tail to the full token.
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correlationToken)))
            .ToLowerInvariant()[..8];
        return $"{id[..(MaxSequenceIdChars - 9)]}-{suffix}";
    }

    /// <summary>Deterministic interaction id for one rendered button: the same
    /// notification re-dispatched claims the same id (replays answer as
    /// duplicates), while a re-notified question mints a fresh one. Derived
    /// from content already bound by the signature, never trusted input.</summary>
    public static string InteractionIdFor(string correlationToken, string answer, DateTimeOffset timestamp)
    {
        var basis = Encoding.UTF8.GetBytes($"{correlationToken}\n{answer}\n{timestamp.ToUnixTimeSeconds()}");
        return $"ntfy:{Convert.ToHexString(SHA256.HashData(basis)).ToLowerInvariant()}";
    }

    /// <summary>The canonical interaction payload a button posts back — the
    /// exact bytes the host endpoint verifies and parses. Serialization is
    /// fixed so the MAC minted here is over the same bytes the endpoint sees
    /// (ntfy relays the action <c>body</c> verbatim).</summary>
    public static string BuildInteractionBody(
        string workItemId,
        string questionId,
        string answer,
        string correlationToken,
        string topic,
        DateTimeOffset timestamp)
    {
        var payload = new Dictionary<string, object?>
        {
            ["interactionId"] = InteractionIdFor(correlationToken, answer, timestamp),
            ["workItemId"] = workItemId,
            ["questionId"] = questionId,
            ["answer"] = answer,
            ["user"] = new Dictionary<string, object?> { ["userId"] = SubscriberUserId },
            ["channelId"] = topic,
            ["correlationToken"] = correlationToken,
        };
        return JsonSerializer.Serialize(payload, CodecJson);
    }

    /// <summary>MAC a canonical interaction body for the
    /// <see cref="SignatureHeader"/> header. The notification carries only the
    /// MAC — never the secret — so a topic reader can replay at most the one
    /// decision that button already grants (dedup and question state absorb
    /// replays).</summary>
    public static string SignatureFor(string secret, string body) =>
        SignaturePrefix + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    /// <summary>Public callback URL for action buttons, or null when the
    /// configured base is missing/unsafe (buttons then degrade to links —
    /// a question must never render unanswerable). Matches the host's
    /// PublicBaseUrl policy exactly: HTTPS required (HTTP only for
    /// loopback), and credentials, a path, query, or fragment reject the
    /// value rather than being silently stripped.</summary>
    public static string? CallbackUrl(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl)
            || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var loopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;
        if (!https && !loopbackHttp)
            return null;
        return $"{uri.GetLeftPart(UriPartial.Authority)}{InteractionsPath}";
    }

    /// <summary>Deep link to the Agnes front end for the owning work item.
    /// Agnes steers; this integration only links. Null when unconfigured or
    /// when the configured base is not an absolute http(s) URI — the value
    /// lands in a rendered action URL, so it gets the same scheme guard as
    /// any other link.</summary>
    public static string? AgnesWorkItemUrl(NtfyPluginOptions options, string? workItemId)
    {
        if (string.IsNullOrWhiteSpace(options.AgnesBaseUrl) || string.IsNullOrWhiteSpace(workItemId))
            return null;
        if (!Uri.TryCreate(options.AgnesBaseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"{baseUri.ToString().TrimEnd('/')}/workitems/{Uri.EscapeDataString(workItemId)}";
    }

    /// <summary>True when <c>http</c> action buttons can be rendered: the
    /// operator asked for buttons AND a callback URL AND the signing secret
    /// are both available. Anything less degrades to links.</summary>
    public static bool CanRenderButtons(NtfyPluginOptions options, string? callbackUrl, string? secret) =>
        options.ActionsMode == NtfyActionsMode.Buttons
        && options.MaxActions > 0
        && callbackUrl is not null
        && !string.IsNullOrEmpty(secret);

    /// <summary>Build the ntfy publish-as-JSON body for a notification.
    /// <paramref name="topic"/> is the already-resolved target topic (also the
    /// channel the signed interaction binds to). <paramref name="secret"/> is
    /// the interaction secret used to MAC each button body; pass null when
    /// unset. Returns the payload and whether any authenticated answer buttons
    /// were rendered.</summary>
    public static (Dictionary<string, object?> Payload, bool ButtonsRendered) BuildPublishPayload(
        Notification notification,
        NtfyPluginOptions options,
        string topic,
        string? publicBaseUrl,
        string? secret)
    {
        var workItemId = BindWorkItemId(notification);
        var callbackUrl = CallbackUrl(publicBaseUrl);
        var buttonsOk = CanRenderButtons(options, callbackUrl, secret)
            && !string.IsNullOrWhiteSpace(workItemId);

        var actions = new List<object>();
        var buttonsRendered = false;
        if (buttonsOk && notification.Actions is { Count: > 0 })
        {
            foreach (var action in notification.Actions)
            {
                if (actions.Count >= Math.Min(options.MaxActions, NtfyMaxActions))
                    break;
                if (!string.Equals(action.WorkItemId, workItemId, StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrWhiteSpace(action.Label)
                    || string.IsNullOrEmpty(action.Value)
                    || action.Value.Length > MaxAnswerChars)
                    continue;
                var correlation = NotificationCorrelation.TokenFor(action.WorkItemId, action.QuestionId);
                var body = BuildInteractionBody(
                    action.WorkItemId, action.QuestionId, action.Value, correlation, topic, notification.Timestamp);
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "http",
                    ["label"] = Truncate(action.Label, MaxActionLabelChars),
                    ["url"] = callbackUrl,
                    ["method"] = "POST",
                    ["headers"] = new Dictionary<string, object?>
                    {
                        ["Content-Type"] = "application/json",
                        [SignatureHeader] = SignatureFor(secret!, body),
                    },
                    ["body"] = body,
                    ["clear"] = true,
                });
                buttonsRendered = true;
            }
        }

        // View actions fill the remaining slots (ntfy caps actions at three);
        // anything that does not fit is appended to the message as a plain
        // link line so the question stays answerable.
        var linkLines = new List<string>();
        var answerUrl = ValidLink(notification.AnswerUrl);
        var agnesUrl = AgnesWorkItemUrl(options, workItemId);
        var remaining = Math.Min(options.MaxActions, NtfyMaxActions) - actions.Count;
        var linkIdx = 0;
        foreach (var (label, url) in LinkButtons(answerUrl, agnesUrl, buttonsRendered))
        {
            if (linkIdx < remaining)
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action"] = "view",
                    ["label"] = label,
                    ["url"] = url,
                });
                linkIdx++;
            }
            else
            {
                linkLines.Add($"{label}: {url}");
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["topic"] = topic,
            ["title"] = Truncate(notification.Title, options.MaxTitleChars),
            ["message"] = BuildMessageText(notification, options, linkLines),
            ["priority"] = PriorityFor(notification.Severity),
            ["tags"] = new[] { TagFor(notification.Severity) },
        };
        if (actions.Count > 0)
            payload["actions"] = actions;
        var click = answerUrl ?? agnesUrl;
        if (click is not null)
            payload["click"] = click;
        var sequenceId = SequenceIdFor(notification.CorrelationToken);
        if (sequenceId is not null)
            payload["sequence_id"] = sequenceId;
        return (payload, buttonsRendered);
    }

    /// <summary>Build the same-<c>sequence_id</c> publish that replaces the
    /// question notification once its decision lands — the visible loop-close.
    /// <paramref name="decisionSummary"/> already reads as the finished line
    /// (e.g. <c>Decided: … — by …</c>).</summary>
    public static Dictionary<string, object?> BuildDecidedPayload(
        string topic,
        string title,
        string decisionSummary,
        string correlationToken,
        NtfyPluginOptions options)
    {
        return new Dictionary<string, object?>
        {
            ["topic"] = topic,
            ["title"] = Truncate($"✅ {title}", options.MaxTitleChars),
            ["message"] = TruncateUtf8(decisionSummary, options.MaxMessageBytes),
            ["priority"] = DecidedPriority,
            ["tags"] = new[] { "white_check_mark" },
            ["sequence_id"] = SequenceIdFor(correlationToken),
        };
    }

    private static string BuildMessageText(
        Notification notification,
        NtfyPluginOptions options,
        IReadOnlyList<string> linkLines)
    {
        var sb = new StringBuilder();
        sb.Append(notification.Body ?? notification.Summary ?? notification.Title);
        if (notification.Fields is { Count: > 0 } && options.MaxFields > 0)
        {
            var rendered = 0;
            foreach (var (key, value) in notification.Fields)
            {
                if (rendered >= options.MaxFields)
                    break;
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                sb.Append('\n').Append(Truncate(key.Trim(), MaxFieldKeyChars)).Append(": ").Append(Truncate(value, MaxFieldValueChars));
                rendered++;
            }
        }
        foreach (var line in linkLines)
            sb.Append('\n').Append(line);
        sb.Append('\n').Append($"CodeyBox · {notification.ConditionId} · {notification.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        return TruncateUtf8(sb.ToString(), options.MaxMessageBytes);
    }

    private static IEnumerable<(string Label, string Url)> LinkButtons(
        string? answerUrl,
        string? agnesUrl,
        bool buttonsRendered)
    {
        if (answerUrl is not null)
            yield return (buttonsRendered ? "Answer in CodeyBox" : "Answer here", answerUrl);
        if (agnesUrl is not null)
            yield return ("Open in Agnes", agnesUrl);
    }

    private static string? ValidLink(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            ? url
            : null;

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
}
