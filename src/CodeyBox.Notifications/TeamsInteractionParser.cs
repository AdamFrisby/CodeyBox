using System.Text.Json;

namespace CodeyBox.Notifications;

/// <summary>
/// Canonical form of one Teams <c>Action.Submit</c> interaction, mapped onto
/// the fields the generic inbound endpoint resolves through the question
/// store. Produced only from activities that already passed
/// <see cref="TeamsInteractionVerifier"/> — verification precedes parsing,
/// never the reverse.
/// </summary>
public sealed record TeamsCanonicalInteraction
{
    public required string InteractionId { get; init; }
    public required string WorkItemId { get; init; }
    public required string QuestionId { get; init; }
    public required string Answer { get; init; }
    public required string UserId { get; init; }
    public string? Login { get; init; }
    public string? ChannelId { get; init; }
    public required string CorrelationToken { get; init; }
}

/// <summary>
/// Maps a real Bot Framework activity (the JSON the bot's messaging
/// endpoint receives when an operator presses a card button or sends the
/// custom-answer follow-up) onto the canonical interaction the endpoint
/// answers exactly once via the question store.
///
/// <para>Submit <c>value</c> contract (shared with the Teams provider
/// plugin): <c>{"w": workItemId, "q": questionId, "a": answer,
/// "c": correlationToken}</c>. The follow-up prompt carries the same
/// binding without <c>a</c>; the typed text arrives merged under
/// <c>codeybox_custom</c> and resolves as the answer. Only
/// <c>type == "message"</c> activities are honoured — anything else is not a
/// CodeyBox answer and fails closed.</para>
///
/// <para>The interaction id derives from the activity <c>id</c>, which is
/// stable across the Connector's own retries of the same submit, so the
/// endpoint's idempotency claim answers each press exactly once without
/// touching the question store on replay.</para>
/// </summary>
public static class TeamsInteractionParser
{
    /// <summary>Input id for the custom-answer follow-up prompt. Mirrors the
    /// Teams provider plugin; both sides are pinned by the same
    /// recorded-shape test.</summary>
    public const string CustomAnswerInputId = "codeybox_custom";

    /// <summary>Parse the raw request body into a canonical interaction.
/// Returns false with a fixed-vocabulary reason when the body is not a
/// CodeyBox Teams submit. Callers must verify the signature first.</summary>
    public static bool TryParse(byte[] rawBody, out TeamsCanonicalInteraction? interaction, out string failureReason)
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

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException)
        {
            failureReason = "Teams activity is not valid JSON";
            return false;
        }

        using (doc)
        {
            return TryMap(doc.RootElement, out interaction, out failureReason);
        }
    }

    private static bool TryMap(JsonElement root, out TeamsCanonicalInteraction? interaction, out string failureReason)
    {
        interaction = null;
        failureReason = string.Empty;

        if (root.ValueKind != JsonValueKind.Object)
        {
            failureReason = "Teams activity is not an object";
            return false;
        }

        if (!root.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String
            || !string.Equals(typeProp.GetString(), "message", StringComparison.Ordinal))
        {
            failureReason = "unsupported Teams activity type";
            return false;
        }

        if (!root.TryGetProperty("id", out var idProp)
            || idProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            failureReason = "Teams activity has no id";
            return false;
        }
        var interactionId = $"teams:{idProp.GetString()}";

        if (!root.TryGetProperty("value", out var valueProp)
            || valueProp.ValueKind != JsonValueKind.Object)
        {
            failureReason = "activity carries no CodeyBox binding";
            return false;
        }

        if (!TryGetNonEmpty(valueProp, "w", out var workItemId)
            || !TryGetNonEmpty(valueProp, "q", out var questionId))
        {
            failureReason = "activity carries no CodeyBox binding";
            return false;
        }

        string answer;
        if (TryGetNonEmpty(valueProp, "a", out var bound))
            answer = bound;
        else if (TryGetNonEmpty(valueProp, CustomAnswerInputId, out var typed))
            answer = typed;
        else
        {
            failureReason = "activity carries no answer";
            return false;
        }

        var correlationToken = string.Empty;
        if (valueProp.TryGetProperty("c", out var tokenProp)
            && tokenProp.ValueKind == JsonValueKind.String)
            correlationToken = tokenProp.GetString() ?? string.Empty;

        var userId = string.Empty;
        string? login = null;
        if (root.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.Object)
        {
            if (fromProp.TryGetProperty("id", out var fromId) && fromId.ValueKind == JsonValueKind.String)
                userId = fromId.GetString() ?? string.Empty;
            if (fromProp.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                login = nameProp.GetString();
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            failureReason = "Teams activity has no sender";
            return false;
        }

        string? channelId = null;
        if (root.TryGetProperty("conversation", out var convProp) && convProp.ValueKind == JsonValueKind.Object
            && convProp.TryGetProperty("id", out var convId) && convId.ValueKind == JsonValueKind.String)
            channelId = convId.GetString();

        interaction = new TeamsCanonicalInteraction
        {
            InteractionId = interactionId,
            WorkItemId = workItemId,
            QuestionId = questionId,
            Answer = answer,
            UserId = userId,
            Login = login,
            ChannelId = channelId,
            CorrelationToken = correlationToken,
        };
        return true;
    }

    private static bool TryGetNonEmpty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }
}
