using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using MajordomoPanelComponent = CodeyBox.Admin.Web.Components.Shared.MajordomoPanel;
using FleetMapPage = CodeyBox.Admin.Web.Components.Pages.FleetMap;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Majordomo panel tests: the conversation streams new output without a
/// reload, the autonomy switch is legible and effective, approve/reject
/// review proposals with a clear change statement, stale approvals surface
/// their refusal reason, and work items render by title with a link — never
/// as a bare id. Plus the FleetMap layout test proving the panel resizes
/// the canvas instead of overlaying it.
/// </summary>
public sealed class MajordomoPanelTests : BunitContext
{
    private sealed class FixedMapOptions : IOptionsMonitor<FleetMapOptions>
    {
        public FleetMapOptions CurrentValue { get; } = new();
        public FleetMapOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<FleetMapOptions, string?> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private const string KnownId = "11111111111111111111111111111111";
    private const string KnownTitle = "Known Title";

    private static WorkItemDto Item(string id, string title, string state = "Queued") => new()
    {
        Id = id,
        ProjectId = "proj-1",
        Prompt = $"Prompt for {title}",
        Title = title,
        Agent = "Claude",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private static MajordomoConversationEntryDto Entry(long sequence, string role, string text, string? tool = null) => new()
    {
        Sequence = sequence,
        Role = role,
        Text = text,
        ToolName = tool,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    private static JsonElement ChangeSetForCancel(string id) => JsonDocument.Parse($$"""
        {
          "dry_run": true,
          "changes": [
            { "kind": "create_item", "item": { "project_id": "proj-1", "title": "Do the thing", "prompt": "do it" } },
            { "kind": "cancel_item", "id": "{{id}}", "reason": "no longer needed" }
          ],
          "affected_items": []
        }
        """).RootElement.Clone();

    private static MajordomoProposalDto Proposal(string id) => new()
    {
        Id = id,
        Tool = "cancel_work_item",
        Reasoning = "The fleet moved on without it.",
        ReviewedChangeSet = ChangeSetForCancel(KnownId),
        ProposedBy = "majordomo",
        ProposedAt = DateTimeOffset.UtcNow,
        State = "pending",
    };

    private MajordomoPanelTests Setup(FakeApiClient fake)
    {
        Services.AddSingleton<ICodeyBoxApiClient>(fake);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return this;
    }

    private IRenderedComponent<MajordomoPanelComponent> RenderPanel(
        FakeApiClient fake, Func<string, string?>? titles = null, Action? onFleetChanged = null)
    {
        Setup(fake);
        return Render<MajordomoPanelComponent>(parameters => parameters
            .Add(p => p.TitleFor, titles ?? (_ => null))
            .Add(p => p.OnFleetChanged,
                onFleetChanged is null ? default : EventCallback.Factory.Create(this, onFleetChanged)));
    }

    [Fact]
    public async Task Panel_StreamsNewOutput_OnRefresh()
    {
        var fake = new FakeApiClient([]);
        fake.ConversationOverride.Add(Entry(1, "majordomo", "First turn."));
        var cut = RenderPanel(fake);

        Assert.Contains("First turn.", cut.Markup);

        fake.ConversationOverride.Add(Entry(2, "toolcall", """{"id": "x"}""", "get_queue_status"));
        await cut.InvokeAsync(() => cut.Instance.RefreshAllAsync());
        cut.Render();

        Assert.Contains("First turn.", cut.Markup);
        Assert.Contains("get_queue_status", cut.Markup);
    }

    [Fact]
    public void Panel_ModeSwitch_IsLegibleAndEffective()
    {
        var fake = new FakeApiClient([]);
        var cut = RenderPanel(fake);

        // Both modes are visible without opening a menu; the current one is pressed.
        Assert.Contains("Proposed", cut.Markup);
        Assert.Contains("Autonomous", cut.Markup);
        Assert.Contains("reporting for review", cut.Markup);

        var autonomous = cut.FindAll("button").First(b => b.TextContent.Contains("Autonomous"));
        autonomous.Click();
        cut.WaitForAssertion(() => Assert.Contains("acting now", cut.Markup));

        Assert.Equal(["autonomous"], fake.MajordomoModeSets);
    }

    [Fact]
    public void Panel_Send_AppendsOperatorMessage()
    {
        var fake = new FakeApiClient([]);
        var cut = RenderPanel(fake);

        cut.Find("#mj-input").Input("What is stuck?");
        cut.Find(".mj-compose").Submit();
        cut.WaitForAssertion(() => Assert.Contains("What is stuck?", cut.Markup));

        Assert.Equal(["What is stuck?"], fake.SentMessages);
    }

    [Fact]
    public void Panel_ToolCalls_RenderInline()
    {
        var fake = new FakeApiClient([]);
        fake.ConversationOverride.Add(Entry(1, "toolcall", """{"project_id": "proj-1"}""", "list_work_items"));
        fake.ConversationOverride.Add(Entry(2, "toolresult", "executed: 2 items", "list_work_items"));
        var cut = RenderPanel(fake);

        Assert.Contains("list_work_items", cut.Markup);
        Assert.Contains("called", cut.Markup);
        Assert.Contains("returned", cut.Markup);
    }

    [Fact]
    public void Panel_Approve_RemovesProposalStatesChangeAndNotifiesMap()
    {
        var fake = new FakeApiClient([]);
        fake.ProposalsOverride.Add(Proposal("proposal-1"));
        var fleetChanged = 0;
        var cut = RenderPanel(fake, id => id == KnownId ? KnownTitle : null, () => fleetChanged++);

        Assert.Contains("The fleet moved on without it.", cut.Markup);
        Assert.Contains("Create “Do the thing”", cut.Markup);
        Assert.Contains("Cancel “Known Title”", cut.Markup);

        cut.Find(".mj-approve").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("proposal-1", cut.Markup));

        Assert.Equal(["proposal-1"], fake.ApprovedProposalIds);
        Assert.Equal(1, fleetChanged);
    }

    [Fact]
    public void Panel_Reject_SendsReasonAndRemovesProposal()
    {
        var fake = new FakeApiClient([]);
        fake.ProposalsOverride.Add(Proposal("proposal-9"));
        var cut = RenderPanel(fake);

        cut.Find(".mj-reject-reason").Change("still needed");
        cut.Find(".mj-reject").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("proposal-9", cut.Markup));

        var rejected = Assert.Single(fake.RejectedProposals);
        Assert.Equal("proposal-9", rejected.Id);
        Assert.Equal("still needed", rejected.Reason);
    }

    [Fact]
    public void Panel_StaleApprove_SurfacesRefusalReasonAndKeepsProposal()
    {
        var fake = new FakeApiClient([]);
        fake.ProposalsOverride.Add(Proposal("proposal-stale"));
        fake.DecideHook = _ => new MajordomoProposalDecisionDto
        {
            Ok = false,
            Status = "refused",
            Reason = "proposal_drifted",
            Detail = "Cancel target has since completed.",
        };
        var cut = RenderPanel(fake);

        cut.Find(".mj-approve").Click();
        cut.WaitForAssertion(() => Assert.Contains("proposal_drifted", cut.Markup));

        Assert.Contains("Cancel target has since completed.", cut.Markup);
        Assert.Contains("proposal-stale", cut.Markup);
        Assert.Empty(fake.ApprovedProposalIds);
    }

    [Fact]
    public void Panel_TurnReferencingWorkItem_RendersTitleLinkNeverBareId()
    {
        var fake = new FakeApiClient([]);
        fake.ConversationOverride.Add(Entry(1, "majordomo", $"Working on {KnownId} now."));
        var cut = RenderPanel(fake, id => id == KnownId ? KnownTitle : null);

        Assert.Contains($"/work-items/{KnownId}", cut.Markup);
        Assert.Contains(KnownTitle, cut.Markup);
        Assert.DoesNotContain($">{KnownId}<", cut.Markup);
    }

    [Fact]
    public void Panel_UnknownGuids_RenderUnchanged()
    {
        // Proposal ids and session ids are GUID-shaped but are not work
        // items: linking them to the work-item page would lie.
        var fake = new FakeApiClient([]);
        const string other = "22222222222222222222222222222222";
        fake.ConversationOverride.Add(Entry(1, "majordomo", $"See proposal {other}."));
        var cut = RenderPanel(fake);

        cut.WaitForAssertion(() => Assert.Contains(other, cut.Markup));
        Assert.DoesNotContain($"/work-items/{other}", cut.Markup);
    }

    [Fact]
    public void Linker_MapsOnlyResolvedIds()
    {
        var segments = MajordomoItemLinker.Linkify(
            $"a {KnownId} b 33333333-3333-3333-3333-333333333333 c",
            id => id == KnownId ? KnownTitle : null);

        var link = Assert.Single(segments, s => s.IsLink);
        Assert.Equal(KnownTitle, link.Text);
        Assert.Equal(KnownId, link.WorkItemId);
        Assert.DoesNotContain(segments, s => s.IsLink && s.WorkItemId!.Contains('-'));
    }

    [Fact]
    public void Summarizer_FallsBackToToolName_ForUnknownShape()
    {
        var proposal = Proposal("p1");
        proposal.ReviewedChangeSet = JsonDocument.Parse("""{"changes": [{"kind": "frobnicate"}]}""").RootElement.Clone();

        var lines = MajordomoChangeSummarizer.Summarize(proposal, _ => null);

        Assert.Equal(["Run frobnicate"], lines);
    }

    [Fact]
    public void FleetMap_PanelOpen_ResizesCanvasInsteadOfOverlaying()
    {
        var fake = new FakeApiClient([Item("a1", "Work a1", "Working")]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);
        Services.AddSingleton<IOptionsMonitor<FleetMapOptions>>(new FixedMapOptions());
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<FleetMapPage>();

        // The panel is a flex sibling of the canvas inside the stage — the
        // canvas wrap keeps its in-flow position and the rail stays docked.
        var stage = cut.Find(".fm-stage");
        var side = stage.QuerySelector("#fleet-majordomo-panel.fm-side");
        Assert.NotNull(side);
        Assert.NotNull(stage.QuerySelector("#fleet-map-wrap"));
        Assert.NotNull(stage.QuerySelector("#fleet-map-rail"));
        Assert.Null(cut.FindAll(".fm-overlay--modal").FirstOrDefault());
        Assert.DoesNotContain("fm-inspector", cut.Markup);

        var toggle = cut.FindAll("button").First(b => b.TextContent.Contains("Majordomo"));
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));

        // Closing removes the sibling (the canvas takes the width back);
        // reopening restores it. The canvas is never covered.
        toggle.Click();
        cut.WaitForAssertion(() => Assert.Equal("false",
            cut.FindAll("button").First(b => b.TextContent.Contains("Majordomo")).GetAttribute("aria-expanded")));
        Assert.Empty(cut.FindAll("#fleet-majordomo-panel"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Majordomo")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#fleet-majordomo-panel")));
        Assert.NotNull(cut.Find("#fleet-map-wrap"));
    }
}
