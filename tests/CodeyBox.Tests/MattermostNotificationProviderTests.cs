using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.MattermostPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Mattermost notification plugin: markdown posts
/// carry severity/fields/answer links over REST v4, follow-ups thread per
/// work item via root_id, redeliveries dedup per correlation token,
/// ambiguous 2xx-without-id responses tombstone instead of duplicating,
/// 429s are honoured with a bounded retry, and delivery failures never
/// escape. Each test drives the real provider against a captured HTTP
/// transport and asserts on the bytes the plugin itself posted — assertions
/// on mock calls alone would prove nothing.
/// </summary>
public sealed class MattermostNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_MATTERMOST_TOKEN";
    private const string Token = "test-mattermost-token-value";
    private const string Server = "https://mattermost.example.invalid";
    private const string Channel = "abcdefghij1234567890abcdef";

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
        values["CodeyBox:Plugins:codeybox.mattermost:TokenEnvVar"] = TokenEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string serverUrl = Server,
        string? defaultChannel = Channel,
        string agnesBaseUrl = "https://agnes.example.invalid",
        bool allowPlainHttp = false,
        string? rateLimitRetries = null,
        string? maxWait = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.mattermost:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.mattermost:ServerUrl"] = serverUrl,
            ["CodeyBox:Plugins:codeybox.mattermost:DefaultChannelId"] = defaultChannel,
            ["CodeyBox:Plugins:codeybox.mattermost:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.mattermost:AllowPlainHttp"] = allowPlainHttp ? "true" : "false",
        };
        if (rateLimitRetries is not null)
            values["CodeyBox:Plugins:codeybox.mattermost:RateLimitMaxRetries"] = rateLimitRetries;
        if (maxWait is not null)
            values["CodeyBox:Plugins:codeybox.mattermost:MaxRateLimitWaitSeconds"] = maxWait;
        return Config(values);
    }

    private static MattermostNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<MattermostNotificationProvider>? logger = null,
        MattermostPostStore? posts = null)
        => new(config, http, logger ?? new CapturingLogger<MattermostNotificationProvider>(), posts: posts);

    private static HttpResponseMessage CreatedJson(string id = "postid1234567890abcdef12") =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent($"{{\"id\":\"{id}\",\"channel_id\":\"{Channel}\",\"message\":\"hi\"}}"),
        };

    [Fact]
    public void Declares_NotificationOnly()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var provider = BuildProvider(EnabledConfig(), new HttpClient(new CapturingHttpHandler()));
            Assert.Equal("mattermost", provider.Name);
            Assert.False(provider.SupportsInteractions);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

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
                    ["CodeyBox:Plugins:codeybox.mattermost:Enabled"] = "false",
                    ["CodeyBox:Plugins:codeybox.mattermost:ServerUrl"] = Server,
                    ["CodeyBox:Plugins:codeybox.mattermost:DefaultChannelId"] = Channel,
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
        var logger = new CapturingLogger<MattermostNotificationProvider>();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task MissingChannel_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var provider = BuildProvider(EnabledConfig(defaultChannel: ""), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(recipients: null), CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task InvalidChannelChars_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(recipients: new[] { "../channels/evil?x=1" }),
                CancellationToken.None);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Theory]
    [InlineData("https://mattermost.example.invalid/?x=1")]
    [InlineData("https://user:pass@mattermost.example.invalid/")]
    [InlineData("https://mattermost.example.invalid/#frag")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task BadServerUrl_MakesNoHttpCall(string serverUrl)
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(serverUrl: serverUrl), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
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
            var refused = new CapturingHttpHandler(_ => CreatedJson());
            await BuildProvider(
                EnabledConfig(serverUrl: "http://mattermost.example.invalid"),
                new HttpClient(refused)).SendAsync(MakeNotification(), CancellationToken.None);
            Assert.Empty(refused.Requests);

            var allowed = new CapturingHttpHandler(_ => CreatedJson());
            await BuildProvider(
                EnabledConfig(serverUrl: "http://mattermost.example.invalid", allowPlainHttp: true),
                new HttpClient(allowed)).SendAsync(MakeNotification(), CancellationToken.None);
            Assert.Single(allowed.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task NormalPost_SendsBearerMarkdown_WithAnswerAndAgnesLinks()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson("rootpost00000000000000001"));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(
                    fields: new Dictionary<string, string> { ["queue"] = "main" },
                    actions: new[] { Action() },
                    answerUrl: "https://codeybox.example.invalid/answer/abc",
                    correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://mattermost.example.invalid/api/v4/posts", request.Url);
            Assert.Equal($"Bearer {Token}", request.Authorization);
            Assert.DoesNotContain(Token, request.Body.Replace($"Bearer {Token}", string.Empty));

            using var doc = JsonDocument.Parse(request.Body);
            var root = doc.RootElement;
            Assert.Equal(Channel, root.GetProperty("channel_id").GetString());
            Assert.False(root.TryGetProperty("root_id", out _));
            var message = root.GetProperty("message").GetString()!;
            Assert.Contains("Queue is empty", message);
            Assert.Contains("https://codeybox.example.invalid/answer/abc", message);
            Assert.Contains("https://agnes.example.invalid/workitems/work-1", message);
            Assert.Contains("main", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RecipientChannel_Wins_OverDefault()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var other = "zzzzzzzzzzzzzzzzzzzzzzzzzz";
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(recipients: new[] { other }),
                CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(request.Body);
            Assert.Equal(other, doc.RootElement.GetProperty("channel_id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task FollowUp_SameWorkItem_ThreadsUnderRoot()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var posts = new MattermostPostStore();
            var handler = new CapturingHttpHandler(_ => CreatedJson("rootpost00000000000000001"));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), posts: posts);

            await provider.SendAsync(
                MakeNotification(actions: new[] { Action("work-9") }, correlationToken: "work-9:q-001"),
                CancellationToken.None);
            await provider.SendAsync(
                MakeNotification(actions: new[] { Action("work-9") }, correlationToken: "work-9:q-002"),
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            using var second = JsonDocument.Parse(handler.Requests[1].Body);
            Assert.Equal("rootpost00000000000000001", second.RootElement.GetProperty("root_id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task SameCorrelationToken_Twice_PostsOnce()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var posts = new MattermostPostStore();
            var handler = new CapturingHttpHandler(_ => CreatedJson("postid00000000000000000001"));
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), posts: posts);
            var notification = MakeNotification(
                actions: new[] { Action() }, correlationToken: "work-1:q-001");

            await provider.SendAsync(notification, CancellationToken.None);
            await provider.SendAsync(notification, CancellationToken.None);

            Assert.Single(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task AmbiguousResponse_Tombstones_SuppressesRepost()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var posts = new MattermostPostStore();
            var calls = 0;
            var handler = new CapturingHttpHandler(_ =>
            {
                calls++;
                // 200 without a post id: the post may already exist.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"not":"an-envelope"}"""),
                };
            });
            var logger = new CapturingLogger<MattermostNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger, posts);

            await provider.SendAsync(
                MakeNotification(actions: new[] { Action() }, correlationToken: "work-2:q-001"),
                CancellationToken.None);
            await provider.SendAsync(
                MakeNotification(actions: new[] { Action() }, correlationToken: "work-2:q-001"),
                CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("rather than risking a duplicate"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RateLimited_RetriesOnceWithinCap_ThenSucceeds()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var calls = 0;
            var handler = new CapturingHttpHandler(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    var limited = new HttpResponseMessage((HttpStatusCode)429);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return limited;
                }
                return CreatedJson("postid00000000000000000002");
            });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(2, calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RateLimited_BeyondWaitCap_DropsWithoutRetry()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var calls = 0;
            var handler = new CapturingHttpHandler(_ =>
            {
                calls++;
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return limited;
            });
            var logger = new CapturingLogger<MattermostNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(maxWait: "30"), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("rate_limited"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RateLimited_RetriesExhausted_StopsRetrying()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var calls = 0;
            var handler = new CapturingHttpHandler(_ =>
            {
                calls++;
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            });
            // Zero retries: exactly one attempt.
            var provider = BuildProvider(EnabledConfig(rateLimitRetries: "0"), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(1, calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task AuthFailure_LoggedWithoutToken_NoThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent($"{{\"message\":\"Invalid or missing token {Token}.\",\"status_code\":401}}"),
            });
            var logger = new CapturingLogger<MattermostNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.DoesNotContain(Token, string.Join("\n", logger.Entries.Select(e => e.Message)));
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("http-401"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task OversizedBody_TruncatedUnderPlatformLimit()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(body: new string('x', 100_000)),
                CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(request.Body);
            var message = doc.RootElement.GetProperty("message").GetString()!;
            Assert.True(message.Length <= MattermostMessageBuilder.MattermostMaxMessageChars);
            Assert.Contains("truncated", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MarkdownInjection_EscapedInPost()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(body: "[steal](https://evil.invalid/) **bold**"),
                CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(request.Body);
            var message = doc.RootElement.GetProperty("message").GetString()!;
            Assert.DoesNotContain("[steal](https://evil.invalid/)", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task CancelledToken_Propagates()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.SendAsync(MakeNotification(), cts.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task TransportFailure_Swallowed_NoThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => throw new HttpRequestException("no route"));
            var logger = new CapturingLogger<MattermostNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task UnsafeAnswerUrl_Omitted_NotEmitted()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(_ => CreatedJson());
            var provider = BuildProvider(EnabledConfig(agnesBaseUrl: ""), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(answerUrl: "javascript:alert(1)"),
                CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            using var doc = JsonDocument.Parse(request.Body);
            Assert.DoesNotContain("javascript:", doc.RootElement.GetProperty("message").GetString()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }
}
