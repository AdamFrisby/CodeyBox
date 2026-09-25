using CodeyBox.Admin.Model;
using CodeyBox.Composition;

namespace CodeyBox.Admin.Model.Tests;

public sealed class ChainBuilderRootsTests
{
    [Fact]
    public void ExistingDependencies_AttachToEveryRootOnly()
    {
        var drafts = new List<ChainItemDraft>
        {
            new("root a", "", []),
            new("root b", "", []),
            new("child", "", [1, 2]),
        };

        var planned = WorkItemChainBuilder.Build(new ComposerDefaults("p"), drafts, "abcd1234", ["existing-1", " existing-1 ", "existing-2"]);

        Assert.Equal(["existing-1", "existing-2"], planned[0].DependsOn);
        Assert.Equal(["existing-1", "existing-2"], planned[1].DependsOn);
        Assert.Equal(
            [WorkItemChainBuilder.ChainExternalId("abcd1234", 1), WorkItemChainBuilder.ChainExternalId("abcd1234", 2)],
            planned[2].DependsOn);
    }

    [Fact]
    public void TimeoutsAndRefactor_FlowThrough()
    {
        var d = new ComposerDefaults("p", WorkTimeoutMinutes: 90, MergeTimeoutMinutes: 30, IsRefactor: true);

        var planned = WorkItemChainBuilder.Build(d, [new ChainItemDraft("t", "b", [])], "abcd1234");

        Assert.Equal(90, planned[0].WorkTimeoutMinutes);
        Assert.Equal(30, planned[0].MergeTimeoutMinutes);
        Assert.True(planned[0].IsRefactor);
    }

    [Fact]
    public void SingleItem_KeepsOperatorExternalIdAndExistingDeps()
    {
        var planned = WorkItemChainBuilder.Build(
            new ComposerDefaults("p"), [new ChainItemDraft("t", "b", [], "JIRA-9")], "abcd1234", ["x"]);

        Assert.Equal("JIRA-9", planned[0].ExternalId);
        Assert.Equal(["x"], planned[0].DependsOn);
    }
}
