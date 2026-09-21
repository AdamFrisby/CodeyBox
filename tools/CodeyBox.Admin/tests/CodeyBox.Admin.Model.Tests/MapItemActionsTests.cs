using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The map's mutation rules: what an item admits from its state alone, and
/// which nodes a viewport at pipeline zoom needs history for.
/// </summary>
public sealed class MapItemActionsTests
{
    private static IReadOnlyList<string> Keys(string state) => MapItemActions.For(state).Select(a => a.Key).ToList();

    [Theory]
    [InlineData("Queued")]
    [InlineData("Working")]
    [InlineData("Auditing")]
    [InlineData("Reworking")]
    [InlineData("WaitingForQuotaReset")]
    [InlineData("NeedsOperatorInput")]
    public void InFlight_AdmitsDependentAndCancel_NotRetry(string state)
    {
        var keys = Keys(state);
        Assert.Contains("addDependent", keys);
        Assert.Contains("cancel", keys);
        Assert.DoesNotContain("retryWork", keys);
        Assert.DoesNotContain("delegate", keys);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    public void Failed_AdmitsRetriesAndDelegation_NoDependent_NoCancel(string state)
    {
        var keys = Keys(state);
        Assert.Contains("retryWork", keys);
        Assert.Contains("retryAudit", keys);
        Assert.Contains("delegate", keys);
        Assert.DoesNotContain("addDependent", keys); // a dependent of a failure is a dead end
        Assert.DoesNotContain("cancel", keys);
    }

    [Fact]
    public void NeedsOperatorInput_AdmitsAnswerFirst()
    {
        Assert.Equal("answer", Keys("NeedsOperatorInput")[0]);
        Assert.DoesNotContain("answer", Keys("Working"));
        Assert.False(MapItemActions.Answer.Confirm);
    }

    [Fact]
    public void Cancelled_AdmitsRetryOnly()
    {
        Assert.Equal(["retryWork", "retryAudit"], Keys("Cancelled"));
    }

    [Fact]
    public void Done_AdmitsDependentOnly()
    {
        Assert.Equal(["addDependent"], Keys("Done"));
    }

    [Fact]
    public void DestructiveActions_RequireConfirmation()
    {
        Assert.All(MapItemActions.For("Failed"), a => Assert.True(a.Confirm));
        Assert.True(MapItemActions.Cancel.Danger);
        Assert.False(MapItemActions.AddDependent.Confirm);
    }

    [Fact]
    public void VisibleItems_IsEmptyBelowTheBand_AndIsTheViewportsNodesAbove()
    {
        var items = new List<AdminWorkItem>();
        for (var i = 0; i < 30; i++)
        {
            items.Add(Fixtures.Item($"n-{i:D2}"));
        }
        var options = new FleetMapOptions();
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), options);
        var view = new CameraViewSize(1200, 800);
        var origin = layout.Nodes["n-00"];

        var below = SemanticZoom.VisibleItems(layout, new CameraViewport { CenterX = origin.X, CenterY = origin.Y, Zoom = options.OpenZoomStart * 0.9 }, view, options);
        Assert.Empty(below);

        var above = SemanticZoom.VisibleItems(layout, new CameraViewport { CenterX = origin.X, CenterY = origin.Y, Zoom = options.ItemFocusZoom }, view, options);
        Assert.Contains("n-00", above);
        Assert.True(above.Count < items.Count, "only the nodes near the centre are visible at pipeline zoom");
        Assert.True(above.Count <= options.MaxDetailFetch);
        Assert.Equal(above.OrderBy(i => i, StringComparer.Ordinal), above);

        var far = SemanticZoom.VisibleItems(layout, new CameraViewport { CenterX = 1e6, CenterY = 1e6, Zoom = options.ItemFocusZoom }, view, options);
        Assert.Empty(far);
    }
}
