using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that POST /workitems/{id}/replay creates a new work item that is a
/// clone of the source (same prompt, base branch, dependsOn) with the optional
/// agent/model override and a replay_of link back to the source.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReplayCreationTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReplayCreationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem DoneItem(string prompt = "do the thing", string? baseBranch = "main") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "source item",
        Prompt = prompt,
        BaseBranch = baseBranch,
        WorkBranch = "feat/source-branch",
        Agent = AgentKind.Claude,
        State = WorkItemState.Done,
    };

    [Fact]
    public async Task Replay_TerminalItem_CreatesNewWorkItemWithSamePrompt()
    {
        var source = DoneItem("implement feature X");
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(dto);
        Assert.Equal("implement feature X", dto!.Prompt);
    }

    [Fact]
    public async Task Replay_TerminalItem_SameBaseBranch()
    {
        var source = DoneItem(baseBranch: "develop");
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("develop", dto!.BaseBranch);
    }

    [Fact]
    public async Task Replay_TerminalItem_HasReplayOfLinkToSource()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal(source.Id.ToString(), dto!.ReplayOfWorkItemId);
    }

    [Fact]
    public async Task Replay_WithDifferentAgent_NewItemHasOverrideAgent()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay",
            new { agent = "codex" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("codex", dto!.Agent);
    }

    [Fact]
    public async Task Replay_NoAgentOverride_KeepsSourceAgent()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("claude", dto!.Agent);
    }

    [Fact]
    public async Task Replay_NewItemHasDifferentId()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotEqual(source.Id.ToString(), dto!.Id);
    }

    [Fact]
    public async Task Replay_NewItemStartsQueued()
    {
        var source = DoneItem();
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("Queued", dto!.State);
    }

    [Fact]
    public async Task Replay_AllTerminalStates_Succeed()
    {
        foreach (var state in new[] { WorkItemState.Done, WorkItemState.Failed, WorkItemState.AuditFailed, WorkItemState.Cancelled })
        {
            var source = DoneItem() with { State = state };
            await _factory.Store.CreateAsync(source);

            var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
    }

    // ── Eligibility fields (MinModelScore + RequiredCapabilities) ────────────

    [Fact]
    public async Task Replay_CopiesMinModelScoreAndRequiredCapabilitiesFromSource()
    {
        var source = DoneItem() with
        {
            MinModelScore = 95,
            RequiredCapabilities = new[] { "sensitive", "architectural" },
        };
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<EligibilityResponse>();
        Assert.Equal(95, dto!.MinModelScore);
        Assert.Equal(new[] { "sensitive", "architectural" }, dto.RequiredCapabilities);
    }

    // ── Local response shape ─────────────────────────────────────────────────

    private sealed record WorkItemResponse(
        string Id,
        string ProjectId,
        string Title,
        string Prompt,
        string Agent,
        string State,
        string? BaseBranch,
        string? WorkBranch,
        string? ReplayOfWorkItemId,
        IReadOnlyList<string> DependsOn);

    private sealed record EligibilityResponse(
        string Id,
        int MinModelScore,
        IReadOnlyList<string> RequiredCapabilities);
}
