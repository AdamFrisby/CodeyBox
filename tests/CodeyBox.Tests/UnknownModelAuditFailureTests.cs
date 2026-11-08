using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the audit fast-path that distinguishes "the agent CLI does not
/// know the requested model" from a generic review-agent exit 1. The former
/// is a deterministic configuration fault (stale baked CLI after a CLI/model
/// upgrade): it must surface immediately with that diagnosis instead of
/// burning the fresh-sandbox retry and failing as an opaque audit error.
/// </summary>
public sealed class UnknownModelAuditFailureTests
{
    [Theory]
    [InlineData("Error: unknown model 'gemini-4-argon-high'")]
    [InlineData("model 'gemini-4-argon-high' not found in the model list")]
    public void UnknownModel_BuildsDeterministicFailure(string stderr)
    {
        var failure = PipelineRunner.BuildUnknownModelFailure(
            "security:llm-review", "antigravity", "gemini-4-argon-high",
            stderr, stdout: null, summary: "agent exited 1");

        Assert.NotNull(failure);
        Assert.True(failure!.IsDeterministic);
        Assert.Contains("does not know the requested model", failure.Message);
        Assert.Contains("gemini-4-argon-high", failure.Message);
        Assert.Contains("configuration fault", failure.Message);
    }

    [Fact]
    public void NoUnknownModel_ReturnsNull()
    {
        var failure = PipelineRunner.BuildUnknownModelFailure(
            "security:llm-review", "antigravity", "gemini-4-argon-high",
            "some other agent crash", stdout: null, summary: "agent exited 1");

        Assert.Null(failure);
    }

    [Fact]
    public void NullModelId_ReportedAsDefault()
    {
        var failure = PipelineRunner.BuildUnknownModelFailure(
            "security:llm-review", "antigravity", modelId: null,
            "Error: unknown model 'x'", stdout: null, summary: null);

        Assert.NotNull(failure);
        Assert.Contains("(default)", failure!.Message);
    }
}
