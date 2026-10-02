using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Canonical form of one Discord message-component interaction, mapped onto
/// the fields the generic inbound endpoint resolves through the question
/// store. Produced only from envelopes that already passed
/// <see cref="DiscordInteractionVerifier"/> — verification precedes parsing,
/// never the reverse.
/// </summary>
public sealed record DiscordCanonicalInteraction
{
    public required string InteractionId { get; init; }
    public required string WorkItemId { get; init; }
    public required string QuestionId { get; init; }
    public required string Answer { get; init; }
    public required string UserId { get; init; }
    public string? Login { get; init; }
    public string? ChannelId { get; init; }
    public string? GuildId { get; init; }
    public required string CorrelationToken { get; init; }
}

/// <summary>
/// Maps a real Discord interaction envelope onto the canonical interaction
/// the endpoint answers exactly once via the question store.
///
/// <para>Only <c>type == 3</c> (MESSAGE_COMPONENT) button presses
/// (<c>component_type == 2</c>) whose <c>custom_id</c> decodes through
/// <see cref="DiscordButtonCodec"/> are honoured — anything else is not a
/// CodeyBox button and fails closed. <c>type == 1</c> (PING) is the
/// registration challenge: it carries no decision and is answered with
/// <c>{"type": 1}</c> by the endpoint before any parsing.</para>
///
/// <para>The interaction id derives from Discord's interaction
/// <c>id</c>, which is stable across Discord's own retries of the same
/// press, so the endpoint's idempotency claim answers each press exactly
/// once without touching the question store on replay.</para>
/// </summary>
public static class DiscordInteractionParser
{
    /// <summary>Discord interaction type: server registration challenge.</summary>
    public const int PingType = 1;

    /// <summary>Discord interaction type: message component (button press).</summary>
    public const int MessageComponentType = 3;

    /// <summary>Discord component type: button.</summary>
    public const int ButtonComponentType = 2;

    /// <summary>True when the raw body is a Discord PING challenge
    /// (<c>{"type": 1, ...}</c>). PING carries no user decision; the
    /// endpoint answers it with <c>{"type": 1}</c> after verification.</summary>
    public static bool IsPingRequest(byte[] rawBody)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(rawBody).Trim();
            if (!text.StartsWith("{", StringComparison.Ordinal))
                return false;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.Number
                && typeProp.GetInt32() == PingType;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parse the raw request body into a canonical interaction.
/// Returns false with a fixed-vocabulary reason when the body is not a
/// CodeyBox Discord action. Callers must verify the signature first.</summary>
    public static bool TryParse(byte[] rawBody, out DiscordCanonicalInteraction? interaction, out string failureReason)
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
            failureReason = "Discord payload is not valid JSON";
            return false;
        }

        using (doc)
        {
            return TryMap(doc.RootElement, out interaction, out failureReason);
        }
    }

    private static bool TryMap(JsonElement root, out DiscordCanonicalInteraction? interaction, out string failureReason)
    {
        interaction = null;
        failureReason = string.Empty;

        if (root.ValueKind != JsonValueKind.Object)
        {
            failureReason = "Discord payload is not an object";
            return false;
        }

        if (!root.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.Number
            || typeProp.GetInt32() != MessageComponentType)
        {
            failureReason = "unsupported Discord interaction type";
            return false;
        }

        if (!root.TryGetProperty("id", out var idProp)
            || idProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            failureReason = "Discord payload has no interaction id";
            return false;
        }
        var interactionId = $"discord:{idProp.GetString()}";

        if (!root.TryGetProperty("data", out var dataProp)
            || dataProp.ValueKind != JsonValueKind.Object
            || !dataProp.TryGetProperty("component_type", out var componentProp)
            || componentProp.ValueKind != JsonValueKind.Number
            || componentProp.GetInt32() != ButtonComponentType
            || !dataProp.TryGetProperty("custom_id", out var customIdProp)
            || customIdProp.ValueKind != JsonValueKind.String)
        {
            failureReason = "not a CodeyBox answer action";
            return false;
        }

        if (!DiscordButtonCodec.TryDecode(customIdProp.GetString(), out var workItemId, out var questionId, out var answer))
        {
            failureReason = "answer action is not a CodeyBox binding";
            return false;
        }

        var userElement = FindUser(root);
        if (userElement is null
            || !userElement.Value.TryGetProperty("id", out var userIdProp)
            || userIdProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(userIdProp.GetString()))
        {
            failureReason = "Discord payload has no user";
            return false;
        }
        var userId = userIdProp.GetString()!;
        string? login = null;
        if (userElement.Value.TryGetProperty("global_name", out var globalName)
            && globalName.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(globalName.GetString()))
            login = globalName.GetString();
        else if (userElement.Value.TryGetProperty("username", out var username)
            && username.ValueKind == JsonValueKind.String)
            login = username.GetString();

        string? channelId = null;
        if (root.TryGetProperty("channel_id", out var channelProp) && channelProp.ValueKind == JsonValueKind.String)
            channelId = channelProp.GetString();
        string? guildId = null;
        if (root.TryGetProperty("guild_id", out var guildProp) && guildProp.ValueKind == JsonValueKind.String)
            guildId = guildProp.GetString();

        interaction = new DiscordCanonicalInteraction
        {
            InteractionId = interactionId,
            WorkItemId = workItemId,
            QuestionId = questionId,
            Answer = answer,
            UserId = userId,
            Login = login,
            ChannelId = channelId,
            GuildId = guildId,
            CorrelationToken = NotificationCorrelation.TokenFor(workItemId, questionId),
        };
        return true;
    }

    private static JsonElement? FindUser(JsonElement root)
    {
        if (root.TryGetProperty("member", out var member)
            && member.ValueKind == JsonValueKind.Object
            && member.TryGetProperty("user", out var memberUser)
            && memberUser.ValueKind == JsonValueKind.Object)
            return memberUser;
        if (root.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object)
            return user;
        return null;
    }
}
