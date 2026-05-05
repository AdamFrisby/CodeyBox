using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemDetailPage = CodeyBox.Admin.Web.Components.Pages.WorkItemDetail;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the live stdout panel in <see cref="WorkItemDetailPage"/>.
/// Hub connection is skipped (HubUrl = "") so tests run without a real SignalR server.
/// The tail is delivered via <see cref="FakeApiClient.GetStdoutTailAsync"/>.
/// </summary>
public sealed class LiveStdoutComponentTests : TestContext
{
    public LiveStdoutComponentTests()
    {
        // Empty HubUrl → ConnectToHubAsync exits after fetching tail, no real hub needed.
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    private static WorkItemDto MakeItem(string id, string state) => new()
    {
        Id = id,
        ProjectId = "proj",
        Title = "Live Task",
        Prompt = "the prompt",
        Agent = "claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        QueuePosition = 1,
    };

    // ── Panel visibility ──────────────────────────────────────────────────────

    [Fact]
    public void LiveOutputPanel_ShownForNonTerminalItem()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("live-stdout", cut.Markup);
        Assert.Contains("Live Output", cut.Markup);
    }

    [Fact]
    public void LiveOutputPanel_HiddenForTerminalItemWithNoTail()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.DoesNotContain("live-stdout", cut.Markup);
    }

    [Fact]
    public void LiveOutputPanel_ShownForTerminalItemWithCachedTail()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Done");
        var fake = new FakeApiClient([item]);
        fake.StdoutTailOverride[item.Id] = "some cached output";
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.WaitForState(() => cut.Markup.Contains("some cached output"), TimeSpan.FromSeconds(2));

        Assert.Contains("live-stdout", cut.Markup);
        Assert.Contains("Output Tail", cut.Markup); // terminal items show "Output Tail" label
    }

    // ── Content rendering ─────────────────────────────────────────────────────

    [Fact]
    public void LiveOutputPanel_ShowsTailContentFromApi()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Working");
        var fake = new FakeApiClient([item]);
        fake.StdoutTailOverride[item.Id] = "agent is working...\nsome progress here\n";
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.WaitForState(() => cut.Markup.Contains("agent is working"), TimeSpan.FromSeconds(2));

        Assert.Contains("agent is working", cut.Markup);
        Assert.Contains("some progress here", cut.Markup);
    }

    [Fact]
    public void LiveOutputPanel_EmptyTailFromApi_NothingExtraInOutput()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Working");
        var fake = new FakeApiClient([item]);
        // GetStdoutTailAsync returns null (no entry in override dict)
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        // Panel is visible (non-terminal), but the pre element is empty
        Assert.Contains("live-stdout", cut.Markup);
        Assert.Contains("stdout-stream", cut.Markup);
    }

    // ── Label switching ───────────────────────────────────────────────────────

    [Fact]
    public void LiveOutputPanel_NonTerminalItem_ShowsLiveOutputLabel()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Working");
        var fake = new FakeApiClient([item]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));

        Assert.Contains("Live Output", cut.Markup);
        Assert.DoesNotContain("Output Tail", cut.Markup);
    }

    [Fact]
    public void LiveOutputPanel_TerminalItemWithTail_ShowsOutputTailLabel()
    {
        var item = MakeItem("aabbccdd-0000-0000-0000-000000000001", "Failed");
        var fake = new FakeApiClient([item]);
        fake.StdoutTailOverride[item.Id] = "last known output";
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemDetailPage>(p => p.Add(x => x.Id, item.Id));
        cut.WaitForState(() => cut.Markup.Contains("last known output"), TimeSpan.FromSeconds(2));

        Assert.Contains("Output Tail", cut.Markup);
        Assert.DoesNotContain("Live Output", cut.Markup);
    }
}
