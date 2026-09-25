using CodeyBox.Admin.Model;
using CodeyBox.Composition;
using ComposerComposition = CodeyBox.Composition.Composition;

namespace CodeyBox.Admin.Model.Tests;

public sealed class CompositionReviewTests
{
    private static ChainItemDraft Item(string title, params int[] deps) => new(title, "body", deps);

    private static ComposerComposition Single(ComposerDefaults? defaults = null, string? externalId = null) =>
        new(defaults ?? new ComposerDefaults("proj"), [new ChainItemDraft("T", "b", [], externalId)], []);

    [Fact]
    public void Validate_CleanComposition_HasNoProblems()
    {
        Assert.Empty(CompositionReview.Validate(Single()));
    }

    [Fact]
    public void Validate_MissingProjectTitleAndItems()
    {
        var empty = new ComposerComposition(new ComposerDefaults(""), [], []);
        var problems = CompositionReview.Validate(empty);

        Assert.Contains(problems, p => p.Contains("Pick a project"));
        Assert.Contains(problems, p => p.Contains("nothing to file"));

        var untitled = new ComposerComposition(new ComposerDefaults("p"), [Item(""), Item("ok", 1)], []);
        Assert.Contains(CompositionReview.Validate(untitled), p => p.Contains("Item 1 needs a title"));
    }

    [Fact]
    public void Validate_ServerBandsAreMirrored()
    {
        var d = new ComposerDefaults("p", Priority: 1001, AuditMaxIterations: 0, MinModelScore: 201,
            WorkTimeoutMinutes: 0, MergeTimeoutMinutes: -1, AuditComplexity: new string('x', 65));
        var problems = CompositionReview.Validate(Single(d));

        Assert.Contains(problems, p => p.Contains("Priority must be between -1000 and 1000"));
        Assert.Contains(problems, p => p.Contains("Audit budget must be between 1 and 100"));
        Assert.Contains(problems, p => p.Contains("Min model score must be between 0 and 200"));
        Assert.Contains(problems, p => p.Contains("Work timeout"));
        Assert.Contains(problems, p => p.Contains("Merge timeout"));
        Assert.Contains(problems, p => p.Contains("Audit complexity"));
    }

    [Fact]
    public void Validate_CycleIsNamed()
    {
        var c = new ComposerComposition(new ComposerDefaults("p"), [Item("a", 2), Item("b", 1), Item("c", 2)], []);

        Assert.Contains(CompositionReview.Validate(c), p => p.Contains("Items 1, 2 wait for each other"));
    }

    [Fact]
    public void Validate_RefactorChainIsRefused()
    {
        var c = new ComposerComposition(new ComposerDefaults("p", IsRefactor: true), [Item("a"), Item("b", 1)], []);

        Assert.Contains(CompositionReview.Validate(c), p => p.Contains("refactor is project-exclusive"));
    }

    [Theory]
    [InlineData("JIRA-123", null)]
    [InlineData("wi-abc", "reserved")]
    [InlineData("has space", "no whitespace")]
    [InlineData("a/b", "no whitespace")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "UUID")]
    public void ExternalIdProblem_MirrorsOrchestratorRules(string id, string? expectedFragment)
    {
        var problem = CompositionReview.ExternalIdProblem(id);

        if (expectedFragment is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(expectedFragment, problem);
        }
    }

    [Fact]
    public void Describe_AllDefaults_SaysSo()
    {
        var sentence = CompositionReview.Describe(Single(), "CodeyBox");

        Assert.Equal("Files 1 item into CodeyBox with project defaults.", sentence);
    }

    [Fact]
    public void Describe_ChainWithSettings_ReadsEverythingNonDefault()
    {
        var d = new ComposerDefaults("p", Agent: "claude", Priority: 5, AuditMaxIterations: 8,
            AuditorProfile: "strict", RequiredCapabilities: ["gpu"],
            Knobs: new Dictionary<string, string> { ["changeScope"] = "surgical" }, PushUpstream: false);
        var c = new ComposerComposition(d, [Item("a"), Item("b", 1), Item("c", 1)], ["11111111-aaaa"]);

        var sentence = CompositionReview.Describe(c, "Proj", id => "ticket " + id[..4]);

        Assert.StartsWith("Files 3 items into Proj as a chain (1, then 2–3 in parallel after 1)", sentence);
        Assert.Contains("agent claude", sentence);
        Assert.Contains("needs gpu", sentence);
        Assert.Contains("no upstream push", sentence);
        Assert.Contains("audit ×8 (strict)", sentence);
        Assert.Contains("priority 5", sentence);
        Assert.Contains("changeScope=surgical", sentence);
        Assert.Contains("the chain's roots wait for ticket 1111", sentence);
    }

    [Fact]
    public void Describe_ClassBeatsAgentInTheSentence()
    {
        var d = new ComposerDefaults("p", Agent: "claude", AgentClassId: "fast");
        var sentence = CompositionReview.Describe(Single(d), null);

        Assert.Contains("class fast", sentence);
        Assert.DoesNotContain("agent claude", sentence);
    }
}
