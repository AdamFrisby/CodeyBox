using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies auto-generated work branch naming for replays:
/// - When the source has a work branch: "&lt;source-branch&gt;-replay-&lt;short-id&gt;"
/// - When the source has no work branch: "replay-&lt;short-id&gt;"
/// - When an explicit work branch is supplied, it is used verbatim.
/// - Two replays of the same source get different auto-generated branches.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReplayWorkBranchDefaultTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReplayWorkBranchDefaultTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem DoneItem(string? workBranch = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "item",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = WorkItemState.Done,
        WorkBranch = workBranch,
    };

    [Fact]
    public async Task Replay_WithSourceWorkBranch_AutoBranchHasSourcePrefix()
    {
        var source = DoneItem("feat/source-branch");
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        Assert.StartsWith("feat/source-branch-replay-", dto!.WorkBranch);
    }

    [Fact]
    public async Task Replay_WithoutSourceWorkBranch_AutoBranchStartsWithReplay()
    {
        var source = DoneItem(workBranch: null);
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        Assert.StartsWith("replay-", dto!.WorkBranch);
    }

    [Fact]
    public async Task Replay_ExplicitWorkBranch_UsedVerbatim()
    {
        var source = DoneItem("feat/original");
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay",
            new { workBranch = "my-custom-branch" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        Assert.Equal("my-custom-branch", dto!.WorkBranch);
    }

    [Fact]
    public async Task TwoReplays_SameSource_GetDifferentAutoBranches()
    {
        var source = DoneItem("feat/base");
        await _factory.Store.CreateAsync(source);

        var resp1 = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        var resp2 = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });

        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, resp2.StatusCode);

        var dto1 = await resp1.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        var dto2 = await resp2.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        Assert.NotEqual(dto1!.WorkBranch, dto2!.WorkBranch);
    }

    [Fact]
    public async Task Replay_AutoBranch_IsValidGitBranchName()
    {
        var source = DoneItem("feat/source");
        await _factory.Store.CreateAsync(source);

        var resp = await _client.PostAsJsonAsync($"/workitems/{source.Id}/replay", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<BranchOnlyResponse>();
        // Valid git branch: first char alphanumeric or forward-slash-preceded; no ".."
        Assert.DoesNotContain("..", dto!.WorkBranch);
        Assert.Matches(@"^[A-Za-z0-9][A-Za-z0-9._/\-]*$", dto.WorkBranch);
    }

    private sealed record BranchOnlyResponse(string? WorkBranch);
}
