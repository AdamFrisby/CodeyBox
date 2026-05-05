using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using ReleasesPage = CodeyBox.Admin.Web.Components.Pages.Releases;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the Releases list component with canned release data and verifies
/// that state badges and action buttons are appropriate to each release state.
/// </summary>
public sealed class ReleasesPageTests : TestContext
{
    private static ReleaseDto MakeRelease(
        string id = "aaaaaaaa-0000-0000-0000-000000000001",
        string name = "v1.0.0",
        string state = "Open",
        string? branchName = "release/v1.0.0",
        string projectId = "proj-1") => new(
            Id: id,
            ProjectId: projectId,
            Name: name,
            Description: null,
            State: state,
            BranchName: branchName,
            BaseCommitSha: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ClosedAt: null,
            ReviewStartedAt: null,
            ReleasedAt: null,
            FailedReason: null,
            TargetTag: null);

    [Fact]
    public void Releases_EmptyList_ShowsNoReleasesMessage()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("No releases found", cut.Markup);
    }

    [Fact]
    public void Releases_EmptyList_DoesNotRenderTable()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.DoesNotContain("queue-table", cut.Markup);
    }

    [Fact]
    public void Releases_WithItems_RendersTable()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease()];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("queue-table", cut.Markup);
    }

    [Fact]
    public void Releases_ReleaseName_AppearsInRow()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(name: "v2.5.1")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("v2.5.1", cut.Markup);
    }

    [Fact]
    public void Releases_NameLinkPointsToDetailPage()
    {
        var id = "aaaaaaaa-0000-0000-0000-000000000001";
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(id: id, name: "v1.0.0")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains($"/releases/{id}", cut.Markup);
    }

    [Fact]
    public void Releases_OpenState_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Open")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("state-open", cut.Markup);
    }

    [Fact]
    public void Releases_ClosedState_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Closed")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("state-closed", cut.Markup);
    }

    [Fact]
    public void Releases_InReviewState_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "InReview")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("state-inreview", cut.Markup);
    }

    [Fact]
    public void Releases_ReleasedState_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Released")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("state-released", cut.Markup);
    }

    [Fact]
    public void Releases_FailedState_ShowsStateBadge()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Failed")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("state-failed", cut.Markup);
    }

    [Fact]
    public void Releases_OpenRelease_ShowsCloseAndAbandonButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Open")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("close", cut.Markup);
        Assert.Contains("abandon", cut.Markup);
    }

    [Fact]
    public void Releases_OpenRelease_DoesNotShowTriggerReviewButton()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Open")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.DoesNotContain("trigger review", cut.Markup);
    }

    [Fact]
    public void Releases_ClosedRelease_ShowsTriggerReviewAndReopenButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Closed")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("trigger review", cut.Markup);
        Assert.Contains("reopen", cut.Markup);
    }

    [Fact]
    public void Releases_ClosedRelease_DoesNotShowCloseOrAbandonButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Closed")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        // "close" button only shown for open releases
        Assert.DoesNotContain(">close<", cut.Markup);
        Assert.DoesNotContain(">abandon<", cut.Markup);
    }

    [Fact]
    public void Releases_FailedRelease_ShowsReopenButton()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Failed")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("reopen", cut.Markup);
    }

    [Fact]
    public void Releases_ReleasedRelease_ShowsNoActionButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Released")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        // Released state has no action buttons (only view link).
        Assert.DoesNotContain(">close<", cut.Markup);
        Assert.DoesNotContain(">abandon<", cut.Markup);
        Assert.DoesNotContain("trigger review", cut.Markup);
        Assert.DoesNotContain(">reopen<", cut.Markup);
    }

    [Fact]
    public void Releases_BranchName_AppearsInRow()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(branchName: "release/feature-x")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("release/feature-x", cut.Markup);
    }

    [Fact]
    public void Releases_NoBranchName_ShowsDash()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(branchName: null)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("—", cut.Markup);
    }

    [Fact]
    public void Releases_MultipleReleases_AllNamesVisible()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride =
        [
            MakeRelease(id: "aaaaaaaa-0000-0000-0000-000000000001", name: "v1.0.0", state: "Released"),
            MakeRelease(id: "aaaaaaaa-0000-0000-0000-000000000002", name: "v1.1.0", state: "Open"),
            MakeRelease(id: "aaaaaaaa-0000-0000-0000-000000000003", name: "v2.0.0-beta", state: "Closed"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("v1.0.0", cut.Markup);
        Assert.Contains("v1.1.0", cut.Markup);
        Assert.Contains("v2.0.0-beta", cut.Markup);
    }

    [Fact]
    public void Releases_NewReleaseButton_IsAlwaysVisible()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.Contains("New Release", cut.Markup);
    }

    [Fact]
    public void Releases_NewReleaseButton_OpensCreateModal()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();
        cut.Find(".btn-primary").Click();

        Assert.Contains("modal-overlay", cut.Markup);
        Assert.Contains("New Release", cut.Markup);
    }

    [Fact]
    public void Releases_AbandonedRelease_ShowsNoActionButtons()
    {
        var fake = new FakeApiClient([]);
        fake.ReleasesOverride = [MakeRelease(state: "Abandoned")];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<ReleasesPage>();

        Assert.DoesNotContain(">close<", cut.Markup);
        Assert.DoesNotContain(">abandon<", cut.Markup);
        Assert.DoesNotContain("trigger review", cut.Markup);
        Assert.DoesNotContain(">reopen<", cut.Markup);
    }
}
