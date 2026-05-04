using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using ComparisonPage = CodeyBox.Admin.Web.Components.Pages.WorkItemComparison;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the WorkItemComparison page with canned data and verifies that the
/// comparison grid is populated correctly. Also verifies that cost and timing
/// cells appear when data is available.
/// </summary>
public sealed class ComparisonPageTests : TestContext
{
    private static WorkItemDto MakeItem(string id, string agent, string state = "Done") => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = "Test item",
        Prompt = "prompt",
        Agent = agent,
        State = state,
        WorkBranch = $"feat/{agent}-branch",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static WorkItemReplaysDto MakeReplays(WorkItemDto source, params WorkItemDto[] replays)
    {
        return new WorkItemReplaysDto
        {
            Source = source,
            Replays = [.. replays],
        };
    }

    [Fact]
    public void ComparisonPage_SourceItem_RendersAgentAndState()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.Contains("claude", cut.Markup);
        Assert.Contains("Done", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_WithOneReplay_ShowsBothColumns()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var replay = MakeItem("aabbccdd-0000-0000-0000-000000000002", "gemini", "Queued");
        var fake = new FakeApiClient([source, replay]);
        fake.ReplaysOverride = MakeReplays(source, replay);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.Contains("claude", cut.Markup);
        Assert.Contains("gemini", cut.Markup);
        Assert.Contains("source", cut.Markup);
        Assert.Contains("replay", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_NotFound_ShowsErrorMessage()
    {
        var fake = new FakeApiClient([]);
        fake.ReplaysOverride = null;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p =>
            p.Add(x => x.Id, "aabbccdd-0000-0000-0000-000000000099"));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComparisonPage_WithTimings_ShowsWallClockRow()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        fake.TimingsOverride[source.Id] = new WorkItemTimingsDto
        {
            WorkItemId = source.Id,
            TotalDurationMs = 45_000,
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.Contains("Wall-clock", cut.Markup);
        Assert.Contains("45.0s", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_WithCosts_ShowsTokenCostRow()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        fake.CostsOverride[source.Id] = new WorkItemCostsDto
        {
            WorkItemId = source.Id,
            Totals = new CostTotalsDto { EstimatedUsd = 0.1234 },
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.Contains("Token cost", cut.Markup);
        Assert.Contains("$0.1234", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_NoTimingsNoCosts_NoWallClockOrCostRows()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.DoesNotContain("Wall-clock", cut.Markup);
        Assert.DoesNotContain("Token cost", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_BackLink_PointsToSource()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        Assert.Contains($"/work-items/{source.Id}", cut.Markup);
    }

    [Fact]
    public void ComparisonPage_ShowsShortIds()
    {
        var source = MakeItem("aabbccdd-0000-0000-0000-000000000001", "claude", "Done");
        var fake = new FakeApiClient([source]);
        fake.ReplaysOverride = MakeReplays(source);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ComparisonPage>(p => p.Add(x => x.Id, source.Id));

        // Short ID (first 8 chars) should appear in the column header
        Assert.Contains("aabbccdd", cut.Markup);
    }
}
