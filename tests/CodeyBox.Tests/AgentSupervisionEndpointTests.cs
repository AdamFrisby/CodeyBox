using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentSupervisionEndpointTests : IDisposable
{
    private readonly AgentSupervisionApiFactory _factory = new();
    private readonly HttpClient _client;

    public AgentSupervisionEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ListSessions_WhenDisabled_ReturnsDisabledEnvelope()
    {
        _factory.SetEnabled(false);

        var resp = await _client.GetAsync("/agent-supervision/sessions");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Empty(body.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task ListSessions_WhenEnabled_ReturnsLiveSessions()
    {
        _factory.SetEnabled(true);
        await using var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var resp = await _client.GetAsync("/agent-supervision/sessions");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        var session = Assert.Single(body.GetProperty("sessions").EnumerateArray());
        Assert.Equal(scope.SessionId, session.GetProperty("sessionId").GetString());
        Assert.Equal("work", session.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Inject_WhenDisabled_Returns403()
    {
        _factory.SetEnabled(false);

        var resp = await _client.PostAsJsonAsync(
            "/agent-supervision/sessions/ags-missing/injections",
            new AgentSupervisionInjectionRequest("hello", "alice"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disabled", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inject_UnknownSession_Returns404()
    {
        _factory.SetEnabled(true);

        var resp = await _client.PostAsJsonAsync(
            "/agent-supervision/sessions/ags-missing/injections",
            new AgentSupervisionInjectionRequest("hello", "alice"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Inject_LiveSession_ReturnsAccepted()
    {
        _factory.SetEnabled(true);
        await using var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var resp = await _client.PostAsJsonAsync(
            $"/agent-supervision/sessions/{scope.SessionId}/injections",
            new AgentSupervisionInjectionRequest("please inspect the failing test", "alice"));

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal("accepted", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inject_QueueFull_Returns429()
    {
        _factory.SetOptions(new AgentSupervisionOptions { Enabled = true, InjectionQueueCapacity = 1 });
        await using var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var first = await _client.PostAsJsonAsync(
            $"/agent-supervision/sessions/{scope.SessionId}/injections",
            new AgentSupervisionInjectionRequest("first", "alice"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await _client.PostAsJsonAsync(
            $"/agent-supervision/sessions/{scope.SessionId}/injections",
            new AgentSupervisionInjectionRequest("second", "alice"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queue_full", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inject_ClosedSession_Returns409()
    {
        _factory.SetOptions(new AgentSupervisionOptions
        {
            Enabled = true,
            InjectionDrainIdleTimeoutMs = 0,
        });
        var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        // Queue one then drain to mark the session no-longer-accepting.
        await _factory.Supervision.EnqueueInjectionAsync(
            scope.SessionId,
            new AgentSupervisionInjectionRequest("warm", "alice"));
        await scope.RunPendingInjectionsAsync(new AgentResult(true, "auto", null, null), (_, _) =>
            Task.FromResult(new AgentResult(true, "drained", null, null)));

        var resp = await _client.PostAsJsonAsync(
            $"/agent-supervision/sessions/{scope.SessionId}/injections",
            new AgentSupervisionInjectionRequest("too late", "alice"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("closed", body.GetProperty("status").GetString());

        await scope.DisposeAsync();
    }

    [Fact]
    public async Task Inject_InvalidMessage_Returns400()
    {
        _factory.SetEnabled(true);
        await using var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        var resp = await _client.PostAsJsonAsync(
            $"/agent-supervision/sessions/{scope.SessionId}/injections",
            new AgentSupervisionInjectionRequest("   ", "alice"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inject_AuthoritativeActorIsDerivedFromServer_NotClientPayload()
    {
        _factory.SetEnabled(true);
        await using var scope = await _factory.Supervision.TryStartSessionAsync(Start())
            ?? throw new InvalidOperationException("expected supervision scope");

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/agent-supervision/sessions/{scope.SessionId}/injections")
        {
            Content = JsonContent.Create(new AgentSupervisionInjectionRequest("inspect", "totally-trusted-name")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "endpoint-test-token-that-is-long-enough-to-fingerprint");
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var queued = Assert.Single(_factory.Notifier.QueuedInjections);
        Assert.StartsWith("apikey:", queued.Actor, StringComparison.Ordinal);
        Assert.Contains("totally-trusted-name", queued.Actor, StringComparison.Ordinal);
        Assert.NotEqual("totally-trusted-name", queued.Actor);
    }

    [Fact]
    public async Task ListSessions_Pagination_RespectsSkipAndTakeAndTailFlag()
    {
        _factory.SetOptions(new AgentSupervisionOptions
        {
            Enabled = true,
            MaxSessions = 8,
            DefaultListPageSize = 2,
            MaxListPageSize = 4,
        });

        var scopes = new List<IAgentSupervisionSession>();
        for (var i = 0; i < 3; i++)
        {
            var scope = await _factory.Supervision.TryStartSessionAsync(Start())
                ?? throw new InvalidOperationException("expected supervision scope");
            scopes.Add(scope);
        }
        try
        {
            var resp = await _client.GetAsync("/agent-supervision/sessions?skip=1&take=1&includeOutputTail=false");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, body.GetProperty("total").GetInt32());
            Assert.Equal(1, body.GetProperty("skip").GetInt32());
            Assert.Equal(1, body.GetProperty("take").GetInt32());
            Assert.Single(body.GetProperty("sessions").EnumerateArray());
        }
        finally
        {
            foreach (var s in scopes)
                await s.DisposeAsync();
        }
    }

    private static AgentSupervisionSessionStart Start() =>
        new(
            WorkItemId.New(),
            "test-project",
            "work",
            1,
            AgentKind.Claude,
            AgentInstanceId: null,
            ModelId: null,
            ReasoningMode: null,
            SandboxId: "sandbox",
            WorkingDirectory: "/work",
            Source: "test");
}

internal sealed class AgentSupervisionApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-supervision-httptest-{Guid.NewGuid():N}.db");
    private AgentSupervisionOptions _options = new();

    public AgentSupervisionService Supervision { get; }
    public RecordingEndpointSupervisionNotifier Notifier { get; } = new();

    public AgentSupervisionApiFactory()
    {
        Supervision = new AgentSupervisionService(() => _options, Notifier);
    }

    public void SetEnabled(bool enabled) => _options.Enabled = enabled;

    public void SetOptions(AgentSupervisionOptions options) => _options = options;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IAgentSupervisionService>();
            services.AddSingleton<IAgentSupervisionService>(Supervision);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            try { File.Delete(_dbPath); } catch { }
        base.Dispose(disposing);
    }
}

internal sealed class RecordingEndpointSupervisionNotifier : IAgentSupervisionNotifier
{
    public List<AgentSupervisionInjectionEvent> QueuedInjections { get; } = [];

    public Task SessionStartedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task SessionUpdatedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task SessionCompletedAsync(AgentSupervisionSessionSnapshot session, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task CodeyBoxCommandAsync(AgentSupervisionCommandEvent command, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task StdoutChunkAsync(AgentSupervisionStdoutEvent chunk, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task InjectionQueuedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default)
    {
        QueuedInjections.Add(injection);
        return Task.CompletedTask;
    }
    public Task InjectionStartedAsync(AgentSupervisionInjectionEvent injection, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task InjectionCompletedAsync(AgentSupervisionInjectionCompletedEvent injection, CancellationToken ct = default) =>
        Task.CompletedTask;
}
