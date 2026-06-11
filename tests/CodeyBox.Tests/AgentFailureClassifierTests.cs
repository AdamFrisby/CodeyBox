using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentFailureClassifier"/> and the default
/// <see cref="IAgentRunner.ClassifyFailure"/> implementation. Mid-iteration
/// quota fallback depends on this classification — a mis-classified work
/// failure as <see cref="AgentFailureKind.QuotaExhausted"/> would burn through
/// every class member on a task no agent can complete; the inverse would
/// silently fail items that a fallback could have rescued.
/// </summary>
public sealed class AgentFailureClassifierTests
{
    [Theory]
    [InlineData("API Error: rate_limit_exceeded")]
    [InlineData("rate limit exceeded")]
    [InlineData("[error] usage_limit reached: weekly cap")]
    [InlineData("hit your usage limit")]
    [InlineData("RESOURCE_EXHAUSTED")]
    [InlineData("quota exceeded for project")]
    [InlineData("[API Error: You have exhausted your capacity on this model.]")]
    [InlineData("status 429 too many requests")]
    [InlineData("HTTP 529")]
    public void QuotaPatterns_Classified_AsQuotaExhausted(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Theory]
    [InlineData("compile error: missing semicolon at line 42")]
    [InlineData("test failures: 3/100 assertions failed")]
    [InlineData("agent refused: cannot perform this task")]
    [InlineData("ENOENT: no such file 'foo.txt'")]
    public void NormalFailures_NotClassified_AsQuota(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    [Theory]
    [InlineData("API Error: 401 Unauthorized")]
    [InlineData("invalid_api_key supplied")]
    [InlineData("OAuth token expired; please reauthenticate")]
    public void AuthPatterns_Classified_AsAuthError(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
    }

    [Theory]
    [InlineData("ECONNRESET while contacting api.anthropic.com")]
    [InlineData("Temporary failure in name resolution")]
    [InlineData("503 Service Unavailable")]
    [InlineData("socket hang up")]
    [InlineData("fetch failed")]
    public void NetworkPatterns_Classified_AsTransient(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
    }

    [Theory]
    [InlineData("agent exited 127", "env: 'agy': No such file or directory")]
    [InlineData("agent exited 127", "bash: codex: command not found")]
    [InlineData("agent exited 127", "")]
    [InlineData("exit 127", "command not found")]
    [InlineData("agentic conflict resolution failed: agent exited 127 (attempts: codex#1(agent exited 127))", "env: 'codex': No such file or directory")]
    public void Exit127BinaryLaunchFailures_Classified_AsInfrastructure(string summary, string stderr)
    {
        var c = AgentFailureClassifier.Classify(stderr: stderr, summary: summary);
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Fact]
    public void Exit127BinaryLaunchFailure_InStdout_Classified_AsInfrastructure()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: null,
            stdout: "bash: codex: command not found",
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    // Realistic non-binary filesystem ENOENT shapes that the broad
    // "No such file or directory" pattern used to swallow. The Node.js fs
    // syscall message and the GNU open-file ENOENT both carry the directory
    // suffix verbatim, so a regression that re-introduced the broad pattern
    // would silently flip a repo-level file-missing error into an
    // infrastructure signal, hiding it from the work-item failure path.
    [Theory]
    [InlineData("ENOENT: no such file or directory, open 'foo.txt'")]
    [InlineData("Error: ENOENT: no such file or directory, scandir '/work/src/missing'")]
    [InlineData("fopen('foo.txt'): No such file or directory")]
    public void Exit127NonBinaryFailure_RemainsNormal(string stderr)
    {
        var c = AgentFailureClassifier.Classify(
            stderr: stderr,
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    // POSIX /bin/sh emits "1: <name>: not found" rather than the bash
    // "command not found" shape. The classifier must still catch this as a
    // binary-launch failure (the sandbox is missing the agent binary) without
    // matching a generic "Not Found" HTTP body.
    [Theory]
    [InlineData("/bin/sh: 1: agy: not found")]
    [InlineData("sh: 1: codex: not found")]
    public void Exit127PosixShellNotFound_Classified_AsInfrastructure(string stderr)
    {
        var c = AgentFailureClassifier.Classify(
            stderr: stderr,
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Theory]
    [InlineData("failed to materialise codex auth: exit 1")]
    [InlineData("failed to materialize cursor auth: exit 7")]
    public void MaterialisationFailures_Classified_AsInfrastructure(string summary)
    {
        var c = AgentFailureClassifier.Classify(stderr: "permission denied", summary: summary);
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Fact]
    public void Quota_BeatsNetwork_WhenBothPatternsPresent()
    {
        // A 429-with-ECONNRESET-tail must classify as quota — falling back to
        // a different agent on a 429 is correct; trying again on the same one
        // because we mistook it for a network blip wastes the operator's day.
        var c = AgentFailureClassifier.Classify(
            stderr: "API rate_limit_exceeded\nECONNRESET while retrying");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void Quota_BeatsAuth_WhenBothPatternsPresent()
    {
        // Defence against a re-ordering bug: a 429 with an "invalid_api_key"
        // tail must still classify as quota. Reversing the check order would
        // silently misclassify quota events as auth failures, sending operators
        // to rotate credentials rather than waiting for the quota window.
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: rate_limit_exceeded\nfollow-up: invalid_api_key reported");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void Auth_BeatsNetwork_WhenBothPatternsPresent()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: 401 Unauthorized; subsequent ECONNRESET");
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
    }

    [Fact]
    public void EmptyOutput_ClassifiedAsUnknown()
    {
        var c = AgentFailureClassifier.Classify(stderr: null, stdout: null);
        Assert.Equal(AgentFailureKind.Unknown, c.Kind);
    }

    [Fact]
    public void DefaultClassifyFailure_OnSuccessResult_ReturnsNormal()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(true, "ok", "", null));
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    [Fact]
    public void DefaultClassifyFailure_DelegatesToSharedClassifier()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(false, "exit 1", "", "RESOURCE_EXHAUSTED"));
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void DefaultClassifyFailure_PassesSummaryToSharedClassifier()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(false, "agent exited 127", "", ""));
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    private sealed class ProbeOnlyRunner : IAgentRunner
    {
        public AgentKind Kind => new("probe");
        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw new NotSupportedException("test fixture only");
    }
}
