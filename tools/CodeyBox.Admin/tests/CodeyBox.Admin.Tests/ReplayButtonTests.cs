using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemDetailPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDetail;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Verifies that the "Replay" button on the work-item-detail page is only shown
/// for terminal items (Done, Failed, AuditFailed, Cancelled) and hidden for
/// non-terminal states. Also verifies that the comparison page link appears
/// alongside the replay button.
/// </summary>
public sealed class ReplayButtonTests : TestContext
{
    public ReplayButtonTests()
    {
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    private static WorkItemDto MakeItem(string id, string state) => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = "Test item",
        Prompt = "prompt",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("Cancelled")]
    public void WorkItemDetail_TerminalItem_ShowsReplayButton(string state)
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", state);
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Replay", cut.Markup);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Working")]
    [InlineData("WorkComplete")]
    [InlineData("Auditing")]
    [InlineData("Merging")]
    public void WorkItemDetail_NonTerminalItem_DoesNotShowReplayButton(string state)
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", state);
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        // "Retry" may appear, but the "Replay" agent-comparison button should not.
        // The Timeline link also contains no "Replay" text now that we renamed it.
        Assert.DoesNotContain("Replay", cut.Markup);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    public void WorkItemDetail_TerminalItem_ShowsComparisonLink(string state)
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", state);
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("/comparison", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_NonTerminalItem_DoesNotShowComparisonLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.DoesNotContain("/comparison", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_TerminalItem_ShowsTimelineButton()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Timeline", cut.Markup);
        Assert.Contains("/timeline", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_WithReplayOf_ShowsReplayOfLink()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done");
        item.ReplayOfWorkItemId = "aabbccdd-0000-0000-0000-000000000099";
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Replay of", cut.Markup);
        Assert.Contains("aabbccdd", cut.Markup);
    }

    [Fact]
    public void WorkItemDetail_ClickReplayButton_OpensModal()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        // Find and click the Replay button (it's a <button>, not a link)
        var replayBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Replay"));
        Assert.NotNull(replayBtn);
        replayBtn!.Click();

        Assert.Contains("modal-overlay", cut.Markup);
        Assert.Contains("Agent", cut.Markup);
    }
}
