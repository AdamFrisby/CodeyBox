using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Plan parser over real plan shapes: markdown headings, numbered lists
/// "1." through "7.", explicit "depends on: 2, 4", and structureless prose
/// (which must yield one item, never a bad chain).
/// </summary>
public sealed class PlanChainParserTests
{
    [Fact]
    public void Parse_NumberedListOneThroughSeven_YieldsSevenItemStraightChain()
    {
        var plan = string.Join("\n", Enumerable.Range(1, 7)
            .SelectMany(n => new[] { $"{n}. Step number {n}", $"Do the thing for step {n}." }));

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(7, parsed.Items.Count);
        Assert.False(parsed.HadExplicitEdges);
        Assert.Equal("Step number 1", parsed.Items[0].Title);
        Assert.Contains("Do the thing for step 1", parsed.Items[0].Body);
        Assert.Empty(parsed.Items[0].DependsOn);
        for (var i = 1; i < 7; i++)
        {
            Assert.Equal([i], parsed.Items[i].DependsOn);
        }
    }

    [Fact]
    public void Parse_MarkdownHeadings_YieldsOneItemPerHeading()
    {
        const string plan = """
            # Release train

            ## 1. Migrate the database
            Add the new column and backfill.

            ## 2. Ship the API
            Expose the new endpoint.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal("Migrate the database", parsed.Items[0].Title);
        Assert.Contains("backfill", parsed.Items[0].Body);
        Assert.Equal("Ship the API", parsed.Items[1].Title);
        Assert.Equal([1], parsed.Items[1].DependsOn);
    }

    [Fact]
    public void Parse_ExplicitDependsOn_OverridesStraightLine()
    {
        const string plan = """
            1. Lay the foundation
            Pour concrete.

            2. Raise the walls
            Bricks go here.

            3. Paint everything
            Depends on: 1, 2
            Pick a colour.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(3, parsed.Items.Count);
        Assert.True(parsed.HadExplicitEdges);
        Assert.Empty(parsed.Items[0].DependsOn);
        Assert.Empty(parsed.Items[1].DependsOn);
        Assert.Equal([1, 2], parsed.Items[2].DependsOn);
        Assert.DoesNotContain("Depends on", parsed.Items[2].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pick a colour", parsed.Items[2].Body);
    }

    [Fact]
    public void Parse_DependsOnVariants_RecogniseRequiresAndBlockedBy()
    {
        const string plan = """
            1. First thing
            Body one.

            2. Second thing
            Requires: 1
            Body two.

            3. Third thing
            Blocked by: 1, 2
            Body three.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.True(parsed.HadExplicitEdges);
        Assert.Equal([1], parsed.Items[1].DependsOn);
        Assert.Equal([1, 2], parsed.Items[2].DependsOn);
    }

    [Fact]
    public void Parse_ProseWithNoStructure_YieldsSingleItem()
    {
        const string plan = """
            Fix the flaky login redirect. Users sometimes land back on the
            login page after authenticating; it smells like a race between
            the session write and the redirect. Reproduce it first, then fix.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Single(parsed.Items);
        Assert.False(parsed.HadExplicitEdges);
        Assert.Empty(parsed.Items[0].DependsOn);
        Assert.StartsWith("Fix the flaky login redirect", parsed.Items[0].Title);
        Assert.Contains("Reproduce it first", parsed.Items[0].Body);
    }

    [Fact]
    public void Parse_ParenthesisedNumbersAndStepPrefixes_SplitItems()
    {
        const string plan = """
            1) Alpha
            First body.

            2) Beta
            Second body.

            Step 3: Gamma
            Third body.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(3, parsed.Items.Count);
        Assert.Equal("Alpha", parsed.Items[0].Title);
        Assert.Equal("Beta", parsed.Items[1].Title);
        Assert.Equal("Gamma", parsed.Items[2].Title);
    }

    [Fact]
    public void Parse_BulletsInsideBody_DoNotSplitItems()
    {
        const string plan = """
            1. First item
            - a detail
            - another detail

            2. Second item
            - more detail
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Contains("- a detail", parsed.Items[0].Body);
    }

    [Fact]
    public void Parse_SelfAndForwardRefs_AreDropped()
    {
        const string plan = """
            1. Only item
            Depends on: 1, 5
            Body.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Single(parsed.Items);
        Assert.Empty(parsed.Items[0].DependsOn);
    }

    [Fact]
    public void Parse_EmptyInput_YieldsNoItems()
    {
        Assert.Empty(PlanChainParser.Parse(null).Items);
        Assert.Empty(PlanChainParser.Parse("   \n  ").Items);
    }

    [Fact]
    public void Parse_LongTitle_TruncatesToTitleCap()
    {
        var plan = "1. " + new string('x', 500) + "\nBody.";

        var parsed = PlanChainParser.Parse(plan);

        Assert.Single(parsed.Items);
        Assert.True(parsed.Items[0].Title.Length <= PlanChainParser.MaxTitleLength);
    }
}
