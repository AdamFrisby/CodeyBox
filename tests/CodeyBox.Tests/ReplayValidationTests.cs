using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that POST /workitems/{id}/replay enforces its preconditions:
/// - 404 for an unknown source ID
/// - 400 when the source is not in a terminal state
/// - 400 for an unknown agent kind
/// - 400 for an invalid work branch name
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReplayValidationTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReplayValidationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem Item(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "item",
        Prompt = "do stuff",
        Agent = AgentKind.Claude,
        State = state,
    };

    // ── 404 for unknown source ────────────────────────────────────────────────

    [Fact]
    public async Task Replay_UnknownSourceId_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{Guid.NewGuid()}/replay", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Replay_InvalidIdFormat_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(
            "/workitems/not-a-guid/replay", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 400 for non-terminal source ───────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.Merged)]
    [InlineData(WorkItemState.UpstreamPushing)]
    public async Task Replay_NonTerminalSource_Returns400(WorkItemState state)
    {
        var source = Item(state);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 400 for unknown agent ─────────────────────────────────────────────────

    [Fact]
    public async Task Replay_UnknownAgent_Returns400()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay",
            new { agent = "definitely-not-an-agent" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 400 for invalid work branch ───────────────────────────────────────────

    [Fact]
    public async Task Replay_WorkBranchWithLeadingDash_Returns400()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay",
            new { workBranch = "-invalid-branch" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Replay_WorkBranchSameAsBaseBranch_Returns400()
    {
        var source = Item(WorkItemState.Done) with { BaseBranch = "main" };
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay",
            new { workBranch = "main" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Replay_WorkBranchWithDoubleDot_Returns400()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay",
            new { workBranch = "feat..bad" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 400 when modelId is set ───────────────────────────────────────────────

    [Fact]
    public async Task Replay_ModelIdProvided_Returns400()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay",
            new { modelId = "gemini-3.0-pro" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Valid replay succeeds ─────────────────────────────────────────────────

    [Fact]
    public async Task Replay_ValidTerminalSource_Returns201()
    {
        var source = Item(WorkItemState.Done);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
