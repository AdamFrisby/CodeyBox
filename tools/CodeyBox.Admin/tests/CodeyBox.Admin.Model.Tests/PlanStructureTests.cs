using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The parser's judgement about whether pasted text is a plan or one
/// prompt with sections, and its promise never to drop text.
/// </summary>
public sealed class PlanStructureTests
{
    [Fact]
    public void NumberedPlan_IsStructured()
    {
        var parsed = PlanChainParser.Parse("1. Alpha\nBody.\n\n2. Beta\nBody.\n\n3. Gamma\nBody.");

        Assert.Equal(3, parsed.Items.Count);
        Assert.Equal(PlanConfidence.Structured, parsed.Confidence);
    }

    [Fact]
    public void LongPromptWithUnnumberedSections_IsOnlySuggested()
    {
        const string prompt = """
            # Fix the flaky login redirect
            ## Context
            Users land back on the login page after authenticating.
            ## Acceptance criteria
            - No redirect loop
            - A regression test
            """;

        var parsed = PlanChainParser.Parse(prompt);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal(PlanConfidence.Suggested, parsed.Confidence);
        Assert.Equal("Context", parsed.Items[0].Title);
        Assert.Contains("Fix the flaky login redirect", parsed.Items[0].Body);
    }

    [Fact]
    public void ProseBeforeNumbering_IsKeptAndOnlySuggested()
    {
        const string prompt = """
            Fix the login bug. Reproduce it first, then:
            1. Add a failing test
            2. Fix the race
            """;

        var parsed = PlanChainParser.Parse(prompt);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal(PlanConfidence.Suggested, parsed.Confidence);
        Assert.Contains("Reproduce it first", parsed.Items[0].Body);
        Assert.Equal("Add a failing test", parsed.Items[0].Title);
    }

    [Fact]
    public void PlanTitleHeadingOverNumberedHeadings_IsStructured()
    {
        const string plan = """
            # Release train
            ## 1. Migrate the database
            Add the column.
            ## 2. Ship the API
            Expose it.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(2, parsed.Items.Count);
        Assert.Equal(PlanConfidence.Structured, parsed.Confidence);
        Assert.Contains("Release train", parsed.Items[0].Body);
    }

    [Fact]
    public void HorizontalRules_SplitItemsAndCountAsStructure()
    {
        const string plan = """
            Build the schema
            Tables and indexes.
            ---
            Build the API
            Endpoints over the schema.
            ***
            Build the UI
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(3, parsed.Items.Count);
        Assert.Equal(PlanConfidence.Structured, parsed.Confidence);
        Assert.Equal("Build the schema", parsed.Items[0].Title);
        Assert.Equal("Build the API", parsed.Items[1].Title);
        Assert.Equal("Build the UI", parsed.Items[2].Title);
        Assert.DoesNotContain("---", parsed.Items[0].Body + parsed.Items[1].Body);
    }

    [Fact]
    public void ExplicitEdges_MakeAPlanStructuredEvenWithoutNumbers()
    {
        const string plan = """
            ## Schema
            Tables.
            ## API
            Depends on: 1
            Endpoints.
            """;

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(PlanConfidence.Structured, parsed.Confidence);
        Assert.True(parsed.HadExplicitEdges);
    }

    [Fact]
    public void FractionAndStepMarkers_SplitItems()
    {
        var parsed = PlanChainParser.Parse("1/3 First\nA.\n2/3 Second\nB.\nStep 3 - Third\nC.");

        Assert.Equal(["First", "Second", "Third"], parsed.Items.Select(i => i.Title));
    }

    [Fact]
    public void SingleItem_HasNoConfidence()
    {
        Assert.Equal(PlanConfidence.None, PlanChainParser.Parse("Just one thing to do.").Confidence);
        Assert.Equal(PlanConfidence.None, PlanChainParser.Parse("1. Only one").Confidence);
    }

    [Fact]
    public void TitleTrailingColon_IsStripped()
    {
        var parsed = PlanChainParser.Parse("1. Migrate:\nDo it.\n\n2. Ship:\nDo that.");

        Assert.Equal("Migrate", parsed.Items[0].Title);
        Assert.Equal("Ship", parsed.Items[1].Title);
    }

    [Fact]
    public void LargeFanOutPlan_IsNotTruncatedAtFifty()
    {
        var plan = string.Join("\n\n", Enumerable.Range(1, 72)
            .Select(n => $"{n}. Item {n}\n" + (n > 3 ? "Depends on: 3" : string.Empty)));

        var parsed = PlanChainParser.Parse(plan);

        Assert.Equal(72, parsed.Items.Count);
        Assert.Equal([3], parsed.Items[71].DependsOn);
    }
}
