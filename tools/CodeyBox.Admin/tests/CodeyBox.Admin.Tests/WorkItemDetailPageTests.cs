using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemDetailPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDetail;

namespace CodeyBox.Admin.Tests;

public sealed class WorkItemDetailPageTests : TestContext
{
    public WorkItemDetailPageTests()
    {
        // OrchestratorHubSettings is injected by WorkItemDetail; empty URL skips the live hub connection.
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    private static WorkItemDto MakeItem(string id, string title, string state = "Queued") => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = title,
        Prompt = "Some prompt text",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    [Fact]
    public void WorkItemDetail_ShowsTitle()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "My Work Item");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("My Work Item", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_ShowsPromptInCollapsible()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Some prompt text", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_QueuedItem_ShowsEditLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Queued Task", "Queued");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("edit", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemDetail_DoneItem_DoesNotShowEditLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done Task", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.DoesNotContain("/edit", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_FailedItem_ShowsRetryButtons()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Failed Task", "Failed");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Retry", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_NotFound_ShowsErrorMessage()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p =>
            p.Add(x => x.Id, "aabbccdd-0000-0000-0000-000000000099"));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemDetail_DoesNotLoadTwice_WhenIdUnchanged()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        // Re-render with the same Id — LoadAsync should not fire again.
        cut.SetParametersAndRender(p => p.Add(x => x.Id, item.Id));

        // GetWorkItemAsync was called exactly once, not twice.
        Assert.Equal(1, fake.GetWorkItemCallCount);
    }

    [Fact]
    public void WorkItemDetail_ShowsReplayButton_ForAnyItem()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Task", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Replay", cut.Markup);
        Assert.Contains("/timeline", cut.Markup);
    }
}
