using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

public sealed class ChatNotificationProviderTests
{
    private static Notification MakeNotification(
        string conditionId = "queue_empty",
        string title = "Queue is empty",
        string? body = "All work items processed.",
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyDictionary<string, string>? fields = null,
        DateTimeOffset? timestamp = null)
        => new()
        {
            ConditionId = conditionId,
            Title = title,
            Body = body,
            Severity = severity,
            Fields = fields,
            Timestamp = timestamp ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task Disabled_ReturnsImmediately_NoHttpCall()
    {
        var handler = new CapturingHttpHandler();
        var http = new HttpClient(handler);
        var logger = new CapturingLogger<ChatNotificationProvider>();
        var opts = new ChatProviderOptions { Enabled = false };

        var provider = new ChatNotificationProvider(opts, http, logger);
        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Enabled_NoWebhooks_ReturnsImmediately_NoHttpCall()
    {
        var handler = new CapturingHttpHandler();
        var http = new HttpClient(handler);
        var logger = new CapturingLogger<ChatNotificationProvider>();
        var opts = new ChatProviderOptions { Enabled = true, Webhooks = [] };

        var provider = new ChatNotificationProvider(opts, http, logger);
        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UrlEnvVarMissing_LogsWarning_NoHttpCall()
    {
        var handler = new CapturingHttpHandler();
        var http = new HttpClient(handler);
        var logger = new CapturingLogger<ChatNotificationProvider>();
        var opts = new ChatProviderOptions
        {
            Enabled = true,
            Webhooks =
            [
                new ChatWebhookOptions
                {
                    Platform = ChatPlatform.Slack,
                    UrlEnvVar = "CODEYBOX_TEST_CHAT_URL_MISSING_XYZ",
                },
            ],
        };

        var provider = new ChatNotificationProvider(opts, http, logger);
        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("UrlEnvVar")
            && e.Message.Contains("not set in environment"));
    }

    [Fact]
    public async Task UrlEnvVarUnconfigured_LogsWarning_NoHttpCall()
    {
        var handler = new CapturingHttpHandler();
        var http = new HttpClient(handler);
        var logger = new CapturingLogger<ChatNotificationProvider>();
        var opts = new ChatProviderOptions
        {
            Enabled = true,
            Webhooks =
            [
                new ChatWebhookOptions { Platform = ChatPlatform.Discord, UrlEnvVar = null },
            ],
        };

        var provider = new ChatNotificationProvider(opts, http, logger);
        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("no UrlEnvVar configured"));
    }

    [Fact]
    public async Task Slack_PostsCorrectPayloadShape()
    {
        const string envVar = "CODEYBOX_TEST_CHAT_SLACK_URL_1";
        const string url = "https://hooks.slack.test/services/T/B/x";
        Environment.SetEnvironmentVariable(envVar, url);
        try
        {
            var handler = new CapturingHttpHandler();
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks =
                [
                    new ChatWebhookOptions
                    {
                        Platform = ChatPlatform.Slack,
                        UrlEnvVar = envVar,
                        Username = "codeybox-bot",
                    },
                ],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await provider.SendAsync(MakeNotification(
                title: "Quotas exhausted",
                body: "All quotas below 10%.",
                severity: NotificationSeverity.Warning,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal(url, req.Url);
            Assert.StartsWith("application/json", req.ContentType);

            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("text", out var text));
            Assert.Contains("Quotas exhausted", text.GetString());
            Assert.Equal("codeybox-bot", root.GetProperty("username").GetString());

            var attachments = root.GetProperty("attachments");
            Assert.Equal(1, attachments.GetArrayLength());
            var att = attachments[0];
            Assert.Equal("warning", att.GetProperty("color").GetString());
            Assert.Equal("Quotas exhausted", att.GetProperty("title").GetString());
            Assert.Equal("All quotas below 10%.", att.GetProperty("text").GetString());
            var slackFields = att.GetProperty("fields");
            Assert.Equal(1, slackFields.GetArrayLength());
            Assert.Equal("agent", slackFields[0].GetProperty("title").GetString());
            Assert.Equal("claude", slackFields[0].GetProperty("value").GetString());
            Assert.True(slackFields[0].GetProperty("short").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task Discord_PostsCorrectPayloadShape()
    {
        const string envVar = "CODEYBOX_TEST_CHAT_DISCORD_URL_1";
        const string url = "https://discord.test/api/webhooks/123/abc";
        Environment.SetEnvironmentVariable(envVar, url);
        try
        {
            var handler = new CapturingHttpHandler();
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks =
                [
                    new ChatWebhookOptions
                    {
                        Platform = ChatPlatform.Discord,
                        UrlEnvVar = envVar,
                        Username = "CodeyBox",
                    },
                ],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await provider.SendAsync(MakeNotification(
                title: "Sandbox leak reaped",
                body: "Reaped 2 leaked sandboxes.",
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["reaped"] = "2" }),
                CancellationToken.None);

            var req = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal(url, req.Url);

            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            Assert.Contains("Sandbox leak reaped", root.GetProperty("content").GetString());

            var embeds = root.GetProperty("embeds");
            Assert.Equal(1, embeds.GetArrayLength());
            var embed = embeds[0];
            Assert.Equal("Sandbox leak reaped", embed.GetProperty("title").GetString());
            Assert.Equal("Reaped 2 leaked sandboxes.", embed.GetProperty("description").GetString());
            Assert.Equal(0xE01E5A, embed.GetProperty("color").GetInt32());
            Assert.Equal("CodeyBox", embed.GetProperty("author").GetProperty("name").GetString());

            var fields = embed.GetProperty("fields");
            Assert.Equal(1, fields.GetArrayLength());
            Assert.Equal("reaped", fields[0].GetProperty("name").GetString());
            Assert.Equal("2", fields[0].GetProperty("value").GetString());
            Assert.True(fields[0].GetProperty("inline").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task FansOutToBothPlatforms_WhenBothConfigured()
    {
        const string slackVar = "CODEYBOX_TEST_CHAT_SLACK_URL_FAN";
        const string discordVar = "CODEYBOX_TEST_CHAT_DISCORD_URL_FAN";
        Environment.SetEnvironmentVariable(slackVar, "https://hooks.slack.test/s/x");
        Environment.SetEnvironmentVariable(discordVar, "https://discord.test/api/webhooks/y");
        try
        {
            var handler = new CapturingHttpHandler();
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks =
                [
                    new ChatWebhookOptions { Platform = ChatPlatform.Slack, UrlEnvVar = slackVar },
                    new ChatWebhookOptions { Platform = ChatPlatform.Discord, UrlEnvVar = discordVar },
                ],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(handler.Requests, r => r.Url == "https://hooks.slack.test/s/x");
            Assert.Contains(handler.Requests, r => r.Url == "https://discord.test/api/webhooks/y");
        }
        finally
        {
            Environment.SetEnvironmentVariable(slackVar, null);
            Environment.SetEnvironmentVariable(discordVar, null);
        }
    }

    [Fact]
    public async Task NonSuccessHttpStatus_LogsWarning_DoesNotThrow()
    {
        const string envVar = "CODEYBOX_TEST_CHAT_SLACK_URL_5XX";
        Environment.SetEnvironmentVariable(envVar, "https://hooks.slack.test/s/x");
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks = [new ChatWebhookOptions { Platform = ChatPlatform.Slack, UrlEnvVar = envVar }],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("500"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task TransportException_LogsErrorAndContinuesToNextWebhook()
    {
        const string slackVar = "CODEYBOX_TEST_CHAT_SLACK_URL_FAIL";
        const string discordVar = "CODEYBOX_TEST_CHAT_DISCORD_URL_OK";
        Environment.SetEnvironmentVariable(slackVar, "https://hooks.slack.test/throw");
        Environment.SetEnvironmentVariable(discordVar, "https://discord.test/api/webhooks/ok");
        try
        {
            var handler = new CapturingHttpHandler(req =>
            {
                if (req.RequestUri!.Host == "hooks.slack.test")
                    throw new HttpRequestException("simulated network failure");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks =
                [
                    new ChatWebhookOptions { Platform = ChatPlatform.Slack, UrlEnvVar = slackVar },
                    new ChatWebhookOptions { Platform = ChatPlatform.Discord, UrlEnvVar = discordVar },
                ],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
                && e.Message.Contains("delivery failed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(slackVar, null);
            Environment.SetEnvironmentVariable(discordVar, null);
        }
    }

    [Fact]
    public async Task OperationCancelled_Rethrows()
    {
        const string envVar = "CODEYBOX_TEST_CHAT_SLACK_URL_CANCEL";
        Environment.SetEnvironmentVariable(envVar, "https://hooks.slack.test/cancel");
        try
        {
            var handler = new CapturingHttpHandler(_ =>
                throw new OperationCanceledException("shutdown"));
            var http = new HttpClient(handler);
            var logger = new CapturingLogger<ChatNotificationProvider>();
            var opts = new ChatProviderOptions
            {
                Enabled = true,
                Webhooks = [new ChatWebhookOptions { Platform = ChatPlatform.Slack, UrlEnvVar = envVar }],
            };
            var provider = new ChatNotificationProvider(opts, http, logger);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => provider.SendAsync(MakeNotification(), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Theory]
    [InlineData(NotificationSeverity.Information, "good", 0x2EB67D)]
    [InlineData(NotificationSeverity.Warning, "warning", 0xECB22E)]
    [InlineData(NotificationSeverity.Critical, "danger", 0xE01E5A)]
    public void SeverityMapping_IsStable(NotificationSeverity sev, string slackColor, int discordColor)
    {
        Assert.Equal(slackColor, ChatNotificationProvider.SlackColor(sev));
        Assert.Equal(discordColor, ChatNotificationProvider.DiscordColor(sev));
    }

    [Fact]
    public void ProviderName_IsChat()
    {
        var provider = new ChatNotificationProvider(
            new ChatProviderOptions { Enabled = false },
            new HttpClient(new CapturingHttpHandler()),
            new CapturingLogger<ChatNotificationProvider>());
        Assert.Equal("chat", provider.Name);
    }
}

/// <summary>
/// Test-only HttpMessageHandler that records each outbound request and either
/// returns a configurable response or invokes a per-request callback (for
/// asserting payload shape / simulating transport faults).
/// </summary>
internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;

    public sealed record CapturedRequest(HttpMethod Method, string Url, string ContentType, string Body);

    public List<CapturedRequest> Requests { get; } = new();

    public CapturingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bodyText = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var contentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty;
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), contentType, bodyText));

        return _responder is null
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : _responder(request);
    }
}
