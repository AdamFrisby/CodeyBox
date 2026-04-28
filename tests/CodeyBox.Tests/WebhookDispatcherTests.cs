using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class WebhookDispatcherTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static WebhookEvent MakeEvent(string eventName = "work_item.working") => new()
    {
        Event = eventName,
        WorkItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "test item",
            Prompt = "do the thing",
        },
        Project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Test Project",
            RepositoryUrl = "https://example.com/repo.git",
        },
    };

    private static WebhookEndpointConfig Endpoint(
        string url = "https://example.com/hook",
        string? secretEnvVar = null,
        IReadOnlyList<string>? filter = null,
        int maxAttempts = 1) => new()
        {
            Name = "test",
            Url = url,
            SecretEnvVar = secretEnvVar,
            EventFilter = filter ?? [],
            MaxAttempts = maxAttempts,
            InitialBackoffSeconds = 0,
            TimeoutSeconds = 5,
        };

    private static (HttpWebhookDispatcher dispatcher, List<HttpRequestMessage> requests, RecordingHandler handler)
        BuildDispatcher(HttpStatusCode statusCode, params WebhookEndpointConfig[] endpoints)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler(statusCode, requests);
        var factory = new SingletonHttpClientFactory(handler);
        var dispatcher = new HttpWebhookDispatcher(
            new WebhookDispatcherOptions { Endpoints = endpoints },
            factory,
            NullLogger<HttpWebhookDispatcher>.Instance);
        return (dispatcher, requests, handler);
    }

    // ── HMAC signature ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeSignature_MatchesKnownValue()
    {
        // Known-answer vector derived independently via Python:
        //   import hashlib, hmac
        //   hmac.new(b"test-secret", b'{"event":"work_item.working"}', hashlib.sha256).hexdigest()
        // → b6590508b853f3ebb35926f22c7b40d2c469e7efcbf60b969ac6b9fc493da7cf
        const string secret = "test-secret";
        const string body = """{"event":"work_item.working"}""";
        const string expected = "b6590508b853f3ebb35926f22c7b40d2c469e7efcbf60b969ac6b9fc493da7cf";

        var actual = HttpWebhookDispatcher.ComputeSignature(Encoding.UTF8.GetBytes(body), secret);

        Assert.Equal(expected, actual);
        Assert.Equal(64, actual.Length);
    }

    [Fact]
    public async Task SignedEndpoint_SetsSignatureHeader()
    {
        const string envVar = "TEST_WEBHOOK_SECRET_HMAC";
        Environment.SetEnvironmentVariable(envVar, "my-secret");
        try
        {
            var (dispatcher, requests, _) = BuildDispatcher(HttpStatusCode.OK, Endpoint(secretEnvVar: envVar));
            await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
            await dispatcher.DisposeAsync();

            var req = Assert.Single(requests);
            Assert.True(req.Headers.TryGetValues("X-CodeyBox-Signature", out var sigs));
            Assert.StartsWith("sha256=", sigs!.First());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task SignedEndpoint_MissingEnvVar_OmitsSignatureHeader()
    {
        const string envVar = "TEST_WEBHOOK_SECRET_ABSENT_XYZ";
        Environment.SetEnvironmentVariable(envVar, null);

        var (dispatcher, requests, _) = BuildDispatcher(HttpStatusCode.OK, Endpoint(secretEnvVar: envVar));
        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
        await dispatcher.DisposeAsync();

        var req = Assert.Single(requests);
        Assert.False(req.Headers.Contains("X-CodeyBox-Signature"));
    }

    [Fact]
    public async Task UnsignedEndpoint_OmitsSignatureHeader()
    {
        var (dispatcher, requests, _) = BuildDispatcher(HttpStatusCode.OK, Endpoint(secretEnvVar: null));
        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
        await dispatcher.DisposeAsync();

        var req = Assert.Single(requests);
        Assert.False(req.Headers.Contains("X-CodeyBox-Signature"));
    }

    // ── EventFilter ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EventFilter_BlocksNonMatchingEvents()
    {
        var (dispatcher, requests, _) = BuildDispatcher(
            HttpStatusCode.OK,
            Endpoint(filter: ["work_item.done"]));

        await dispatcher.PublishAsync(MakeEvent("work_item.working"), CancellationToken.None);
        await dispatcher.DisposeAsync();

        Assert.Empty(requests);
    }

    [Fact]
    public async Task EventFilter_AllowsMatchingEvents()
    {
        var (dispatcher, requests, _) = BuildDispatcher(
            HttpStatusCode.OK,
            Endpoint(filter: ["work_item.done"]));

        await dispatcher.PublishAsync(MakeEvent("work_item.done"), CancellationToken.None);
        await dispatcher.DisposeAsync();

        Assert.Single(requests);
    }

    [Fact]
    public async Task EmptyEventFilter_AllowsAllEvents()
    {
        var (dispatcher, requests, _) = BuildDispatcher(HttpStatusCode.OK, Endpoint(filter: []));

        await dispatcher.PublishAsync(MakeEvent("work_item.working"), CancellationToken.None);
        await dispatcher.PublishAsync(MakeEvent("work_item.done"), CancellationToken.None);
        await dispatcher.DisposeAsync();

        Assert.Equal(2, requests.Count);
    }

    // ── Multiple endpoints ───────────────────────────────────────────────────

    [Fact]
    public async Task MultipleEndpoints_AllReceiveMatchingEvent()
    {
        var ep1 = Endpoint("https://example.com/hook1");
        var ep2 = new WebhookEndpointConfig
        {
            Name = "ep2",
            Url = "https://example.com/hook2",
            MaxAttempts = 1,
            InitialBackoffSeconds = 0,
            TimeoutSeconds = 5,
        };

        var (dispatcher, requests, _) = BuildDispatcher(HttpStatusCode.OK, ep1, ep2);
        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
        await dispatcher.DisposeAsync();

        Assert.Equal(2, requests.Count);
        var urls = requests.Select(r => r.RequestUri!.ToString()).Order().ToList();
        Assert.Contains("https://example.com/hook1", urls);
        Assert.Contains("https://example.com/hook2", urls);
    }

    // ── HTTP failure + retry ─────────────────────────────────────────────────

    [Fact]
    public async Task FiveHundredResponse_RetriesAndGivesUpGracefully()
    {
        var (dispatcher, requests, _) = BuildDispatcher(
            HttpStatusCode.InternalServerError,
            Endpoint(maxAttempts: 3));

        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
        await dispatcher.DisposeAsync(); // must not throw

        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task HttpException_DoesNotPropagateOutOfDispatcher()
    {
        var handler = new ThrowingHandler();
        using var factory = new SingletonHttpClientFactory(handler);
        var dispatcher = new HttpWebhookDispatcher(
            new WebhookDispatcherOptions { Endpoints = [Endpoint(maxAttempts: 2)] },
            factory,
            NullLogger<HttpWebhookDispatcher>.Instance);

        await dispatcher.PublishAsync(MakeEvent(), CancellationToken.None);
        var ex = await Record.ExceptionAsync(async () => await dispatcher.DisposeAsync());
        Assert.Null(ex);
    }

    // ── NullWebhookDispatcher ────────────────────────────────────────────────

    [Fact]
    public async Task NullDispatcher_CompletesImmediately()
    {
        var d = new NullWebhookDispatcher();
        var t = d.PublishAsync(MakeEvent(), CancellationToken.None);
        Assert.True(t.IsCompleted);
        await t; // must not throw
    }

    // ── Payload shape ────────────────────────────────────────────────────────

    [Fact]
    public void BuildPayload_ContainsExpectedFields()
    {
        var evt = MakeEvent("work_item.done");
        var json = HttpWebhookDispatcher.BuildPayload(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("work_item.done", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("occurredAt", out _));
        Assert.True(root.TryGetProperty("workItem", out var wi));
        Assert.True(root.TryGetProperty("project", out var proj));

        Assert.Equal("proj", wi.GetProperty("projectId").GetString());
        Assert.Equal("proj", proj.GetProperty("id").GetString());
        Assert.Equal("Test Project", proj.GetProperty("displayName").GetString());
    }
}

// ── Test fakes ────────────────────────────────────────────────────────────────

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly List<HttpRequestMessage> _captured;

    public RecordingHandler(HttpStatusCode statusCode, List<HttpRequestMessage> captured)
    {
        _statusCode = statusCode;
        _captured = captured;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (_captured) _captured.Add(request);
        return Task.FromResult(new HttpResponseMessage(_statusCode));
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new HttpRequestException("simulated network failure");
}

internal sealed class SingletonHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler;
    public SingletonHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    public void Dispose() => _handler.Dispose();
}
