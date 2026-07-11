using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP contract tests for <c>POST /baselines/migrate</c>. The test host's
/// sandbox provider does not implement <see cref="IBaselineImageResolver"/>, so
/// the current-config baseline resolves to null: any non-terminal item with a
/// non-null pin is a migration candidate (recomputing to "no pin"). That is
/// enough to exercise the count, filtering, terminal-exclusion, and idempotency
/// contract end-to-end through the real store and write gate.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class BaselineMigrateEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory;
    private readonly HttpClient _client;

    public BaselineMigrateEndpointTests()
    {
        _factory = new WorkItemApiFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<WorkItem> SeedAsync(
        string? baselineRef, WorkItemState state = WorkItemState.Working, string project = "test-project")
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(project),
            Title = "t",
            Prompt = "x",
            Agent = AgentKind.Claude,
            State = state,
            BaselineImageRef = baselineRef,
        };
        await _factory.Store.CreateAsync(item);
        return item;
    }

    private sealed record MigrateResponse(int Migrated, int Scanned, bool Truncated, RecomputeTarget[] RecomputeTargets);
    private sealed record RecomputeTarget(string? BaselineImageRef, int Count);

    [Fact]
    public async Task Migrate_ClearsNonTerminalPins_AndReturnsCount()
    {
        var working = await SeedAsync("cb-baseline-old", WorkItemState.Working);
        var done = await SeedAsync("cb-baseline-old", WorkItemState.Done);

        var response = await _client.PostAsJsonAsync("/baselines/migrate", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MigrateResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.Migrated);
        Assert.Null((await _factory.Store.GetAsync(working.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-old", (await _factory.Store.GetAsync(done.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Migrate_IsIdempotent_OnSecondCall()
    {
        await SeedAsync("cb-baseline-old", WorkItemState.Working);

        var first = await (await _client.PostAsJsonAsync("/baselines/migrate", new { }))
            .Content.ReadFromJsonAsync<MigrateResponse>();
        var second = await (await _client.PostAsJsonAsync("/baselines/migrate", new { }))
            .Content.ReadFromJsonAsync<MigrateResponse>();

        Assert.Equal(1, first!.Migrated);
        Assert.Equal(0, second!.Migrated);
    }

    [Fact]
    public async Task Migrate_RespectsProjectFilter()
    {
        var inScope = await SeedAsync("cb-baseline-old", WorkItemState.Working, project: "test-project");
        var outOfScope = await SeedAsync("cb-baseline-old", WorkItemState.Working, project: "second-project");

        var body = await (await _client.PostAsJsonAsync(
                "/baselines/migrate", new { projectId = "test-project" }))
            .Content.ReadFromJsonAsync<MigrateResponse>();

        Assert.Equal(1, body!.Migrated);
        Assert.Null((await _factory.Store.GetAsync(inScope.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-old", (await _factory.Store.GetAsync(outOfScope.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Migrate_InvalidProjectId_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/baselines/migrate", new { projectId = "has spaces!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
