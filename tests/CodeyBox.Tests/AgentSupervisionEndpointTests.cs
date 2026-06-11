using System.Net;
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
    private readonly AgentSupervisionOptions _options = new();

    public AgentSupervisionService Supervision { get; }

    public AgentSupervisionApiFactory()
    {
        Supervision = new AgentSupervisionService(() => _options);
    }

    public void SetEnabled(bool enabled) => _options.Enabled = enabled;

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
