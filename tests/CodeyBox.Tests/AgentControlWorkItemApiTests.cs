using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class AgentControlWorkItemApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public AgentControlWorkItemApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_AgentControlPause_PersistsAgentControlWorkItem()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "pause claude",
            prompt = "pause claude",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
                durationSeconds = 3600,
            },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id = WorkItemId.Parse(doc.RootElement.GetProperty("id").GetString()!);
        var control = doc.RootElement.GetProperty("agentControl");
        Assert.Equal("pause", control.GetProperty("action").GetString());
        Assert.Equal("claude", control.GetProperty("agent").GetString());
        Assert.Equal("reserve quota", control.GetProperty("reason").GetString());
        Assert.Equal(3600, control.GetProperty("durationSeconds").GetInt32());

        var item = await _factory.Store.GetAsync(id);
        Assert.NotNull(item);
        Assert.Equal(JobType.AgentControl, item!.JobType);
        Assert.Equal(AgentControlAction.Pause, item.AgentControl!.Action);
        Assert.Equal("claude", item.AgentControl.Agent);
        Assert.Equal("reserve quota", item.AgentControl.Reason);
        Assert.Equal(3600, item.AgentControl.DurationSeconds);
    }

    [Fact]
    public async Task Create_AgentControlResume_PersistedItemRunsThroughRealPipeline()
    {
        var pauses = _factory.Services.GetRequiredService<IAgentPauseController>();
        await pauses.PauseAsync(AgentKind.Claude, "existing pause", "test");

        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "resume claude",
            prompt = "resume claude",
            agentControl = new
            {
                action = "resume",
                agent = "claude",
                reason = "maintenance done",
            },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id = WorkItemId.Parse(doc.RootElement.GetProperty("id").GetString()!);
        var item = await _factory.Store.GetAsync(id);
        Assert.NotNull(item);
        Assert.Equal(JobType.AgentControl, item!.JobType);

        var webhooks = new CapturingWebhookDispatcher();
        using var pipeline = TestSupport.BuildAgentControlPipeline(
            _factory.Store,
            pauses,
            webhooks,
            "codeybox-agent-control-api-git-");

        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await _factory.Store.GetAsync(id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(await pauses.GetAgentStateAsync(AgentKind.Claude));
        Assert.Contains(webhooks.Events, e => e.Event == "agent.resumed");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("disable")]
    public async Task Create_AgentControlRejectsNumericOrUnknownActions(string action)
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad control",
            prompt = "bad control",
            agentControl = new
            {
                action,
                agent = "claude",
                reason = "reserve quota",
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_AgentControlValidatesReasonAndExpiry()
    {
        var controlReason = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad reason",
            prompt = "bad reason",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "bad\nreason",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, controlReason.StatusCode);

        var pastExpiry = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad expiry",
            prompt = "bad expiry",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, pastExpiry.StatusCode);
    }

    [Fact]
    public async Task Create_CheckAndAgentControlTogether_IsRejected()
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "mixed controls",
            prompt = "mixed controls",
            check = new
            {
                question = "Proceed?",
                onYes = new
                {
                    title = "Do work",
                    prompt = "Do the work.",
                },
            },
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("check and agentControl", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_AgentControlPause_RequiresReason(string? reason)
    {
        var resp = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "pause without reason",
            prompt = "pause without reason",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason,
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("agentControl.reason is required for pause", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_AgentControlValidatesAgentAndDuration()
    {
        var missingAgent = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "missing agent",
            prompt = "missing agent",
            agentControl = new
            {
                action = "pause",
                agent = "",
                reason = "reserve quota",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingAgent.StatusCode);

        var unknownAgent = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "unknown agent",
            prompt = "unknown agent",
            agentControl = new
            {
                action = "pause",
                agent = "not-real",
                reason = "reserve quota",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownAgent.StatusCode);

        var nonPositiveDuration = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad duration",
            prompt = "bad duration",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
                durationSeconds = 0,
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonPositiveDuration.StatusCode);

        var conflictingDuration = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "conflicting duration",
            prompt = "conflicting duration",
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
                durationSeconds = 60,
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, conflictingDuration.StatusCode);
    }

    [Fact]
    public void AgentControlPipelineFixture_Dispose_RemovesGitRoot()
    {
        var pauses = _factory.Services.GetRequiredService<IAgentPauseController>();
        var pipeline = TestSupport.BuildAgentControlPipeline(
            _factory.Store,
            pauses,
            new CapturingWebhookDispatcher(),
            "codeybox-agent-control-api-git-");
        var gitRoot = pipeline.GitRoot;
        Directory.CreateDirectory(gitRoot);
        File.WriteAllText(Path.Combine(gitRoot, "repo.txt"), "repo");

        pipeline.Dispose();

        Assert.False(Directory.Exists(gitRoot));
    }

}
