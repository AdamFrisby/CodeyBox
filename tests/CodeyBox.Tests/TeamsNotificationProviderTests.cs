using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.TeamsPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Teams notification plugin: the posted activity
/// carries severity/summary/fields as a native Adaptive Card, offered
/// actions become submit controls bound to the question (with a custom-answer
/// follow-up for multi-step flows), landed decisions refresh the original
/// card, and delivery failures never escape. Each test drives the real
/// provider against a captured HTTP transport and asserts on the bytes the
/// plugin itself posted.
/// </summary>
public sealed class TeamsNotificationProviderTests
{
    private const string AppIdEnvVar = "CODEYBOX_TEST_TEAMS_APP_ID";
    private const string AppPasswordEnvVar = "CODEYBOX_TEST_TEAMS_APP_PASSWORD";
    private const string AppId = "test-teams-app-id";
    private const string AppPassword = "test-teams-app-password";
    private const string ServiceUrl = "https://smba.trafficmanager.net/teams/";
    private const string Conversation = "19:channel-id@thread.tacv2";

    private static Notification MakeNotification(
        string conditionId = "queue_empty",
        string title = "Queue is empty",
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
        values["CodeyBox:Plugins:codeybox.teams:AppIdEnvVar"] = AppIdEnvVar;
        values["CodeyBox:Plugins:codeybox.teams:AppPasswordEnvVar"] = AppPasswordEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string? serviceUrl = ServiceUrl,
        string? conversationId = Conversation,
        string agnesBaseUrl = "https://agnes.example.invalid",
        string actionsMode = "Buttons")
        => Config(new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.teams:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.teams:ServiceUrl"] = serviceUrl,
            ["CodeyBox:Plugins:codeybox.teams:ConversationId"] = conversationId,
            ["CodeyBox:Plugins:codeybox.teams:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.teams:ActionsMode"] = actionsMode,
        });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static CapturingHttpHandler ConnectorHandler(string activityId = "activity-1")
        => new(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"access_token":"test-bot-token","expires_in":3600,"token_type":"Bearer"}""");
            if (request.Method == HttpMethod.Put)
                return JsonResponse(HttpStatusCode.OK, """{"id":"activity-1"}""");
            return JsonResponse(HttpStatusCode.OK, $$"""{"id":"{{activityId}}"}""");
        });

    private static TeamsNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<TeamsNotificationProvider>? logger = null,
        TeamsMessageStore? messages = null,
        TeamsBotTokenCache? tokens = null)
        => new(config, http, logger ?? new CapturingLogger<TeamsNotificationProvider>(), clock: null, messages: messages, tokens: tokens);

    private static void SetCredentials()
    {
        Environment.SetEnvironmentVariable(AppIdEnvVar, AppId);
        Environment.SetEnvironmentVariable(AppPasswordEnvVar, AppPassword);
    }

    private static void ClearCredentials()
    {
        Environment.SetEnvironmentVariable(AppIdEnvVar, null);
        Environment.SetEnvironmentVariable(AppPasswordEnvVar, null);
    }

    [Fact]
    public async Task Disabled_MakesNoHttpCall()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(Config(new Dictionary<string, string?>()), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task MissingCredentials_MakesNoHttpCall()
    {
        ClearCredentials();
        var handler = ConnectorHandler();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidServiceUrl_MakesNoHttpCall()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(serviceUrl: "http://intranet.local/teams/"), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task Send_PostsAdaptiveCardWithSeverityFieldsAndActions()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));
            var notification = MakeNotification(
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string> { ["Work item"] = "work-1", ["Question"] = "q-001" },
                actions: new List<NotificationAction> { Action(), Action(questionId: "q-001", label: "Stay the course") },
                correlationToken: "work-1:q-001");

            await provider.SendAsync(notification, CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            var tokenRequest = handler.Requests[0];
            Assert.StartsWith("https://login.microsoftonline.com/", tokenRequest.Url, StringComparison.Ordinal);

            var post = handler.Requests[1];
            Assert.Equal(HttpMethod.Post, post.Method);
            Assert.Equal($"{ServiceUrl}v3/conversations/{Uri.EscapeDataString(Conversation)}/activities", post.Url);
            Assert.Equal("Bearer test-bot-token", post.Authorization);

            using var doc = JsonDocument.Parse(post.Body);
            var root = doc.RootElement;
            Assert.Equal("message", root.GetProperty("type").GetString());
            var attachment = root.GetProperty("attachments")[0];
            Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
            var card = attachment.GetProperty("content");
            Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());

            var cardText = card.GetRawText();
            Assert.Contains("Queue is empty", cardText, StringComparison.Ordinal);
            Assert.Contains("All work items processed.", cardText, StringComparison.Ordinal);
            Assert.Contains("attention", cardText, StringComparison.Ordinal);
            Assert.Contains("Work item", cardText, StringComparison.Ordinal);
            Assert.Contains("work-1", cardText, StringComparison.Ordinal);

            var actions = card.GetProperty("actions");
            var submits = actions.EnumerateArray()
                .Where(a => a.GetProperty("type").GetString() == "Action.Submit")
                .ToList();
            Assert.Equal(2, submits.Count);
            var data = submits[0].GetProperty("data");
            Assert.Equal("work-1", data.GetProperty("w").GetString());
            Assert.Equal("q-001", data.GetProperty("q").GetString());
            Assert.Equal("Use rollbacks", data.GetProperty("a").GetString());
            Assert.Equal("work-1:q-001", data.GetProperty("c").GetString());

            // The multi-step follow-up prompt renders alongside the buttons.
            Assert.Contains(actions.EnumerateArray(),
                a => a.GetProperty("type").GetString() == "Action.ShowCard");
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task Send_UsesFirstRecipientConversation()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(recipients: new List<string> { "19:other@thread.tacv2" }),
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("/v3/conversations/19%3Aother%40thread.tacv2/activities", handler.Requests[1].Url, StringComparison.Ordinal);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task Send_LinksMode_RendersNoSubmitControls()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(actionsMode: "Links"), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(
                    actions: new List<NotificationAction> { Action() },
                    answerUrl: "https://codeybox.example.invalid/questions/work-1"),
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            using var doc = JsonDocument.Parse(handler.Requests[1].Body);
            var actions = doc.RootElement
                .GetProperty("attachments")[0]
                .GetProperty("content")
                .GetProperty("actions");
            Assert.DoesNotContain(actions.EnumerateArray(),
                a => a.GetProperty("type").GetString() == "Action.Submit");
            Assert.Contains(actions.EnumerateArray(),
                a => a.GetProperty("type").GetString() == "Action.OpenUrl"
                    && a.GetProperty("title").GetString() == "Answer here");
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task Send_OversizedBinding_DegradesToLinks()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));
            var huge = new string('x', TeamsAdaptiveCard.MaxSubmitDataChars + 1);

            await provider.SendAsync(
                MakeNotification(
                    actions: new List<NotificationAction> { Action(label: huge) },
                    answerUrl: "https://codeybox.example.invalid/questions/work-1",
                    correlationToken: "work-1:q-001"),
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            using var doc = JsonDocument.Parse(handler.Requests[1].Body);
            var actions = doc.RootElement
                .GetProperty("attachments")[0]
                .GetProperty("content")
                .GetProperty("actions");
            // The unresolvable button is never posted; the question stays
            // answerable through the answer link.
            Assert.DoesNotContain(actions.EnumerateArray(),
                a => a.GetProperty("type").GetString() == "Action.Submit");
            Assert.Contains(actions.EnumerateArray(),
                a => a.GetProperty("type").GetString() == "Action.OpenUrl");
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task Send_DeliveryFailure_NeverEscapes()
    {
        SetCredentials();
        try
        {
            var handler = new CapturingHttpHandler(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal))
                    return JsonResponse(HttpStatusCode.OK, """{"access_token":"test-bot-token","expires_in":3600}""");
                return JsonResponse(HttpStatusCode.InternalServerError, """{}""");
            });
            var logger = new CapturingLogger<TeamsNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task TokenCache_ReusedAcrossSends()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);
            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests, r => r.Url.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal));
            Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Url.Contains("/activities", StringComparison.Ordinal)));
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task UpdateDecision_RefreshesOriginalCard()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var messages = new TeamsMessageStore();
            messages.RememberMessage("work-1:q-001", Conversation, "activity-9");
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), messages: messages);
            var notification = MakeNotification(title: "Which approach?", correlationToken: "work-1:q-001");

            await provider.UpdateDecisionAsync(notification, "Decided: Use rollbacks — by teams:alice (Alice)", CancellationToken.None);

            var update = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
            Assert.Equal($"{ServiceUrl}v3/conversations/{Uri.EscapeDataString(Conversation)}/activities/activity-9", update.Url);
            Assert.Equal("Bearer test-bot-token", update.Authorization);
            Assert.Contains("Decided: Use rollbacks", update.Body, StringComparison.Ordinal);
            Assert.Contains("Which approach?", update.Body, StringComparison.Ordinal);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public async Task UpdateDecision_UnknownToken_MakesNoHttpCall()
    {
        SetCredentials();
        try
        {
            var handler = ConnectorHandler();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.UpdateDecisionAsync(
                MakeNotification(correlationToken: "work-1:q-001"),
                "Decided: something",
                CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            ClearCredentials();
        }
    }

    [Fact]
    public void Capability_DeclaresInteractions()
    {
        var provider = BuildProvider(Config(new Dictionary<string, string?>()), new HttpClient(new CapturingHttpHandler()));
        Assert.True(provider.SupportsInteractions);
        Assert.Equal("teams", provider.Name);
    }
}
