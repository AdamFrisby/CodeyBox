using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.DiscordPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Discord notification plugin: the embed carries
/// severity/summary/fields, offered actions become native buttons bound to
/// the question, follow-ups thread per work item, decisions edit the
/// original message, and delivery failures never escape. Each test drives
/// the real provider against a captured HTTP transport and asserts on the
/// bytes the plugin itself posted.
/// </summary>
public sealed class DiscordNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_DISCORD_BOT_TOKEN";
    private const string Token = "test-discord-bot-token";
    private const string Channel = "1100000000000000001";

    private static Notification MakeNotification(
        string conditionId = "queue_empty",
        string title = "Queue.device is empty",
        string? body = "All work items processed.",
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyDictionary<string, string>? fields = null,
        IReadOnlyList<NotificationAction>? actions = null,
        string? answerUrl = null,
        string? correlationToken = null,
        IReadOnlyList<string>? recipients = null)
        => new()
        {
            ConditionId = conditionId,
            Title = title,
            Body = body,
            Severity = severity,
            Fields = fields,
            Actions = actions,
            AnswerUrl = answerUrl,
            CorrelationToken = correlationToken,
            Recipients = recipients,
            Timestamp = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
        };

    private static NotificationAction Action(string workItemId = "work-1", string questionId = "q-001", string label = "Use rollbacks")
        => new() { Label = label, Value = label, WorkItemId = workItemId, QuestionId = questionId };

    private static IConfiguration Config(Dictionary<string, string?> values)
    {
        values["CodeyBox:Plugins:codeybox.discord:BotTokenEnvVar"] = TokenEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string? defaultChannel = Channel,
        string agnesBaseUrl = "https://agnes.example.invalid",
        string actionsMode = "Buttons")
        => Config(new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.discord:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.discord:DefaultChannelId"] = defaultChannel,
            ["CodeyBox:Plugins:codeybox.discord:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.discord:ActionsMode"] = actionsMode,
        });

    private static string MessageJson(string channel = Channel, string id = "1400000000000000001")
        => $"{{\"id\":\"{id}\",\"channel_id\":\"{channel}\"}}";

    private static string ThreadJson(string id = "1500000000000000001")
        => $"{{\"id\":\"{id}\",\"type\":11}}";

    private static DiscordNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        DiscordThreadStore? threads = null)
        => new(config, http, new CapturingLogger<DiscordNotificationProvider>(), clock: null, threads: threads);

    private static JsonElement BodyJson(CapturingHttpHandler.CapturedRequest request)
        => JsonDocument.Parse(request.Body).RootElement;

    [Fact]
    public async Task Disabled_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(Config(new Dictionary<string, string?>()), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MissingToken_SkipsDelivery()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, null);
        var handler = new CapturingHttpHandler();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FleetNotification_PostsEmbedWithSeverityAndFields()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageJson()) });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string> { ["Project"] = "demo" }), CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://discord.com/api/v10/channels/{Channel}/messages", request.Url);
            Assert.Equal("Bot test-discord-bot-token", request.Authorization);
            var root = BodyJson(request);
            var embed = root.GetProperty("embeds")[0];
            Assert.Equal(0xE01E5A, embed.GetProperty("color").GetInt32());
            Assert.Equal("Project", embed.GetProperty("fields")[0].GetProperty("name").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task WorkItemNotification_StartsThreadAndRoutesFollowUpsIntoIt()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(request =>
            {
                var body = request.RequestUri!.ToString().Contains("/threads", StringComparison.Ordinal)
                    ? ThreadJson()
                    : MessageJson();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            });
            var threads = new DiscordThreadStore();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), threads);

            var notification = MakeNotification(
                conditionId: "operator_question",
                actions: [Action("work-1", "q-001")],
                correlationToken: "work-1:q-001");
            await provider.SendAsync(notification, CancellationToken.None);
            await provider.SendAsync(notification, CancellationToken.None);

            Assert.Equal(3, handler.Requests.Count);
            Assert.EndsWith($"/channels/{Channel}/messages", handler.Requests[0].Url);
            Assert.Contains("/threads", handler.Requests[1].Url);
            var threadBody = BodyJson(handler.Requests[1]);
            Assert.NotEmpty(threadBody.GetProperty("name").GetString() ?? string.Empty);
            Assert.EndsWith("/channels/1500000000000000001/messages", handler.Requests[2].Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_EditsOriginalMessageAndRemovesButtons()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(request =>
            {
                var body = request.RequestUri!.ToString().Contains("/threads", StringComparison.Ordinal)
                    ? ThreadJson()
                    : MessageJson(id: "1700000000000000001");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            });
            var threads = new DiscordThreadStore();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), threads);

            var notification = MakeNotification(
                conditionId: "operator_question",
                actions: [Action("work-1", "q-001")],
                correlationToken: "work-1:q-001");
            await provider.SendAsync(notification, CancellationToken.None);
            handler.Requests.Clear();

            await provider.UpdateDecisionAsync(
                notification, "Decided: Use rollbacks — by discord:U1 (alice)", CancellationToken.None);

            var edit = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Patch, edit.Method);
            Assert.EndsWith("/channels/1100000000000000001/messages/1700000000000000001", edit.Url);
            var root = BodyJson(edit);
            Assert.Contains("Use rollbacks", root.GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Empty(root.GetProperty("components").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task DeliveryFailure_NeverEscapes()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message": "Missing Access"}""") });
            var threads = new DiscordThreadStore();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), threads);

            var sendNotification = MakeNotification();
            await provider.SendAsync(sendNotification, CancellationToken.None);

            var send = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, send.Method);
            Assert.EndsWith($"/channels/{Channel}/messages", send.Url);

            const string correlationToken = "work-1:q-001";
            threads.RememberMessage(correlationToken, Channel, "1700000000000000001");

            await provider.UpdateDecisionAsync(
                MakeNotification(correlationToken: correlationToken),
                "Decided: x", CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            var edit = handler.Requests[1];
            Assert.Equal(HttpMethod.Patch, edit.Method);
            Assert.EndsWith($"/channels/{Channel}/messages/1700000000000000001", edit.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RecipientChannel_WinsOverDefault()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageJson(channel: "2200000000000000002")) });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(recipients: ["2200000000000000002"]), CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.EndsWith("/channels/2200000000000000002/messages", request.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public void Capability_DeclaresInteractions()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var provider = BuildProvider(EnabledConfig(), new HttpClient(new CapturingHttpHandler()));

            Assert.Equal("discord", provider.Name);
            Assert.True(provider.SupportsInteractions);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }
}
