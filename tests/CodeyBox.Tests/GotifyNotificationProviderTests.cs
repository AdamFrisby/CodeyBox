using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.GotifyPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Gotify notification plugin: the POST /message
/// body carries severity (as priority + title marker), summary and fields
/// rendered as markdown, and the AnswerUrl route surfaces both as an
/// in-body link and as the notification's click target — Gotify cannot
/// carry an authenticated interaction, so the provider declares
/// notification-only and never synthesizes buttons. Each test drives the
/// real provider against a captured HTTP transport and asserts on the
/// bytes the plugin itself posted.
/// </summary>
public sealed class GotifyNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_GOTIFY_APP_TOKEN";
    private const string Token = "A-test-app-token-value";
    private const string Server = "https://gotify.example.invalid";

    /// <summary>Recorded Gotify POST /message success envelope.</summary>
    private const string GotifyCreatedJson =
        """{"id":42,"appid":7,"message":"body","title":"title","priority":5,"date":"2026-09-18T12:00:00.000000000Z"}""";

    /// <summary>Recorded Gotify error envelope (401 for a bad app token).</summary>
    private const string GotifyUnauthorizedJson =
        """{"errorCode":401,"error":"Unauthorized","errorDescription":"you need to provide a valid access token or user credentials to access this api"}""";

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
        values["CodeyBox:Plugins:codeybox.gotify:AppTokenEnvVar"] = TokenEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string serverUrl = Server,
        string agnesBaseUrl = "https://agnes.example.invalid",
        bool allowPlainHttp = false,
        bool markdown = true)
        => Config(new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.gotify:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.gotify:ServerUrl"] = serverUrl,
            ["CodeyBox:Plugins:codeybox.gotify:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.gotify:AllowPlainHttp"] = allowPlainHttp ? "true" : "false",
            ["CodeyBox:Plugins:codeybox.gotify:Markdown"] = markdown ? "true" : "false",
        });

    private static GotifyNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<GotifyNotificationProvider>? logger = null)
        => new(config, http, logger ?? new CapturingLogger<GotifyNotificationProvider>());

    [Fact]
    public async Task Disabled_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(
                Config(new Dictionary<string, string?>
                {
                    ["CodeyBox:Plugins:codeybox.gotify:Enabled"] = "false",
                    ["CodeyBox:Plugins:codeybox.gotify:ServerUrl"] = Server,
                }),
                new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MissingToken_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, null);
        var handler = new CapturingHttpHandler();
        var logger = new CapturingLogger<GotifyNotificationProvider>();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not set"));
    }

    [Fact]
    public async Task MissingServerUrl_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var logger = new CapturingLogger<GotifyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(serverUrl: ""), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ServerUrl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task PlainHttp_Refused_UnlessOptedIn()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            // The app token travels in a request header: plain http would
            // expose it, so the provider refuses unless the operator opts in.
            var refusedHandler = new CapturingHttpHandler();
            var refusedLogger = new CapturingLogger<GotifyNotificationProvider>();
            var refused = BuildProvider(
                EnabledConfig(serverUrl: "http://gotify.internal:8080", allowPlainHttp: false),
                new HttpClient(refusedHandler), refusedLogger);

            await refused.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(refusedHandler.Requests);
            Assert.Contains(refusedLogger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("plain http"));

            var allowedHandler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var allowed = BuildProvider(
                EnabledConfig(serverUrl: "http://gotify.internal:8080", allowPlainHttp: true),
                new HttpClient(allowedHandler));

            await allowed.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(allowedHandler.Requests);
            Assert.Equal("http://gotify.internal:8080/message", allowedHandler.Requests[0].Url);
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
            string? gotifyKey = null;
            var handler = new CapturingHttpHandler(req =>
            {
                gotifyKey = req.Headers.TryGetValues("X-Gotify-Key", out var values)
                    ? string.Join(";", values)
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                };
            });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "All quotas exhausted",
                body: "Spend is blocked.",
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.Equal($"{Server}/message", req.Url);
            Assert.DoesNotContain("token=", req.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Token, gotifyKey);

            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            Assert.Equal("🚨 All quotas exhausted", root.GetProperty("title").GetString());
            Assert.Equal(8, root.GetProperty("priority").GetInt32());

            var message = root.GetProperty("message").GetString()!;
            Assert.Contains("Spend is blocked.", message);
            Assert.Contains("- **agent**: claude", message);
            Assert.Contains("queue\\_empty", message);

            var display = root.GetProperty("extras").GetProperty("client::display");
            Assert.Equal("text/markdown", display.GetProperty("contentType").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Question_SurfacesAnswerRoute_AsLinkAndClickTarget()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            var answerUrl = "https://codeybox.example.invalid/workitems/work-1/questions";
            await provider.SendAsync(MakeNotification(
                title: "Input needed: q-001",
                actions: [Action("work-1", "q-001", "Use rollbacks"), Action("work-1", "q-001", "Stay the course")],
                answerUrl: answerUrl,
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            var message = root.GetProperty("message").GetString()!;

            // Notification-only by honest declaration: the offered actions
            // cannot become authenticated buttons, so the message carries
            // the answer route — never an unanswerable prompt.
            Assert.False(provider.SupportsInteractions);
            Assert.Contains($"[Answer in CodeyBox]({answerUrl})", message);
            Assert.Contains("[Open in Agnes](https://agnes.example.invalid/workitems/work-1)", message);
            Assert.DoesNotContain("codeybox_answer", message);

            var click = root.GetProperty("extras").GetProperty("client::notification").GetProperty("click");
            Assert.Equal(answerUrl, click.GetProperty("url").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task PlainTextMode_RendersRawAnswerUrl()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var provider = BuildProvider(EnabledConfig(markdown: false), new HttpClient(handler));

            var answerUrl = "https://codeybox.example.invalid/workitems/work-1/questions";
            await provider.SendAsync(MakeNotification(
                answerUrl: answerUrl,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            var message = root.GetProperty("message").GetString()!;

            Assert.Contains($"Answer here: {answerUrl}", message);
            Assert.Contains("agent: claude", message);
            Assert.Equal("text/plain",
                root.GetProperty("extras").GetProperty("client::display").GetProperty("contentType").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Payload_MatchesRecordedGotifyMessageShape()
    {
        // Recorded-shape fixture standing in for a live-server test: a real
        // Gotify instance accepts exactly {title, message, priority,
        // extras{client::display.contentType, client::notification.click.url}}
        // on POST /message with the X-Gotify-Key header. Assert the wire
        // keys verbatim so a rendering refactor cannot silently drift the
        // contract.
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                answerUrl: "https://codeybox.example.invalid/workitems/work-9/questions",
                correlationToken: "work-9:q-009"),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;

            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            var topKeys = root.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(["extras", "message", "priority", "title"], topKeys);

            Assert.Equal(JsonValueKind.String, root.GetProperty("title").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("message").ValueKind);
            Assert.Equal(JsonValueKind.Number, root.GetProperty("priority").ValueKind);

            var extras = root.GetProperty("extras");
            Assert.Equal("text/markdown",
                extras.GetProperty("client::display").GetProperty("contentType").GetString());
            Assert.Equal(
                "https://codeybox.example.invalid/workitems/work-9/questions",
                extras.GetProperty("client::notification").GetProperty("click").GetProperty("url").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task UntrustedContent_IsMarkdownEscaped()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                body: "run `rm -rf /` then click [x](https://evil.invalid)",
                fields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["detail"] = "see [this](https://evil.invalid) *now*",
                }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(req.Body);
            var message = doc.RootElement.GetProperty("message").GetString()!;

            Assert.DoesNotContain("[x](https://evil.invalid)", message);
            Assert.DoesNotContain("[this](https://evil.invalid)", message);
            Assert.Contains("\\[this\\](https://evil.invalid)", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task SeverityPriority_IsConfigurable_AndClamped()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var config = Config(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:codeybox.gotify:Enabled"] = "true",
                ["CodeyBox:Plugins:codeybox.gotify:ServerUrl"] = Server,
                ["CodeyBox:Plugins:codeybox.gotify:CriticalPriority"] = "99",
                ["CodeyBox:Plugins:codeybox.gotify:WarningPriority"] = "6",
            });
            var provider = BuildProvider(config, new HttpClient(handler));

            await provider.SendAsync(MakeNotification(severity: NotificationSeverity.Critical), CancellationToken.None);
            await provider.SendAsync(MakeNotification(severity: NotificationSeverity.Warning), CancellationToken.None);

            using var first = JsonDocument.Parse(handler.Requests[0].Body);
            Assert.Equal(10, first.RootElement.GetProperty("priority").GetInt32());
            using var second = JsonDocument.Parse(handler.Requests[1].Body);
            Assert.Equal(6, second.RootElement.GetProperty("priority").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task ServerPathPrefix_IsHonoured()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GotifyCreatedJson),
                });
            var provider = BuildProvider(
                EnabledConfig(serverUrl: "https://host.example.invalid/gotify/"),
                new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal("https://host.example.invalid/gotify/message", Assert.Single(handler.Requests).Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task GotifyError_LogsWarning_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(GotifyUnauthorizedJson),
                });
            var logger = new CapturingLogger<GotifyNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("Unauthorized"));
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
            var logger = new CapturingLogger<GotifyNotificationProvider>();
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
    public void Capability_DeclaresNotificationOnly()
    {
        var provider = BuildProvider(
            Config(new Dictionary<string, string?>()),
            new HttpClient(new CapturingHttpHandler()));

        Assert.Equal("gotify", provider.Name);
        Assert.False(provider.SupportsInteractions);
    }
}
