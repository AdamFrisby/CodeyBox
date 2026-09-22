using System.Net;
using System.Text.Json;

namespace CodeyBox.Notifications;

/// <summary>
/// Canonical form of one Slack <c>block_actions</c> interaction, mapped onto
/// the fields the generic inbound endpoint resolves through the question
/// store. Produced only from payloads that already passed
/// <see cref="SlackInteractionVerifier"/> — verification precedes parsing,
/// never the reverse.
/// </summary>
public sealed record SlackCanonicalInteraction
{
    public required string InteractionId { get; init; }
    public required string WorkItemId { get; init; }
    public required string QuestionId { get; init; }
    public required string Answer { get; init; }
    public required string UserId { get; init; }
    public string? Login { get; init; }
    public string? ChannelId { get; init; }
    public string? ResponseUrl { get; init; }
    public required string CorrelationToken { get; init; }
}

/// <summary>
/// Maps a real Slack interactive payload (<c>application/x-www-form-urlencoded</c>
/// body of the shape <c>payload={...block_actions JSON...}</c>, as Slack POSTs
/// to the app's Request URL) onto the canonical interaction the endpoint
/// answers exactly once via the question store.
///
/// <para>Button <c>value</c> contract (shared with the Slack provider
/// plugin): compact JSON <c>{"w": workItemId, "q": questionId, "a": answer,
/// "c": correlationToken}</c>, max 2000 chars. Only
/// <c>action_id == "codeybox_answer"</c> is honoured — anything else is not a
/// CodeyBox button and fails closed.</para>
///
/// <para>The interaction id derives from Slack's <c>trigger_id</c>, which is
/// stable across Slack's own retries of the same press, so the endpoint's
/// idempotency claim answers each press exactly once without touching the
/// question store on replay.</para>
/// </summary>
public static class SlackInteractionParser
{
    /// <summary>Action id the Slack provider stamps on answer buttons.</summary>
    public const string AnswerActionId = "codeybox_answer";

    /// <summary>Slack button <c>value</c> budget in characters.</summary>
    public const int MaxButtonValueChars = 2000;

    /// <summary>Parse the raw request body into a canonical interaction.
/// Returns false with a fixed-vocabulary reason when the body is not a
/// CodeyBox Slack action. Callers must verify the signature first.</summary>
    public static bool TryParse(byte[] rawBody, out SlackCanonicalInteraction? interaction, out string failureReason)
    {
        interaction = null;
        failureReason = string.Empty;

        string bodyText;
        try
        {
            bodyText = System.Text.Encoding.UTF8.GetString(rawBody);
        }
        catch (Exception)
        {
            failureReason = "body is not valid UTF-8";
            return false;
        }

        var json = ExtractPayloadJson(bodyText);
        if (json is null)
        {
            failureReason = "body carries no Slack payload";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            failureReason = "Slack payload is not valid JSON";
            return false;
        }

        using (doc)
        {
            return TryMap(doc.RootElement, out interaction, out failureReason);
        }
    }

    private static string? ExtractPayloadJson(string bodyText)
    {
        var text = bodyText.Trim();
        if (text.StartsWith("payload=", StringComparison.Ordinal))
        {
            var encoded = text["payload=".Length..];
            var amp = encoded.IndexOf('&');
            if (amp >= 0)
                encoded = encoded[..amp];
            try
            {
                return WebUtility.UrlDecode(encoded);
            }
            catch (Exception)
            {
                return null;
            }
        }
        if (text.StartsWith("{", StringComparison.Ordinal))
            return text;
        return null;
    }

    private static bool TryMap(JsonElement root, out SlackCanonicalInteraction? interaction, out string failureReason)
    {
        interaction = null;
        failureReason = string.Empty;

        if (root.ValueKind != JsonValueKind.Object)
        {
            failureReason = "Slack payload is not an object";
            return false;
        }

        if (!root.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String
            || !string.Equals(typeProp.GetString(), "block_actions", StringComparison.Ordinal))
        {
            failureReason = "unsupported Slack interaction type";
            return false;
        }

        if (!root.TryGetProperty("trigger_id", out var triggerProp)
            || triggerProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(triggerProp.GetString()))
        {
            failureReason = "Slack payload has no trigger_id";
            return false;
        }
        var interactionId = $"slack:{triggerProp.GetString()}";

        if (!root.TryGetProperty("actions", out var actionsProp)
            || actionsProp.ValueKind != JsonValueKind.Array
            || actionsProp.GetArrayLength() == 0)
        {
            failureReason = "Slack payload carries no actions";
            return false;
        }
        var action = actionsProp[0];
        if (action.ValueKind != JsonValueKind.Object
            || !action.TryGetProperty("action_id", out var actionIdProp)
            || actionIdProp.ValueKind != JsonValueKind.String
            || !string.Equals(actionIdProp.GetString(), AnswerActionId, StringComparison.Ordinal))
        {
            failureReason = "not a CodeyBox answer action";
            return false;
        }
        if (!action.TryGetProperty("value", out var valueProp)
            || valueProp.ValueKind != JsonValueKind.String)
        {
            failureReason = "answer action carries no value";
            return false;
        }
        var value = valueProp.GetString() ?? string.Empty;
        if (value.Length == 0 || value.Length > MaxButtonValueChars)
        {
            failureReason = "answer value is missing or oversized";
            return false;
        }
        if (!TryDecodeValue(value, out var workItemId, out var questionId, out var answer, out var correlationToken))
        {
            failureReason = "answer value is not a CodeyBox binding";
            return false;
        }

        var userId = string.Empty;
        string? login = null;
        if (root.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.Object)
        {
            if (userProp.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                userId = idProp.GetString() ?? string.Empty;
            if (userProp.TryGetProperty("username", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                login = nameProp.GetString();
            if (string.IsNullOrEmpty(login)
                && userProp.TryGetProperty("name", out var altName) && altName.ValueKind == JsonValueKind.String)
                login = altName.GetString();
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            failureReason = "Slack payload has no user";
            return false;
        }

        string? channelId = null;
        if (root.TryGetProperty("channel", out var channelProp) && channelProp.ValueKind == JsonValueKind.Object
            && channelProp.TryGetProperty("id", out var channelIdProp) && channelIdProp.ValueKind == JsonValueKind.String)
            channelId = channelIdProp.GetString();

        string? responseUrl = null;
        if (root.TryGetProperty("response_url", out var responseProp) && responseProp.ValueKind == JsonValueKind.String)
            responseUrl = responseProp.GetString();

        interaction = new SlackCanonicalInteraction
        {
            InteractionId = interactionId,
            WorkItemId = workItemId,
            QuestionId = questionId,
            Answer = answer,
            UserId = userId,
            Login = login,
            ChannelId = channelId,
            ResponseUrl = responseUrl,
            CorrelationToken = correlationToken,
        };
        return true;
    }

    private static bool TryDecodeValue(string value, out string workItemId, out string questionId, out string answer, out string correlationToken)
    {
        workItemId = questionId = answer = correlationToken = string.Empty;
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
}
