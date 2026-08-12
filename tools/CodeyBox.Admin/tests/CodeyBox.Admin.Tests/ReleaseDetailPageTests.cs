using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using ReleaseDetailPage = CodeyBox.Admin.Web.Components.Pages.ReleaseDetail;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the ReleaseDetail component with canned data and verifies that
/// the work item table shows links and states, and that action buttons match
/// the release state.
/// </summary>
public sealed class ReleaseDetailPageTests : BunitContext
{
    private const string ReleaseId = "aaaaaaaa-0000-0000-0000-000000000001";

    private static ReleaseDto MakeRelease(
        string id = ReleaseId,
        string name = "v1.0.0",
        string state = "Open",
        string? branchName = "release/v1.0.0",
        string? failedReason = null) => new(
            Id: id,
            ProjectId: "proj-1",
            Name: name,
            Description: null,
            State: state,
            BranchName: branchName,
            BaseCommitSha: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ClosedAt: null,
            ReviewStartedAt: null,
            ReleasedAt: null,
            FailedReason: failedReason,
            TargetTag: null);

    private static object MakeWorkItemObj(
        string id = "bbbbbbbb-0000-0000-0000-000000000001",
        string title = "Work Item",
        string state = "Done") =>
        new { Id = id, Title = title, State = state };

    // ── Basic rendering ───────────────────────────────────────────────────────

    [Fact]
    public void ReleaseDetail_ShowsReleaseName()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(name: "v2.3.0");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("v2.3.0", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Open");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("state-open", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_ShowsBranchName()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(branchName: "release/feature-y");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("release/feature-y", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_ReleaseNotFound_ShowsError()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = null;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ── Work item table ───────────────────────────────────────────────────────

    [Fact]
    public void ReleaseDetail_WorkItems_RendersTable()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride =
        [
            MakeWorkItemObj("bbbbbbbb-0000-0000-0000-000000000001", "Add tests", "Done"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("queue-table", cut.Markup);
        Assert.Contains("Add tests", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_WorkItems_ShowsShortId()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride =
        [
            MakeWorkItemObj("bbbbbbbb-0000-0000-0000-000000000001", "Fix bug", "Done"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        // Short ID is first 8 chars
        Assert.Contains("bbbbbbbb", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_WorkItems_ShowsStatePerItem()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride =
        [
            MakeWorkItemObj("bbbbbbbb-0000-0000-0000-000000000001", "Item A", "Done"),
            MakeWorkItemObj("cccccccc-0000-0000-0000-000000000001", "Item B", "Failed"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("state-done", cut.Markup);
        Assert.Contains("state-failed", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_WorkItems_LinkToWorkItemDetailPage()
    {
        var wiId = "bbbbbbbb-0000-0000-0000-000000000001";
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride = [MakeWorkItemObj(wiId, "My Task", "Done")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains($"/work-items/{wiId}", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_NoWorkItems_ShowsEmptyMessage()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("No work items in this release", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_MultipleWorkItems_AllTitlesVisible()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease();
        fake.ReleaseWorkItemsOverride =
        [
            MakeWorkItemObj("bbbbbbbb-0000-0000-0000-000000000001", "Auth refactor", "Done"),
            MakeWorkItemObj("cccccccc-0000-0000-0000-000000000001", "DB migration", "Working"),
            MakeWorkItemObj("dddddddd-0000-0000-0000-000000000001", "API tests", "Queued"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("Auth refactor", cut.Markup);
        Assert.Contains("DB migration", cut.Markup);
        Assert.Contains("API tests", cut.Markup);
    }

    // ── Action buttons by state ───────────────────────────────────────────────

    [Fact]
    public void ReleaseDetail_OpenState_ShowsCloseAndAbandonButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Open");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("Close", cut.Markup);
        Assert.Contains("Abandon", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_ClosedState_ShowsTriggerReviewAndReopenButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Closed");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("Trigger Review", cut.Markup);
        Assert.Contains("Reopen", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_FailedState_ShowsReopenButton()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Failed", failedReason: "audit did not converge");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("Reopen", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_FailedState_ShowsFailedReason()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Failed", failedReason: "audit did not converge");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.Contains("audit did not converge", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_ReleasedState_NoActionButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "Released");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.DoesNotContain("Close", cut.Markup);
        Assert.DoesNotContain("Abandon", cut.Markup);
        Assert.DoesNotContain("Trigger Review", cut.Markup);
        Assert.DoesNotContain("Reopen", cut.Markup);
    }

    [Fact]
    public void ReleaseDetail_InReviewState_NoActionButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleaseOverride = MakeRelease(state: "InReview");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<ReleaseDetailPage>(p => p.Add(x => x.Id, ReleaseId));

        Assert.DoesNotContain("Close", cut.Markup);
        Assert.DoesNotContain("Trigger Review", cut.Markup);
        Assert.DoesNotContain("Reopen", cut.Markup);
    }
}
