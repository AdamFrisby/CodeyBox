using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

public sealed class ComposerInferenceModelTests
{
    private static readonly string[] Known = ["a", "b", "c"];

    [Fact]
    public void ResolveProject_QueryBeatsEverything()
    {
        var (value, source) = ComposerInference.ResolveProject("b", "a", "c", "a", Known);

        Assert.Equal("b", value);
        Assert.Equal(ComposerFieldSource.Query, source);
    }

    [Fact]
    public void ResolveProject_UnknownCandidatesAreSkipped()
    {
        var (value, source) = ComposerInference.ResolveProject("zzz", null, null, "c", Known);

        Assert.Equal("c", value);
        Assert.Equal(ComposerFieldSource.Recent, source);
    }

    [Fact]
    public void ResolveProject_FollowUpBeatsRecent_SuggestionBeatsFollowUp()
    {
        Assert.Equal(("c", ComposerFieldSource.FollowUp), ComposerInference.ResolveProject(null, null, "c", "a", Known));
        Assert.Equal(("a", ComposerFieldSource.Suggestion), ComposerInference.ResolveProject(null, "a", "c", "b", Known));
    }

    [Fact]
    public void ResolveProject_SingleKnownProjectIsTheProject()
    {
        var (value, source) = ComposerInference.ResolveProject(null, null, null, null, ["only"]);

        Assert.Equal("only", value);
        Assert.Equal(ComposerFieldSource.ProjectDefault, source);
        Assert.Equal((null, ComposerFieldSource.None), ComposerInference.ResolveProject(null, null, null, null, Known));
    }

    [Theory]
    [InlineData("Fix the login bug\nmore", "Fix the login bug")]
    [InlineData("\n\n## Migrate the database:\nbody", "Migrate the database")]
    [InlineData("- bullet first", "bullet first")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void DeriveTitle_FirstMeaningfulLineWithoutMarkdown(string? text, string expected)
    {
        Assert.Equal(expected, ComposerInference.DeriveTitle(text));
    }

    [Fact]
    public void DeriveTitle_IsCapped()
    {
        var title = ComposerInference.DeriveTitle(new string('x', 500));

        Assert.Equal(ComposerInference.MaxDerivedTitleLength, title.Length);
    }

    [Fact]
    public void InferredField_OperatorOverrideSurvivesInference_ResetHandsBack()
    {
        var f = InferredField<string>.Empty.ApplyInferred("claude", ComposerFieldSource.ProjectDefault);
        f = f.SetOperator("codex");
        f = f.ApplyInferred("copilot", ComposerFieldSource.ProjectDefault);
        Assert.Equal("codex", f.Value);
        Assert.True(f.Overridden);

        f = f.ResetToInferred("copilot", ComposerFieldSource.ProjectDefault);
        Assert.Equal("copilot", f.Value);
        Assert.False(f.Overridden);
    }
}
