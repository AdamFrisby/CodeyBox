using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

public sealed class AggregateTimingsTests : BunitContext
{
    [Fact]
    public void AggregateTimings_ShowsWorkItemCount()
    {
        var fake = new FakeApiClient([]);
        fake.AggregateTimingsOverride = new AggregateTimingsDto
        {
            WorkItemCount = 42,
            StepStats = [],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CodeyBox.Admin.Web.Components.Pages.AggregateTimings>();

        Assert.Contains("42", cut.Markup);
    }

    [Fact]
    public void AggregateTimings_NoData_ShowsEmptyMessage()
    {
        var fake = new FakeApiClient([]);
        fake.AggregateTimingsOverride = new AggregateTimingsDto { WorkItemCount = 0, StepStats = [] };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CodeyBox.Admin.Web.Components.Pages.AggregateTimings>();

        Assert.Contains("No completed timing data", cut.Markup);
    }

    [Fact]
    public void AggregateTimings_WithStats_ShowsTable()
    {
        var fake = new FakeApiClient([]);
        fake.AggregateTimingsOverride = new AggregateTimingsDto
        {
            WorkItemCount = 5,
            StepStats =
            [
                new StepStatDto { Phase = "work", Step = "agent.exec", Count = 5, MedianMs = 8000, P95Ms = 15_000 },
                new StepStatDto { Phase = "work", Step = "git.clone_into_sandbox", Count = 5, MedianMs = 2000, P95Ms = 4000 },
            ],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CodeyBox.Admin.Web.Components.Pages.AggregateTimings>();

        Assert.Contains("aggregate-timings-table", cut.Markup);
        Assert.Contains("agent.exec", cut.Markup);
        Assert.Contains("git.clone_into_sandbox", cut.Markup);
        Assert.Contains("8.0s", cut.Markup);
    }

    [Fact]
    public void AggregateTimings_ShowsRefreshButton()
    {
        var fake = new FakeApiClient([]);
        fake.AggregateTimingsOverride = new AggregateTimingsDto { WorkItemCount = 0, StepStats = [] };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<CodeyBox.Admin.Web.Components.Pages.AggregateTimings>();

        Assert.Contains("Refresh", cut.Markup);
    }
}
