using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.NtfyPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the ntfy notification plugin: the publish payload
/// carries severity/summary/fields natively, offered actions become signed
/// <c>http</c> buttons bound to the question, landed decisions republish over
/// the same <c>sequence_id</c>, and delivery failures never escape. Each test
/// drives the real provider against a captured HTTP transport and asserts on
/// the bytes the plugin itself posted.
/// </summary>
public sealed class NtfyNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_NTFY_TOKEN";
    private const string SecretEnvVar = "CODEYBOX_TEST_NTFY_INTERACTION_SECRET";
    private const string Secret = "test-ntfy-interaction-secret";
    private const string Token = "tk_testtokenvalue";
    private const string Topic = "codeybox-test-topic";
    private const string BaseUrl = "https://ntfy.example.invalid";
    private const string PublicBaseUrl = "https://codeybox.example.invalid";

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
        values["CodeyBox:Plugins:codeybox.ntfy:TokenEnvVar"] = TokenEnvVar;
        values["CodeyBox:Plugins:codeybox.ntfy:InteractionSecretEnvVar"] = SecretEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string? defaultTopic = Topic,
        string agnesBaseUrl = "https://agnes.example.invalid",
        string actionsMode = "Buttons",
        string publicBaseUrl = PublicBaseUrl)
        => Config(new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.ntfy:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.ntfy:BaseUrl"] = BaseUrl,
            ["CodeyBox:Plugins:codeybox.ntfy:DefaultTopic"] = defaultTopic,
            ["CodeyBox:Plugins:codeybox.ntfy:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.ntfy:ActionsMode"] = actionsMode,
            ["CodeyBox:Plugins:codeybox.ntfy:PublicBaseUrl"] = publicBaseUrl,
        });

    private static NtfyNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<NtfyNotificationProvider>? logger = null,
        NtfyMessageStore? messages = null)
        => new(config, http, logger ?? new CapturingLogger<NtfyNotificationProvider>(), messages: messages);

    private static JsonElement Published(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task Disabled_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(
                Config(new Dictionary<string, string?>
                {
                    ["CodeyBox:Plugins:codeybox.ntfy:Enabled"] = "false",
                    ["CodeyBox:Plugins:codeybox.ntfy:DefaultTopic"] = Topic,
                }),
                new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task NoTopic_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler();
            var logger = new CapturingLogger<NtfyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(defaultTopic: ""), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no topic"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task Outbound_RendersSeveritySummaryAndFields()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"x","event":"message"}""") });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "All quotas exhausted",
                body: "Spend is blocked.",
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.Equal($"{BaseUrl}/", req.Url);
            Assert.Equal($"Bearer {Token}", req.Authorization);

            var root = Published(req.Body);
            Assert.Equal(Topic, root.GetProperty("topic").GetString());
            Assert.Equal("All quotas exhausted", root.GetProperty("title").GetString());
            Assert.Equal(5, root.GetProperty("priority").GetInt32());
            Assert.Equal("rotating_light", root.GetProperty("tags")[0].GetString());
            var message = root.GetProperty("message").GetString()!;
            Assert.Contains("Spend is blocked.", message);
            Assert.Contains("agent: claude", message);
            Assert.Contains("queue_empty", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task Actions_BecomeSignedHttpButtons_WithQuestionBinding()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            var notification = MakeNotification(
                actions: [Action("work-1", "q-001", "Use rollbacks"), Action("work-1", "q-001", "Stay the course")],
                answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
                correlationToken: "work-1:q-001");
            await provider.SendAsync(notification, CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            var root = Published(req.Body);

            // The question notification is sequence-pinned so the decision can
            // replace it in place.
            Assert.Equal(NtfyMessageBuilder.SequenceIdFor("work-1:q-001"),
                root.GetProperty("sequence_id").GetString());
            Assert.Equal("https://codeybox.example.invalid/workitems/work-1/questions",
                root.GetProperty("click").GetString());

            var actions = root.GetProperty("actions").EnumerateArray().ToList();
            var http = actions.Where(a => a.GetProperty("action").GetString() == "http").ToList();
            Assert.Equal(2, http.Count);
            foreach (var action in http)
            {
                Assert.Equal($"{PublicBaseUrl}/webhooks/interactions/ntfy",
                    action.GetProperty("url").GetString());
                Assert.Equal("POST", action.GetProperty("method").GetString());
                Assert.True(action.GetProperty("clear").GetBoolean());

                var body = action.GetProperty("body").GetString()!;
                var signature = action.GetProperty("headers")
                    .GetProperty(NtfyMessageBuilder.SignatureHeader).GetString()!;

                // The MAC is over the exact callback bytes — recompute it.
                Assert.Equal(NtfyMessageBuilder.SignatureFor(Secret, body), signature);
                Assert.StartsWith("sha256=", signature);

                using var interaction = JsonDocument.Parse(body);
                var ir = interaction.RootElement;
                Assert.Equal("work-1", ir.GetProperty("workItemId").GetString());
                Assert.Equal("q-001", ir.GetProperty("questionId").GetString());
                Assert.Equal("work-1:q-001", ir.GetProperty("correlationToken").GetString());
                Assert.Equal(Topic, ir.GetProperty("channelId").GetString());
                Assert.Equal("subscriber", ir.GetProperty("user").GetProperty("userId").GetString());
                Assert.StartsWith("ntfy:", ir.GetProperty("interactionId").GetString());
            }
            var answers = http.Select(a =>
                JsonDocument.Parse(a.GetProperty("body").GetString()!).RootElement
                    .GetProperty("answer").GetString()).ToList();
            Assert.Equal(["Use rollbacks", "Stay the course"], answers);

            // The answer link also fills a remaining slot as a view action.
            Assert.Contains(actions, a =>
                a.GetProperty("action").GetString() == "view"
                && a.GetProperty("url").GetString() == notification.AnswerUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task Actions_CappedAtNtfyLimit()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            var five = Enumerable.Range(1, 5)
                .Select(i => Action("work-1", "q-001", $"Option {i}"))
                .ToList();
            await provider.SendAsync(MakeNotification(
                actions: five, correlationToken: "work-1:q-001"), CancellationToken.None);

            var root = Published(Assert.Single(handler.Requests).Body);
            var actions = root.GetProperty("actions").EnumerateArray().ToList();
            Assert.Equal(3, actions.Count(a => a.GetProperty("action").GetString() == "http"));
            Assert.True(actions.Count <= 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task ButtonsDegrade_WhenSecretMissing_AndStayAnswerable()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger<NtfyNotificationProvider>();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

        await provider.SendAsync(MakeNotification(
            actions: [Action("work-1", "q-001")],
            answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
            correlationToken: "work-1:q-001"),
            CancellationToken.None);

        var root = Published(Assert.Single(handler.Requests).Body);
        var actions = root.GetProperty("actions").EnumerateArray().ToList();
        Assert.DoesNotContain(actions, a => a.GetProperty("action").GetString() == "http");
        Assert.Contains(actions, a =>
            a.GetProperty("action").GetString() == "view"
            && a.GetProperty("url").GetString() == "https://codeybox.example.invalid/workitems/work-1/questions");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("cannot be rendered"));
    }

    [Fact]
    public async Task LinksMode_RendersViewActionsOnly()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(actionsMode: "Links"), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                actions: [Action("work-1", "q-001")],
                answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions",
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var root = Published(Assert.Single(handler.Requests).Body);
            var actions = root.GetProperty("actions").EnumerateArray().ToList();
            Assert.DoesNotContain(actions, a => a.GetProperty("action").GetString() == "http");
            Assert.Contains(actions, a =>
                a.GetProperty("action").GetString() == "view"
                && a.GetProperty("label").GetString() == "Answer here");
            Assert.Contains(actions, a =>
                a.GetProperty("action").GetString() == "view"
                && a.GetProperty("url").GetString() == "https://agnes.example.invalid/workitems/work-1");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task PublishError_LogsWarning_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"code":40101,"http":401,"error":"unauthorized"}"""),
                });
            var logger = new CapturingLogger<NtfyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("unauthorized"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task PublishError_ServerReason_IsFlattenedToOneLine()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    // The server's error text is untrusted: embedded newlines
                    // must not smuggle forged lines into operator logs.
                    Content = new StringContent("""{"code":40303,"http":403,"error":"denied\nforged-line"}"""),
                });
            var logger = new CapturingLogger<NtfyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            var entry = Assert.Single(logger.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains("denied"));
            Assert.DoesNotContain('\n', entry.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task PlainHttpBaseUrl_IsRefusedBeforePublish()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler();
            var logger = new CapturingLogger<NtfyNotificationProvider>();
            // Plain HTTP beyond loopback would carry the bearer token and the
            // signed button bodies in cleartext, so it is refused outright.
            var provider = BuildProvider(
                Config(new Dictionary<string, string?>
                {
                    ["CodeyBox:Plugins:codeybox.ntfy:Enabled"] = "true",
                    ["CodeyBox:Plugins:codeybox.ntfy:BaseUrl"] = "http://ntfy.example.invalid",
                    ["CodeyBox:Plugins:codeybox.ntfy:DefaultTopic"] = Topic,
                }),
                new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("BaseUrl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task TransportException_LogsError_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => throw new HttpRequestException("simulated outage"));
            var logger = new CapturingLogger<NtfyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
                && e.Message.Contains("delivery failed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task OperationCancelled_Rethrows()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => throw new OperationCanceledException("shutdown"));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => provider.SendAsync(MakeNotification(), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_RepublishesOverSameSequenceId()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "Input needed",
                actions: [Action("work-3", "q-002")],
                correlationToken: "work-3:q-002"), CancellationToken.None);
            await provider.UpdateDecisionAsync(
                MakeNotification(title: "Input needed", correlationToken: "work-3:q-002"),
                "Decided: Use rollbacks — by ntfy:subscriber",
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            var update = Published(handler.Requests[1].Body);
            Assert.Equal(Topic, update.GetProperty("topic").GetString());
            Assert.Equal("codeybox-work-3-q-002", update.GetProperty("sequence_id").GetString());
            Assert.Equal("Decided: Use rollbacks — by ntfy:subscriber", update.GetProperty("message").GetString());
            Assert.Equal("white_check_mark", update.GetProperty("tags")[0].GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_UnknownToken_FallsBackToDefaultTopic()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.UpdateDecisionAsync(
                MakeNotification(correlationToken: "work-9:q-009"),
                "Decided: X — by ntfy:subscriber",
                CancellationToken.None);

            var update = Published(Assert.Single(handler.Requests).Body);
            Assert.Equal(Topic, update.GetProperty("topic").GetString());
            Assert.Equal("codeybox-work-9-q-009", update.GetProperty("sequence_id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task DecisionUpdate_NoTokenAndNoDefault_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(EnabledConfig(defaultTopic: ""), new HttpClient(handler));

            await provider.UpdateDecisionAsync(
                MakeNotification(correlationToken: "work-9:q-009"),
                "Decided: X — by ntfy:subscriber",
                CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public void Capability_DeclaresInteractiveSupport()
    {
        var provider = BuildProvider(
            Config(new Dictionary<string, string?>()),
            new HttpClient(new CapturingHttpHandler()));

        Assert.Equal("ntfy", provider.Name);
        Assert.True(provider.SupportsInteractions);
    }

    [Fact]
    public void CallbackUrl_RequiresHttpsOrLoopback()
    {
        Assert.Equal("https://host.example/webhooks/interactions/ntfy",
            NtfyMessageBuilder.CallbackUrl("https://host.example"));
        Assert.Equal("http://localhost:5000/webhooks/interactions/ntfy",
            NtfyMessageBuilder.CallbackUrl("http://localhost:5000"));
        Assert.Null(NtfyMessageBuilder.CallbackUrl("http://host.example"));
        Assert.Null(NtfyMessageBuilder.CallbackUrl("https://user:pw@host.example"));
        Assert.Null(NtfyMessageBuilder.CallbackUrl(""));
        Assert.Null(NtfyMessageBuilder.CallbackUrl("not a url"));
    }

    [Fact]
    public void CallbackUrl_RejectsPathQueryAndFragment()
    {
        // The host's PublicBaseUrl policy rejects these outright; silently
        // stripping a configured path would mint buttons pointing at a URL
        // that does not exist.
        Assert.Null(NtfyMessageBuilder.CallbackUrl("https://host.example/base"));
        Assert.Null(NtfyMessageBuilder.CallbackUrl("https://host.example/?x=1"));
        Assert.Null(NtfyMessageBuilder.CallbackUrl("https://host.example/#frag"));
    }

    [Fact]
    public void AgnesWorkItemUrl_RequiresWebScheme()
    {
        var opts = new NtfyPluginOptions { AgnesBaseUrl = "https://agnes.example.invalid" };
        Assert.Equal("https://agnes.example.invalid/workitems/work-1",
            NtfyMessageBuilder.AgnesWorkItemUrl(opts, "work-1"));

        // Non-web schemes must not land in a rendered action URL.
        Assert.Null(NtfyMessageBuilder.AgnesWorkItemUrl(
            new NtfyPluginOptions { AgnesBaseUrl = "file:///etc/passwd" }, "work-1"));
        Assert.Null(NtfyMessageBuilder.AgnesWorkItemUrl(
            new NtfyPluginOptions { AgnesBaseUrl = "javascript:alert(1)" }, "work-1"));
    }

    [Fact]
    public void TruncateUtf8_BoundsBytesWithoutSplittingCodePoints()
    {
        Assert.Equal("abc", NtfyMessageBuilder.TruncateUtf8("abc", 10));
        Assert.Equal(string.Empty, NtfyMessageBuilder.TruncateUtf8("abc", 0));

        // Budget smaller than the marker cuts to a bare prefix: four ASCII
        // bytes fit, the 3-byte CJK char does not and is never split.
        Assert.Equal("aaaa", NtfyMessageBuilder.TruncateUtf8("aaaa你你", 6));

        // Same for an astral character (4 UTF-8 bytes / one surrogate pair).
        Assert.Equal("a", NtfyMessageBuilder.TruncateUtf8("a🙂b", 3));

        var withMarker = NtfyMessageBuilder.TruncateUtf8(new string('x', 100), 30);
        Assert.EndsWith("… (truncated)", withMarker);
        Assert.True(Encoding.UTF8.GetByteCount(withMarker) <= 30);
    }

    [Fact]
    public async Task Message_TruncatesOnUtf8ByteBudget()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            // 2 000 CJK chars = 6 000 UTF-8 bytes: within a naive char budget
            // of 3 800 but over ntfy's byte ceiling — the message must come
            // back under the byte bound.
            await provider.SendAsync(
                MakeNotification(body: new string('你', 2000)), CancellationToken.None);

            var message = Published(Assert.Single(handler.Requests).Body)
                .GetProperty("message").GetString()!;
            Assert.True(Encoding.UTF8.GetByteCount(message) <= 3800);
            Assert.Contains("… (truncated)", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public void SequenceId_SlugifiesAndStaysBounded()
    {
        Assert.Equal("codeybox-work-1-q-001", NtfyMessageBuilder.SequenceIdFor("work-1:q-001"));
        Assert.Null(NtfyMessageBuilder.SequenceIdFor(null));
        Assert.Null(NtfyMessageBuilder.SequenceIdFor("  "));

        var longToken = new string('w', 200) + ":q-001";
        var id = NtfyMessageBuilder.SequenceIdFor(longToken)!;
        Assert.True(id.Length <= 96);
        // The hash suffix keeps truncated ids unique.
        var otherId = NtfyMessageBuilder.SequenceIdFor(new string('w', 200) + ":q-002")!;
        Assert.NotEqual(id, otherId);
    }
}
