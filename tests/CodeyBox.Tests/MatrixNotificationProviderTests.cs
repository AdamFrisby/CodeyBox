using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.MatrixPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for the Matrix notification plugin: the
/// <c>PUT …/send/m.room.message/{txnId}</c> body carries severity, summary
/// and fields as text plus an HTML parallel, and the AnswerUrl route surfaces
/// as a raw URL and as an anchor — Matrix is notification-only here, so the
/// provider never renders interactive controls and accepts no inbound
/// answers. Each test drives the real provider against a captured HTTP
/// transport and asserts on the bytes the plugin itself exchanged, including
/// the encryption-state read that gates every send.
/// </summary>
public sealed class MatrixNotificationProviderTests
{
    private const string TokenEnvVar = "CODEYBOX_TEST_MATRIX_ACCESS_TOKEN";
    private const string Token = "syt-test-access-token-value";
    private const string Homeserver = "https://matrix.example.invalid";
    private const string Room = "!codeybox:example.invalid";

    /// <summary>Recorded send success envelope.</summary>
    private const string SendCreatedJson =
        """{"event_id":"$abc123:example.invalid"}""";

    /// <summary>Recorded unencrypted signal: no m.room.encryption state.</summary>
    private const string StateNotFoundJson =
        """{"errcode":"M_NOT_FOUND","error":"Event not found."}""";

    /// <summary>Recorded encrypted signal: an m.room.encryption state event.</summary>
    private const string StateEncryptedJson =
        """{"algorithm":"m.megolm.v1.aes-sha2","rotation_period_ms":604800000,"rotation_period_msgs":100}""";

    /// <summary>Recorded auth failure envelope (bad access token).</summary>
    private const string UnknownTokenJson =
        """{"errcode":"M_UNKNOWN_TOKEN","error":"Invalid access token passed"}""";

    /// <summary>Recorded rate-limit envelope. Zero backoff so tests retry
    /// without waiting; the provider still honours the same-txn path.</summary>
    private const string RateLimitedJson =
        """{"errcode":"M_LIMIT_EXCEEDED","error":"Too many requests","retry_after_ms":0}""";

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
        values["CodeyBox:Plugins:codeybox.matrix:AccessTokenEnvVar"] = TokenEnvVar;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration EnabledConfig(
        string homeserverUrl = Homeserver,
        string defaultRoomId = Room,
        string agnesBaseUrl = "https://agnes.example.invalid",
        bool allowPlainHttp = false,
        bool useHtml = true,
        int maxRetries = 2,
        string? extraRoom = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:Plugins:codeybox.matrix:Enabled"] = "true",
            ["CodeyBox:Plugins:codeybox.matrix:HomeserverUrl"] = homeserverUrl,
            ["CodeyBox:Plugins:codeybox.matrix:DefaultRoomId"] = defaultRoomId,
            ["CodeyBox:Plugins:codeybox.matrix:AgnesBaseUrl"] = agnesBaseUrl,
            ["CodeyBox:Plugins:codeybox.matrix:AllowPlainHttp"] = allowPlainHttp ? "true" : "false",
            ["CodeyBox:Plugins:codeybox.matrix:UseHtml"] = useHtml ? "true" : "false",
            ["CodeyBox:Plugins:codeybox.matrix:MaxRetries"] = maxRetries.ToString(),
        };
        values["CodeyBox:Plugins:codeybox.matrix:AllowedRoomIds:0"] = Room;
        if (extraRoom is not null)
            values["CodeyBox:Plugins:codeybox.matrix:AllowedRoomIds:1"] = extraRoom;
        return Config(values);
    }

    private static MatrixNotificationProvider BuildProvider(
        IConfiguration config,
        HttpClient http,
        CapturingLogger<MatrixNotificationProvider>? logger = null)
        => new(config, http, logger ?? new CapturingLogger<MatrixNotificationProvider>());

    /// <summary>Responder serving the encryption-state read from one queue
    /// and every send from another, so tests pin both legs of the flow.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> MatrixResponder(
        Func<HttpRequestMessage, HttpResponseMessage>? onState = null,
        Func<HttpRequestMessage, HttpResponseMessage>? onSend = null)
        => req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/state/m.room.encryption/", StringComparison.Ordinal))
                return (onState ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(StateNotFoundJson),
                }))(req);
            if (url.Contains("/send/m.room.message/", StringComparison.Ordinal))
                return (onSend ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SendCreatedJson),
                }))(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

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
                    ["CodeyBox:Plugins:codeybox.matrix:Enabled"] = "false",
                    ["CodeyBox:Plugins:codeybox.matrix:HomeserverUrl"] = Homeserver,
                    ["CodeyBox:Plugins:codeybox.matrix:DefaultRoomId"] = Room,
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
        var logger = new CapturingLogger<MatrixNotificationProvider>();
        var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not set"));
    }

    [Fact]
    public async Task MissingHomeserver_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler();
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(homeserverUrl: ""), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("HomeserverUrl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task DirtyHomeserver_Refused()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            foreach (var bad in new[]
            {
                "https://matrix.example.invalid?x=1",
                "https://matrix.example.invalid#frag",
                "https://user:pass@matrix.example.invalid",
                "not-a-url",
            })
            {
                var handler = new CapturingHttpHandler();
                var logger = new CapturingLogger<MatrixNotificationProvider>();
                var provider = BuildProvider(EnabledConfig(homeserverUrl: bad), new HttpClient(handler), logger);

                await provider.SendAsync(MakeNotification(), CancellationToken.None);

                Assert.Empty(handler.Requests);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
            }
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
            var refusedHandler = new CapturingHttpHandler(MatrixResponder());
            var refusedLogger = new CapturingLogger<MatrixNotificationProvider>();
            var refused = BuildProvider(
                EnabledConfig(homeserverUrl: "http://matrix.internal:8008", allowPlainHttp: false),
                new HttpClient(refusedHandler), refusedLogger);

            await refused.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(refusedHandler.Requests);
            Assert.Contains(refusedLogger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("plain http"));

            var allowedHandler = new CapturingHttpHandler(MatrixResponder());
            var allowed = BuildProvider(
                EnabledConfig(homeserverUrl: "http://matrix.internal:8008", allowPlainHttp: true),
                new HttpClient(allowedHandler));

            await allowed.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal(2, allowedHandler.Requests.Count); // state read + send
            Assert.StartsWith("http://matrix.internal:8008/_matrix/client/v3/", allowedHandler.Requests[0].Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MissingRoom_LogsWarning_MakesNoHttpCall()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(defaultRoomId: ""), new HttpClient(handler), logger);
            // No allowlist entry matches either: clear the default allowlist.
            var config = Config(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:codeybox.matrix:Enabled"] = "true",
                ["CodeyBox:Plugins:codeybox.matrix:HomeserverUrl"] = Homeserver,
                ["CodeyBox:Plugins:codeybox.matrix:DefaultRoomId"] = "",
            });
            provider = BuildProvider(config, new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no room"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task NonRoomIdRecipient_Refused()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            foreach (var bad in new[]
            {
                "#general:example.invalid",
                "@user:example.invalid",
                "https://matrix.example.invalid/#/room/!x:y",
                "!no-colon-here",
                "!too:many:colons",
                "!white space:example.invalid",
            })
            {
                var handler = new CapturingHttpHandler(MatrixResponder());
                var logger = new CapturingLogger<MatrixNotificationProvider>();
                var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

                await provider.SendAsync(
                    MakeNotification(recipients: [bad], correlationToken: "work-1:q-001"),
                    CancellationToken.None);

                Assert.Empty(handler.Requests);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RoomNotAllowlisted_Refused()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(
                MakeNotification(recipients: ["!other:example.invalid"]),
                CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("not explicitly configured"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task AllowlistedRecipientRoom_Sends()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            const string other = "!other:example.invalid";
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(extraRoom: other), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(recipients: [other]), CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/m.room.message/"));
            Assert.Contains(Uri.EscapeDataString(other), send.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task EncryptedRoom_Refused_NoSend()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onState: _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StateEncryptedJson),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            // The state read happened; the send must never follow into an
            // encrypted room — no downgrade, no E2EE claim.
            Assert.Single(handler.Requests);
            Assert.Contains("/state/m.room.encryption/", handler.Requests[0].Url);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("encrypted"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task UnknownSecurityState_FailsClosed_NoSend()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            // A server error on the state read is "unknown" — fail closed.
            var handler = new CapturingHttpHandler(MatrixResponder(
                onState: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"errcode":"M_UNKNOWN","error":"oops"}"""),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("failing closed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MalformedStateBody_FailsClosed_NoSend()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onState: _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not-json{{"),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("failing closed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task StateRedirect_FailsClosed_NoSend()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onState: _ => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Content = new StringContent(string.Empty),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            // A redirect policy never re-sends the bearer token; the room
            // stays unverified and the send never happens.
            Assert.Single(handler.Requests);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("failing closed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Outbound_RendersSeveritySummaryAndFields_AsTextAndHtml()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            string? auth = null;
            var handler = new CapturingHttpHandler(req =>
            {
                if (req.RequestUri!.ToString().Contains("/state/m.room.encryption/"))
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(StateNotFoundJson),
                    };
                auth = req.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SendCreatedJson),
                };
            });
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "All quotas exhausted",
                body: "Spend is blocked.",
                severity: NotificationSeverity.Critical,
                fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = "claude" }),
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            var state = handler.Requests[0];
            Assert.Contains("/state/m.room.encryption/", state.Url);
            Assert.Equal("Bearer", state.Authorization.Split(' ')[0]);

            var send = handler.Requests[1];
            Assert.Contains("/_matrix/client/v3/rooms/", send.Url);
            Assert.Contains("/send/m.room.message/cbx-", send.Url);
            Assert.DoesNotContain("syt-test", send.Url, StringComparison.Ordinal);
            Assert.Equal($"Bearer {Token}", auth);

            using var doc = JsonDocument.Parse(send.Body);
            var root = doc.RootElement;
            Assert.Equal("m.text", root.GetProperty("msgtype").GetString());

            var body = root.GetProperty("body").GetString()!;
            Assert.Contains("🚨 All quotas exhausted", body);
            Assert.Contains("Spend is blocked.", body);
            Assert.Contains("agent: claude", body);
            Assert.Contains("queue_empty", body);

            Assert.Equal("org.matrix.custom.html", root.GetProperty("format").GetString());
            var html = root.GetProperty("formatted_body").GetString()!;
            Assert.Contains("<strong>", html);
            Assert.Contains("agent", html);
            Assert.False(root.TryGetProperty("m.relates_to", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Payload_MatchesRecordedSendShape()
    {
        // Recorded-shape fixture standing in for a live-server test: a
        // homeserver accepts exactly {msgtype, body, format,
        // formatted_body} (+ m.relates_to for thread replies) on
        // PUT …/send/m.room.message/{txnId} with a Bearer token. Assert the
        // wire keys verbatim so a rendering refactor cannot silently drift
        // the contract.
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(correlationToken: "work-9:q-009",
                    answerUrl: "https://codeybox.example.invalid/workitems/work-9/questions"),
                CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/"));
            using var doc = JsonDocument.Parse(send.Body);
            var root = doc.RootElement;
            var topKeys = root.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(["body", "format", "formatted_body", "msgtype"], topKeys);
            Assert.Equal(JsonValueKind.String, root.GetProperty("body").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("formatted_body").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task Question_SurfacesAnswerRoute_AsUrlAndAnchor()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            var answerUrl = "https://codeybox.example.invalid/workitems/work-1/questions";
            await provider.SendAsync(MakeNotification(
                title: "Input needed: q-001",
                actions: [Action("work-1", "q-001", "Use rollbacks"), Action("work-1", "q-001", "Stay the course")],
                answerUrl: answerUrl,
                correlationToken: "work-1:q-001"),
                CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/"));
            using var doc = JsonDocument.Parse(send.Body);
            var root = doc.RootElement;

            // Notification-only by honest declaration: offered actions never
            // become interactive controls, so the message carries the answer
            // route — never an unanswerable prompt.
            Assert.False(provider.SupportsInteractions);
            Assert.Contains($"Answer here: {answerUrl}", root.GetProperty("body").GetString());
            Assert.Contains($"<a href=\"{answerUrl}\">Answer in CodeyBox</a>",
                root.GetProperty("formatted_body").GetString());
            Assert.Contains("https://agnes.example.invalid/workitems/work-1",
                root.GetProperty("formatted_body").GetString());
            Assert.DoesNotContain("codeybox_answer", send.Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task PlainTextMode_OmitsHtml()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(useHtml: false), new HttpClient(handler));

            await provider.SendAsync(
                MakeNotification(answerUrl: "https://codeybox.example.invalid/workitems/work-1/questions"),
                CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/"));
            using var doc = JsonDocument.Parse(send.Body);
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("format", out _));
            Assert.False(root.TryGetProperty("formatted_body", out _));
            Assert.Contains("Answer here: https://codeybox.example.invalid/workitems/work-1/questions",
                root.GetProperty("body").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task FollowUp_UsesThreadRelation()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                title: "First",
                actions: [Action("work-7", "q-001")],
                correlationToken: "work-7:q-001"), CancellationToken.None);
            await provider.SendAsync(MakeNotification(
                title: "Second",
                body: "Still going.",
                actions: [Action("work-7", "q-002")],
                correlationToken: "work-7:q-002"), CancellationToken.None);

            var sends = handler.Requests.Where(r => r.Url.Contains("/send/")).ToList();
            Assert.Equal(2, sends.Count);
            using var first = JsonDocument.Parse(sends[0].Body);
            Assert.False(first.RootElement.TryGetProperty("m.relates_to", out _));

            using var second = JsonDocument.Parse(sends[1].Body);
            var relates = second.RootElement.GetProperty("m.relates_to");
            Assert.Equal("m.thread", relates.GetProperty("rel_type").GetString());
            Assert.Equal("$abc123:example.invalid", relates.GetProperty("event_id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RateLimit_RetriesWithSameTxnId_ThenSucceeds()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var sendCalls = 0;
            var handler = new CapturingHttpHandler(req =>
            {
                if (req.RequestUri!.ToString().Contains("/state/"))
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(StateNotFoundJson),
                    };
                sendCalls++;
                return sendCalls == 1
                    ? new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent(RateLimitedJson),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SendCreatedJson),
                    };
            });
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            var sends = handler.Requests.Where(r => r.Url.Contains("/send/")).ToList();
            Assert.Equal(2, sends.Count);
            // Stable transaction ID: the retry re-PUTs the same path, so the
            // server dedups instead of double-posting.
            Assert.Equal(sends[0].Url, sends[1].Url);
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("send failed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task RateLimit_Exhausted_StopsAfterBound_LogsWarning()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onSend: _ => new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent(RateLimitedJson),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(maxRetries: 2), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            var sends = handler.Requests.Where(r => r.Url.Contains("/send/")).ToList();
            Assert.Equal(3, sends.Count); // 1 initial + 2 retries, all one txn
            Assert.Single(sends.Select(s => s.Url).Distinct());
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("M_LIMIT_EXCEEDED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public void TxnId_IsStable_ForSameNotification()
    {
        var first = MatrixTxnIds.ForNotification(MakeNotification());
        var second = MatrixTxnIds.ForNotification(MakeNotification());
        Assert.Equal(first, second);
        Assert.StartsWith("cbx-", first);
        Assert.DoesNotContain("/", first);
    }

    [Fact]
    public async Task AuthFailure_LogsWarning_RedactsToken()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onSend: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(UnknownTokenJson),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("M_UNKNOWN_TOKEN"));
            // The secret travels the header only: never in URLs, bodies, or logs.
            Assert.DoesNotContain(Token, string.Join("\n", handler.Requests.Select(r => r.Url)));
            Assert.DoesNotContain(Token, string.Join("\n", handler.Requests.Select(r => r.Body)));
            Assert.DoesNotContain(Token, string.Join("\n", logger.Entries.Select(e => e.Message)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task MalformedSendBody_LogsWarning_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onSend: _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not-json{{"),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("malformed_response"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task OversizedResponse_Bounded_LogsWarning()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder(
                onSend: _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('x', MatrixApiClient.MaxResponseBodyBytes + 1)),
                }));
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("response_too_large"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task OversizedBody_IsTruncated()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var config = Config(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:codeybox.matrix:Enabled"] = "true",
                ["CodeyBox:Plugins:codeybox.matrix:HomeserverUrl"] = Homeserver,
                ["CodeyBox:Plugins:codeybox.matrix:DefaultRoomId"] = Room,
                ["CodeyBox:Plugins:codeybox.matrix:AllowedRoomIds:0"] = Room,
                ["CodeyBox:Plugins:codeybox.matrix:MaxTextChars"] = "100",
            });
            var provider = BuildProvider(config, new HttpClient(handler));

            await provider.SendAsync(MakeNotification(body: new string('b', 5000)), CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/"));
            using var doc = JsonDocument.Parse(send.Body);
            var body = doc.RootElement.GetProperty("body").GetString()!;
            Assert.Contains("(truncated)", body);
            Assert.True(body.Length < 5000);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task UntrustedContent_IsHtmlEscaped()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            await provider.SendAsync(MakeNotification(
                body: "click <script>alert(1)</script> & enjoy",
                fields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["detail"] = "a\n<b>bold</b>",
                }),
                CancellationToken.None);

            var send = Assert.Single(handler.Requests, r => r.Url.Contains("/send/"));
            using var doc = JsonDocument.Parse(send.Body);
            var root = doc.RootElement;
            var html = root.GetProperty("formatted_body").GetString()!;
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&amp;", html);
            // Plain body keeps the raw text; the field value's newline is
            // flattened so it cannot break out of its line.
            var body = root.GetProperty("body").GetString()!;
            Assert.Contains("<script>alert(1)</script>", body);
            Assert.Contains("detail: a <b>bold</b>", body);
            Assert.DoesNotContain("detail: a\n<b>bold</b>", body);
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
            var logger = new CapturingLogger<MatrixNotificationProvider>();
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
    public async Task InvalidPostTimeout_FallsBackToDefault_AndWarns()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var logger = new CapturingLogger<MatrixNotificationProvider>();
            var config = Config(new Dictionary<string, string?>
            {
                ["CodeyBox:Plugins:codeybox.matrix:Enabled"] = "true",
                ["CodeyBox:Plugins:codeybox.matrix:HomeserverUrl"] = Homeserver,
                ["CodeyBox:Plugins:codeybox.matrix:DefaultRoomId"] = Room,
                ["CodeyBox:Plugins:codeybox.matrix:AllowedRoomIds:0"] = Room,
                ["CodeyBox:Plugins:codeybox.matrix:PostTimeoutSeconds"] = "0",
            });
            var provider = BuildProvider(config, new HttpClient(handler), logger);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Contains(handler.Requests, r => r.Url.Contains("/send/"));
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                && e.Message.Contains("PostTimeoutSeconds"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenEnvVar, null);
        }
    }

    [Fact]
    public async Task ConcurrentSends_ThreadSafely()
    {
        Environment.SetEnvironmentVariable(TokenEnvVar, Token);
        try
        {
            var handler = new CapturingHttpHandler(MatrixResponder());
            var provider = BuildProvider(EnabledConfig(), new HttpClient(handler));

            var tasks = Enumerable.Range(0, 8).Select(i => provider.SendAsync(
                MakeNotification(
                    title: $"Item {i}",
                    actions: [Action($"work-{i}", "q-001")],
                    correlationToken: $"work-{i}:q-001"),
                CancellationToken.None));
            await Task.WhenAll(tasks);

            Assert.Equal(8, handler.Requests.Count(r => r.Url.Contains("/send/")));
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

        Assert.Equal("matrix", provider.Name);
        Assert.False(provider.SupportsInteractions);
    }

    [Fact]
    public void RoomId_Validation_Rules()
    {
        Assert.True(MatrixRoomIds.IsValid("!abc:example.invalid"));
        Assert.False(MatrixRoomIds.IsValid(null));
        Assert.False(MatrixRoomIds.IsValid(""));
        Assert.False(MatrixRoomIds.IsValid("#alias:example.invalid"));
        Assert.False(MatrixRoomIds.IsValid("@user:example.invalid"));
        Assert.False(MatrixRoomIds.IsValid("!missing-server-colon"));
        Assert.False(MatrixRoomIds.IsValid("!:empty-local"));
        Assert.False(MatrixRoomIds.IsValid(new string('!', 1) + new string('x', 300) + ":s"));
    }
}
