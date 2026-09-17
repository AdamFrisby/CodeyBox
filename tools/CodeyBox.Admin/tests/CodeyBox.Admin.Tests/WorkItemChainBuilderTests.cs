using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Chain builder: a reviewed preview becomes ordered creates whose sibling
/// edges are locally generated external ids, and the preview is exactly
/// what gets created.
/// </summary>
public sealed class WorkItemChainBuilderTests
{
    private static readonly ComposerDefaults Defaults = new(
        ProjectId: "proj-1",
        Agent: "claude",
        BaseBranch: "main",
        Priority: 5,
        MinModelScore: 70);

    [Fact]
    public void Build_ThreeItemChain_EdgesReferenceSiblingExternalIdsInOrder()
    {
        var drafts = new[]
        {
            new ChainItemDraft("One", "Body one", []),
            new ChainItemDraft("Two", "Body two", [1]),
            new ChainItemDraft("Three", "Body three", [1, 2]),
        };

        var built = WorkItemChainBuilder.Build(Defaults, drafts, "a1b2c3d4");

        Assert.Equal(3, built.Count);
        Assert.Equal("cb-chain-a1b2c3d4-01", built[0].ExternalId);
        Assert.Equal("cb-chain-a1b2c3d4-02", built[1].ExternalId);
        Assert.Empty(built[0].DependsOn);
        Assert.Equal(["cb-chain-a1b2c3d4-01"], built[1].DependsOn);
        Assert.Equal(["cb-chain-a1b2c3d4-01", "cb-chain-a1b2c3d4-02"], built[2].DependsOn);
    }

    [Fact]
    public void Build_PreviewIsAccurate_ShownFieldsAreCreatedFields()
    {
        var drafts = new[]
        {
            new ChainItemDraft("  Spaced title  ", "Prompt body", []),
            new ChainItemDraft("Second", "Second body", [1]),
        };

        var built = WorkItemChainBuilder.Build(Defaults, drafts, "feedbeef");

        Assert.Equal("Spaced title", built[0].Title);
        Assert.Equal("Prompt body", built[0].Prompt);
        Assert.Equal("Second body", built[1].Prompt);
        foreach (var item in built)
        {
            Assert.Equal("proj-1", item.ProjectId);
            Assert.Equal("claude", item.Agent);
            Assert.Equal("main", item.BaseBranch);
            Assert.Equal(5, item.Priority);
            Assert.Equal(70, item.MinModelScore);
            Assert.True(item.PushUpstream);
        }
    }

    [Fact]
    public void Build_SingleItem_KeepsOperatorExternalIdAndNoDependsOn()
    {
        var drafts = new[] { new ChainItemDraft("Solo", "Body", [], "JIRA-123") };

        var built = WorkItemChainBuilder.Build(Defaults, drafts, "abcd1234");

        Assert.Single(built);
        Assert.Equal("JIRA-123", built[0].ExternalId);
        Assert.Empty(built[0].DependsOn);
    }

    [Fact]
    public void Build_SingleItemWithoutExternalId_DoesNotInventOne()
    {
        var drafts = new[] { new ChainItemDraft("Solo", "Body", []) };

        var built = WorkItemChainBuilder.Build(Defaults, drafts, "abcd1234");

        Assert.Null(built[0].ExternalId);
    }

    [Fact]
    public void Build_DuplicateAndSelfRefs_DeduplicatedAndDropped()
    {
        var drafts = new[]
        {
            new ChainItemDraft("One", "B1", []),
            new ChainItemDraft("Two", "B2", [1, 1, 2, 99]),
        };

        var built = WorkItemChainBuilder.Build(Defaults, drafts, "abcd1234");

        Assert.Equal(["cb-chain-abcd1234-01"], built[1].DependsOn);
    }

    [Fact]
    public void Build_DistinctChainKeys_ProduceDistinctExternalIds()
    {
        var drafts = new[]
        {
            new ChainItemDraft("One", "B1", []),
            new ChainItemDraft("Two", "B2", [1]),
        };

        var first = WorkItemChainBuilder.Build(Defaults, drafts, "aaaaaaaa");
        var second = WorkItemChainBuilder.Build(Defaults, drafts, "bbbbbbbb");

        Assert.NotEqual(first[0].ExternalId, second[0].ExternalId);
        Assert.DoesNotContain(first[0].ExternalId!, second[1].DependsOn);
    }

    [Fact]
    public void Build_EmptyDrafts_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => WorkItemChainBuilder.Build(Defaults, [], "abcd1234"));
    }

    [Fact]
    public void Build_BlankChainKey_Throws()
    {
        var drafts = new[] { new ChainItemDraft("One", "B1", []) };
        Assert.Throws<ArgumentException>(
            () => WorkItemChainBuilder.Build(Defaults, drafts, "  "));
    }

    [Fact]
    public void ChainExternalId_IsSafeForExternalIdSurface()
    {
        var id = WorkItemChainBuilder.ChainExternalId("A1B2C3D4", 3);

        Assert.Equal("cb-chain-a1b2c3d4-03", id);
        Assert.DoesNotContain(" ", id);
        Assert.False(Guid.TryParse(id, out _));
        Assert.False(id.StartsWith("wi-", StringComparison.OrdinalIgnoreCase));
    }
}
