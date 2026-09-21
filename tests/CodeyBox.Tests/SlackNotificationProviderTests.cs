using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.SlackPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Slack notification plugin: Block Kit rendering
/// carries severity/summary/fields, offered actions become native buttons
/// bound to the question, follow-ups thread per work item, decisions update
/// the original message, and delivery failures never escape. Each test drives
/// the real provider against a captured HTTP transport and asserts on the
/// bytes the plugin itself posted.
/// </summary>
public sealed class SlackNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_SLACK_BOT_TOKEN";
    private const string Token = "xoxb-test-token-value";
    private const string Channel = "C999";

    private static Notification MakeNotification(
        string conditionId = "queue_empty",
        string title = "Queue is empty",
        string? body = "All work items processed.",
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyDictionary<string, string>? fields = null,
        IReadOnlyList<NotificationAction>? actions = null,
        string? answerUrl = null,
        string? correlationToken = null)
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
            Timestamp = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
        };

    private static NotificationAction Action(string workItemId = "work-1", string questionId = "q-001", string label = "Use rollbacks")
        => new() { Label = label, Value = label, WorkItemId = workItemId, QuestionId = questionId };

    private static IConfiguration Config(Dictionary<string, string?> values)
    {
        values["CodeyBox:Plugins:codeybox.slack:BotTokenEnvVar"] = TokenEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string? defaultChannel = Channel,
        string agnesBaseUrl = "https://agnes.example.invalid",
        string actionsMode = "Buttons")
        => Config(new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.slack:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.slack:DefaultChannel"] = defaultChannel,
            ["CodeyBox:Plugins:codeybox.slack:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.slack:ActionsMode"] = actionsMode,
        });

    private static string SlackOkJson(string channel = Channel, string ts = "1758640000.001200")
        => $"{{\"ok\":true,\"channel\":\"{channel}\",\"ts\":\"{ts}\"}}";

    private static SlackNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<SlackNotificationProvider>? logger = null,
        SlackThreadStore? threads = null)
        => new(config, http, logger ?? new CapturingLogger<SlackNotificationProvider>(), clock: null, threads: threads);

    [Fact]
    public async Task Disabled_MakesNoHttpCall()
    {
        var handler = new CapturingHttpHandler();
        var provider = BuildProvider(
            Config(new Dictionary<string, string?> { ["CodeyBox:Plugins:codeybox.slack:Enabled"] = "false" }),
            new HttpClient(handler));

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingToken_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, null);
        var handler = new CapturingHttpHandler();
        var logger = new CapturingLogger<SlackNotificationProvider>();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not set"));
    }

    [Fact]
    public async Task NoChannel_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var logger = new CapturingLogger<SlackNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(defaultChannel: ""), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no channel"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Outbound_RendersSeveritySummaryAndFields()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson()),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "All quotas exhausted",
                body: "Spend is blocked.",
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.Equal("https://slack.com/api/chat.postMessage", req.Url);
            Assert.Equal($"Bearer {Token}", req.Authorization);

            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            Assert.Equal(Channel, root.GetProperty("channel").GetString());
            Assert.Contains("Critical", root.GetProperty("text").GetString());

            var attachments = root.GetProperty("attachments");
            var attachment = Assert.Single(attachments.EnumerateArray());
            Assert.Equal("danger", attachment.GetProperty("color").GetString());

            var blocks = attachment.GetProperty("blocks");
            var dump = blocks.GetRawText();
            Assert.Contains("All quotas exhausted", dump);
            Assert.Contains("Spend is blocked.", dump);
            Assert.Contains("claude", dump);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Actions_BecomeButtons_WithQuestionBinding()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson()),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                actions: [Action("work-1", "q-001", "Use rollbacks"), Action("work-1", "q-001", "Stay the course")],
                answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(req.Body);
            var blocks = doc.RootElement.GetProperty("attachments")[0].GetProperty("blocks");
            var dump = blocks.GetRawText();

            Assert.Contains("codeybox_answer", dump);
            Assert.Contains("Use rollbacks", dump);
            Assert.Contains("Answer here", dump);
            Assert.Contains("https://agnes.example.invalid/workitems/work-1", dump);

            var values = blocks.EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "actions")
                .SelectMany(b => b.GetProperty("elements").EnumerateArray())
                .Where(e => e.TryGetProperty("action_id", out var id)
                    && id.GetString() == "codeybox_answer")
                .Select(e => e.GetProperty("value").GetString()!)
                .ToList();
            Assert.Equal(2, values.Count);
            foreach (var value in values)
            {
                Assert.True(SlackBlockKit.TryDecodeButtonValue(
                    value, out var w, out var q, out var a, out var c));
                Assert.Equal("work-1", w);
                Assert.Equal("q-001", q);
                Assert.Equal("work-1:q-001", c);
                Assert.NotEmpty(a);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task FollowUp_SameWorkItem_PostsToThread()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson(ts: "1758640000.001200")),
                });
            var threads = new SlackThreadStore();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), threads: threads);

            var actionable = MakeNotification(
                actions: [Action("work-7", "q-001")],
                correlationToken: "work-7:q-001");
            await provider.SendAsync(actionable, CancellationToken.None);
            await provider.SendAsync(MakeNotification(
                title: "Progress update",
                body: "Still working.",
                correlationToken: "work-7:q-001"), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            using var first = JsonDocument.Parse(handler.Requests[0].Body);
            Assert.False(first.RootElement.TryGetProperty("thread_ts", out _));
            using var second = JsonDocument.Parse(handler.Requests[1].Body);
            Assert.Equal("1758640000.001200", second.RootElement.GetProperty("thread_ts").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task FleetNotification_PostsTopLevel()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson()),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);
            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            foreach (var req in handler.Requests)
            {
                using var doc = JsonDocument.Parse(req.Body);
                Assert.False(doc.RootElement.TryGetProperty("thread_ts", out _));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task SlackError_LogsWarning_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":false,"error":"channel_not_found"}"""),
                });
            var logger = new CapturingLogger<SlackNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("channel_not_found"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task TransportException_LogsError_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => throw new HttpRequestException("simulated outage"));
            var logger = new CapturingLogger<SlackNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
                && e.Message.Contains("delivery failed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task OperationCancelled_Rethrows()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => throw new OperationCanceledException("shutdown"));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => provider.SendAsync(MakeNotification(), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_RewritesOriginalMessage()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = req.RequestUri!.AbsolutePath.EndsWith("chat.update", StringComparison.Ordinal)
                        ? new StringContent(SlackOkJson(ts: "1758640000.001200"))
                        : new StringContent(SlackOkJson(ts: "1758640000.001200")),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "Input needed",
                actions: [Action("work-3", "q-002")],
                correlationToken: "work-3:q-002"), CancellationToken.None);
            await provider.UpdateDecisionAsync(
                MakeNotification(title: "Input needed", correlationToken: "work-3:q-002"),
                "Decided: Use rollbacks — by slack:U123 (alice)",
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            var update = handler.Requests[1];
            Assert.Equal("https://slack.com/api/chat.update", update.Url);
            using var doc = JsonDocument.Parse(update.Body);
            Assert.Equal(Channel, doc.RootElement.GetProperty("channel").GetString());
            Assert.Equal("1758640000.001200", doc.RootElement.GetProperty("ts").GetString());
            Assert.Contains("Decided", doc.RootElement.GetRawText());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_UnknownMessage_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.UpdateDecisionAsync(
                MakeNotification(correlationToken: "work-9:q-009"),
                "Decided: X — by slack:U1",
                CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public void Capability_DeclaresInteractiveSupport()
    {
        var provider = BuildProvider(
            Config(new Dictionary<string, string?>()),
            new HttpClient(new CapturingHttpHandler()));

        Assert.Equal("slack", provider.Name);
        Assert.True(provider.SupportsInteractions);
    }

    [Fact]
    public async Task LinksMode_RendersNoAnswerButtons_StillAnswerable()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson()),
                });
            var provider = BuildProvider(EnabledConfig(actionsMode: "Links"), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                actions: [Action("work-1", "q-001")],
                answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.DoesNotContain("\"action_id\":\"codeybox_answer\"", req.Body);
            Assert.Contains("codeybox.example.invalid/workitems/work-1/questions", req.Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task OversizedAnswerValue_DegradesToLinks()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SlackOkJson()),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                actions: [Action("work-1", "q-001", new string('A', 1950))],
                answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.DoesNotContain("\"action_id\":\"codeybox_answer\"", req.Body);
            Assert.Contains("codeybox.example.invalid/workitems/work-1/questions", req.Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }
}
