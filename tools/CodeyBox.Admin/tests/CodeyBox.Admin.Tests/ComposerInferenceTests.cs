using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Inference contract: every inferred field accepts an override, and once
/// overridden, later inference (project switches included) leaves it alone
/// until the operator hands it back.
/// </summary>
public sealed class ComposerInferenceTests
{
    [Fact]
    public void InferredField_ApplyInferred_UpdatesValueAndSource()
    {
        var field = InferredField<string>.Empty.ApplyInferred("claude", ComposerFieldSource.ProjectDefault);

        Assert.Equal("claude", field.Value);
        Assert.Equal(ComposerFieldSource.ProjectDefault, field.Source);
        Assert.False(field.Overridden);
    }

    [Fact]
    public void InferredField_OverrideSurvivesReInference()
    {
        var field = InferredField<string>.Empty
            .ApplyInferred("claude", ComposerFieldSource.ProjectDefault)
            .SetOperator("codex")
            .ApplyInferred("copilot", ComposerFieldSource.ProjectDefault);

        Assert.Equal("codex", field.Value);
        Assert.Equal(ComposerFieldSource.Operator, field.Source);
        Assert.True(field.Overridden);
    }

    [Fact]
    public void InferredField_EachFieldOverridesIndependently()
    {
        var agent = InferredField<string>.Empty
            .ApplyInferred("claude", ComposerFieldSource.ProjectDefault)
            .SetOperator("codex");
        var branch = InferredField<string>.Empty
            .ApplyInferred("main", ComposerFieldSource.ProjectDefault);

        agent = agent.ApplyInferred("copilot", ComposerFieldSource.Query);
        branch = branch.ApplyInferred("develop", ComposerFieldSource.Query);

        Assert.Equal("codex", agent.Value);
        Assert.Equal("develop", branch.Value);
        Assert.Equal(ComposerFieldSource.Query, branch.Source);
    }

    [Fact]
    public void InferredField_ResetToInferred_HandsBackControl()
    {
        var field = InferredField<string>.Empty
            .ApplyInferred("claude", ComposerFieldSource.ProjectDefault)
            .SetOperator("codex")
            .ResetToInferred("copilot", ComposerFieldSource.Query)
            .ApplyInferred("gemini", ComposerFieldSource.ProjectDefault);

        Assert.Equal("gemini", field.Value);
        Assert.False(field.Overridden);
    }

    [Fact]
    public void ResolveText_QueryBeatsProjectDefault()
    {
        var (value, source) = ComposerInference.ResolveText(" codex ", "claude");

        Assert.Equal("codex", value);
        Assert.Equal(ComposerFieldSource.Query, source);
    }

    [Fact]
    public void ResolveText_BlankQueryFallsBackToProjectDefault()
    {
        var (value, source) = ComposerInference.ResolveText("  ", "main");

        Assert.Equal("main", value);
        Assert.Equal(ComposerFieldSource.ProjectDefault, source);
    }

    [Fact]
    public void ResolveText_BlankEverywhere_IsUnset()
    {
        var (value, source) = ComposerInference.ResolveText(null, " ");

        Assert.Null(value);
        Assert.Equal(ComposerFieldSource.None, source);
    }

    [Fact]
    public void ResolveAuditBudget_NonPositiveProjectDefault_IsUnset()
    {
        var (value, source) = ComposerInference.ResolveAuditBudget(null, 0);

        Assert.Null(value);
        Assert.Equal(ComposerFieldSource.None, source);
    }

    [Fact]
    public void ResolveAuditBudget_QueryOverrideWins()
    {
        var (value, source) = ComposerInference.ResolveAuditBudget(3, 8);

        Assert.Equal(3, value);
        Assert.Equal(ComposerFieldSource.Query, source);
    }
}
