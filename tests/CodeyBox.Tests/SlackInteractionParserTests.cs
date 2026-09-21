using System.Text;
using System.Text.Json;
using CodeyBox.Notifications;
using CodeyBox.SlackPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the host-side Slack interaction parser: the recorded shape of a
/// real Slack <c>block_actions</c> delivery maps onto the canonical fields
/// the verified endpoint answers. A live Slack workspace cannot be reached
/// from CI, so the fixture below is the integration evidence: it preserves
/// the exact envelope Slack POSTs to an app Request URL
/// (<c>payload={...}</c> form body), and the round-trip test pins the button
/// <c>value</c> contract the provider plugin emits against what this parser
/// accepts.
/// </summary>
public sealed class SlackInteractionParserTests
{
    /// <summary>Recorded shape of Slack's block_actions delivery for a
    /// CodeyBox answer button. Field values are synthetic; the envelope —
    /// form body, nesting, and naming — mirrors Slack's documented format.
    /// {VALUE} is substituted per test.</summary>
    private const string RecordedShape = """{"type":"block_actions","team":{"id":"T123","domain":"example"},"user":{"id":"U123","username":"alice","name":"alice","team_id":"T123"},"api_app_id":"A123","token":"verification-token-not-used","container":{"type":"message","message_ts":"1758640000.001200","channel_id":"C999","is_ephemeral":false},"trigger_id":"TRIGGER123","channel":{"id":"C999","name":"ops"},"message":{"type":"message","subtype":"bot_message","ts":"1758640000.001200","text":"fallback"},"response_url":"https://hooks.slack.com/actions/T123/123/abc","actions":[{"action_id":"codeybox_answer","block_id":"codeybox_actions_3","text":{"type":"plain_text","text":"Use rollbacks","emoji":true},"value":"{VALUE}","type":"button","action_ts":"1758640010.002300"}]}""";

    private static string ButtonValue(string workItemId = "work-1", string questionId = "q-001", string answer = "Use rollbacks")
    {
        var value = SlackBlockKit.EncodeButtonValue(workItemId, questionId, answer, $"{workItemId}:{questionId}");
        Assert.NotNull(value);
        return value!.Replace("\"", "\\\"");
    }

    private static byte[] FormBody(string payloadJson)
        => Encoding.UTF8.GetBytes("payload=" + Uri.EscapeDataString(payloadJson));

    [Fact]
    public void RecordedShape_MapsToCanonicalFields()
    {
        var body = FormBody(RecordedShape.Replace("{VALUE}", ButtonValue(), StringComparison.Ordinal));

        Assert.True(SlackInteractionParser.TryParse(body, out var interaction, out var reason), reason);
        Assert.NotNull(interaction);
        Assert.Equal("slack:TRIGGER123", interaction!.InteractionId);
        Assert.Equal("work-1", interaction.WorkItemId);
        Assert.Equal("q-001", interaction.QuestionId);
        Assert.Equal("Use rollbacks", interaction.Answer);
        Assert.Equal("U123", interaction.UserId);
        Assert.Equal("alice", interaction.Login);
        Assert.Equal("C999", interaction.ChannelId);
        Assert.Equal("https://hooks.slack.com/actions/T123/123/abc", interaction.ResponseUrl);
        Assert.Equal("work-1:q-001", interaction.CorrelationToken);
    }

    [Fact]
    public void RawJsonBody_ParsesWithoutFormEnvelope()
    {
        var json = RecordedShape.Replace("{VALUE}", ButtonValue(), StringComparison.Ordinal);

        Assert.True(SlackInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out var interaction, out var reason), reason);
        Assert.NotNull(interaction);
        Assert.Equal("work-1", interaction!.WorkItemId);
    }

    [Fact]
    public void ForeignActionId_FailsClosed()
    {
        var json = RecordedShape
            .Replace("{VALUE}", ButtonValue(), StringComparison.Ordinal)
            .Replace("codeybox_answer", "some_other_app", StringComparison.Ordinal);

        Assert.False(SlackInteractionParser.TryParse(FormBody(json), out _, out var reason));
        Assert.Contains("not a CodeyBox", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonBlockActionsType_FailsClosed()
    {
        var json = RecordedShape
            .Replace("{VALUE}", ButtonValue(), StringComparison.Ordinal)
            .Replace("block_actions", "view_submission", StringComparison.Ordinal);

        Assert.False(SlackInteractionParser.TryParse(FormBody(json), out _, out var reason));
        Assert.Contains("unsupported", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TamperedValue_FailsClosed()
    {
        var json = RecordedShape.Replace("{VALUE}", "tampered", StringComparison.Ordinal);

        Assert.False(SlackInteractionParser.TryParse(FormBody(json), out _, out var reason));
        Assert.Contains("binding", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyBody_FailsClosed()
    {
        Assert.False(SlackInteractionParser.TryParse([], out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void ProviderValueContract_MatchesParserExpectation()
    {
        // The plugin encodes; the host parses. This pins both ends together:
        // any drift in field names or shape breaks here, not in production.
        var encoded = SlackBlockKit.EncodeButtonValue("w-42", "q-7", "Ship it", "w-42:q-7");
        Assert.NotNull(encoded);
        var json = RecordedShape.Replace("{VALUE}", encoded!.Replace("\"", "\\\"", StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.True(SlackInteractionParser.TryParse(FormBody(json), out var interaction, out var reason), reason);
        Assert.Equal("w-42", interaction!.WorkItemId);
        Assert.Equal("q-7", interaction.QuestionId);
        Assert.Equal("Ship it", interaction.Answer);
    }
}
