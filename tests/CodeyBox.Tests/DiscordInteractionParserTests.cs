using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Notifications;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the host-side Discord interaction parser: the recorded shape of
/// a real Discord MESSAGE_COMPONENT delivery maps onto the canonical fields
/// the verified endpoint answers. A live Discord application cannot be
/// reached from CI, so the fixture below is the integration evidence: it
/// preserves the exact envelope Discord POSTs to the Interactions Endpoint
/// URL, and the round-trip test pins the <c>custom_id</c> contract the
/// provider plugin emits against what this parser accepts.
/// </summary>
public sealed class DiscordInteractionParserTests
{
    /// <summary>Recorded shape of Discord's message-component delivery for a
    /// CodeyBox answer button. Ids are synthetic; the envelope — field
    /// naming, nesting, and types — mirrors Discord's documented
    /// interaction format. {CUSTOM_ID} is substituted per test.</summary>
    internal const string RecordedShape = """{"type":3,"id":"1234567890123456789","application_id":"111111111111111111","channel_id":"1100000000000000001","guild_id":"1200000000000000001","data":{"component_type":2,"custom_id":"{CUSTOM_ID}"},"member":{"user":{"id":"1300000000000000001","username":"alice","global_name":"Alice"}},"token":"interaction-token-not-used","message":{"id":"1400000000000000001","channel_id":"1100000000000000001"},"version":1}""";

    internal static string CustomId(string workItemId = "work-1", string questionId = "q-001", string answer = "Use rollbacks")
    {
        var customId = DiscordButtonCodec.Encode(workItemId, questionId, answer);
        Assert.NotNull(customId);
        return customId!;
    }

    [Fact]
    public void RecordedShape_MapsToCanonicalFields()
    {
        var body = Encoding.UTF8.GetBytes(RecordedShape.Replace("{CUSTOM_ID}", CustomId(), StringComparison.Ordinal));

        Assert.True(DiscordInteractionParser.TryParse(body, out var interaction, out var reason), reason);
        Assert.NotNull(interaction);
        Assert.Equal("discord:1234567890123456789", interaction!.InteractionId);
        Assert.Equal("work-1", interaction.WorkItemId);
        Assert.Equal("q-001", interaction.QuestionId);
        Assert.Equal("Use rollbacks", interaction.Answer);
        Assert.Equal("1300000000000000001", interaction.UserId);
        Assert.Equal("Alice", interaction.Login);
        Assert.Equal("1100000000000000001", interaction.ChannelId);
        Assert.Equal("1200000000000000001", interaction.GuildId);
        Assert.Equal("work-1:q-001", interaction.CorrelationToken);
    }

    [Fact]
    public void DmEnvelope_ParsesTopLevelUser()
    {
        var json = """{"type":3,"id":"999","application_id":"app","channel_id":"C1","data":{"component_type":2,"custom_id":"{CUSTOM_ID}"},"user":{"id":"U9","username":"bob"},"token":"t","version":1}"""
            .Replace("{CUSTOM_ID}", CustomId(), StringComparison.Ordinal);

        Assert.True(DiscordInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out var interaction, out var reason), reason);
        Assert.NotNull(interaction);
        Assert.Equal("discord:999", interaction!.InteractionId);
        Assert.Equal("U9", interaction.UserId);
        Assert.Equal("bob", interaction.Login);
        Assert.Equal("C1", interaction.ChannelId);
        Assert.Null(interaction.GuildId);
    }

    [Fact]
    public void RoundTrip_PinsProviderCodecAgainstParser()
    {
        var customId = CustomId("abc123", "q-007", "Ship it");
        var json = RecordedShape.Replace("{CUSTOM_ID}", customId, StringComparison.Ordinal);

        Assert.True(DiscordInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out var interaction, out var reason), reason);
        Assert.NotNull(interaction);
        Assert.Equal("abc123", interaction!.WorkItemId);
        Assert.Equal("q-007", interaction.QuestionId);
        Assert.Equal("Ship it", interaction.Answer);
    }

    [Fact]
    public void PingEnvelope_IsDetectedAsPing()
    {
        var ping = Encoding.UTF8.GetBytes("""{"type":1,"id":"p1","application_id":"app","version":1}""");

        Assert.True(DiscordInteractionParser.IsPingRequest(ping));
        Assert.False(DiscordInteractionParser.TryParse(ping, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void ComponentEnvelope_IsNotPing()
    {
        var body = Encoding.UTF8.GetBytes(RecordedShape.Replace("{CUSTOM_ID}", CustomId(), StringComparison.Ordinal));

        Assert.False(DiscordInteractionParser.IsPingRequest(body));
    }

    [Fact]
    public void NonButtonComponent_IsRejected()
    {
        var json = RecordedShape.Replace("{CUSTOM_ID}", CustomId(), StringComparison.Ordinal)
            .Replace("\"component_type\":2", "\"component_type\":3", StringComparison.Ordinal);

        Assert.False(DiscordInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void ForeignCustomId_IsRejected()
    {
        var json = RecordedShape.Replace("{CUSTOM_ID}", "someone-elses-button", StringComparison.Ordinal);

        Assert.False(DiscordInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void MissingUser_IsRejected()
    {
        var json = """{"type":3,"id":"999","application_id":"app","channel_id":"C1","data":{"component_type":2,"custom_id":"{CUSTOM_ID}"},"token":"t","version":1}"""
            .Replace("{CUSTOM_ID}", CustomId(), StringComparison.Ordinal);

        Assert.False(DiscordInteractionParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void MalformedBody_IsRejected()
    {
        Assert.False(DiscordInteractionParser.TryParse("not json"u8.ToArray(), out _, out var reason));
        Assert.NotEmpty(reason);
        Assert.False(DiscordInteractionParser.IsPingRequest("not json"u8.ToArray()));
    }

    [Fact]
    public void Codec_OversizedBinding_ReturnsNull()
    {
        var longAnswer = new string('a', 200);

        Assert.Null(DiscordButtonCodec.Encode("work-1", "q-001", longAnswer));
    }

    [Fact]
    public void Codec_RoundTripsShortAnswers()
    {
        Assert.True(DiscordButtonCodec.TryDecode(CustomId(), out var w, out var q, out var a));
        Assert.Equal("work-1", w);
        Assert.Equal("q-001", q);
        Assert.Equal("Use rollbacks", a);
    }

    [Fact]
    public void Codec_RejectsForeignOrTruncatedValues()
    {
        Assert.False(DiscordButtonCodec.TryDecode("other:1:2:3", out _, out _, out _));
        Assert.False(DiscordButtonCodec.TryDecode("cb:only-two", out _, out _, out _));
        Assert.False(DiscordButtonCodec.TryDecode(null, out _, out _, out _));
        Assert.False(DiscordButtonCodec.TryDecode(new string('x', 101), out _, out _, out _));
    }
}
