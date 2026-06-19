using CodeyBox.Audit.Llm;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the deep LLM auditors (OWASP ASVS, arch-coherence) forward the
/// configured (ModelId, ReasoningMode) from <see cref="DeepAuditContext"/>
/// through to the agent runner. Regression for the bug where the auditors
/// hardcoded <c>modelId: null, reasoningMode: null</c> so audits ran on the
/// CLI's defaults instead of the routed top-tier model + reasoning effort.
/// </summary>
public sealed class DeepAuditorParameterPlumbingTests
{
    public static IEnumerable<object[]> DeepAuditorCases()
    {
        yield return [new OwaspAsvsDeepAuditor(), "/audit/owasp-result.json"];
        yield return [new ArchCoherenceDeepAuditor(), "/audit/arch-result.json"];
    }

    [Fact]
    public async Task OwaspAsvsDeepAuditor_ForwardsModelAndReasoningToRunner()
    {
        var runner = new CapturingAgentRunner();
        var auditor = new OwaspAsvsDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("p"),
            BranchName: "release/v1",
            Iteration: 1,
            AuditRunner: runner,
            ModelId: "claude-opus-4-7",
            ReasoningMode: "high");

        await auditor.RunAsync(new VerdictSandbox("/audit/owasp-result.json"), "/work", ctx);

        Assert.True(runner.RunCalled);
        Assert.Equal("claude-opus-4-7", runner.ObservedModelId);
        Assert.Equal("high", runner.ObservedReasoningMode);
    }

    [Fact]
    public async Task ArchCoherenceDeepAuditor_ForwardsModelAndReasoningToRunner()
    {
        var runner = new CapturingAgentRunner();
        var auditor = new ArchCoherenceDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("p"),
            BranchName: "release/v1",
            Iteration: 1,
            AuditRunner: runner,
            ModelId: "claude-opus-4-7",
            ReasoningMode: "high");

        await auditor.RunAsync(new VerdictSandbox("/audit/arch-result.json"), "/work", ctx);

        Assert.True(runner.RunCalled);
        Assert.Equal("claude-opus-4-7", runner.ObservedModelId);
        Assert.Equal("high", runner.ObservedReasoningMode);
    }

    [Fact]
    public async Task OwaspAsvsDeepAuditor_NoModelOrReasoning_ForwardsNullNotLiteral()
    {
        var runner = new CapturingAgentRunner();
        var auditor = new OwaspAsvsDeepAuditor();
        var ctx = new DeepAuditContext(
            ReleaseId.New(),
            new ProjectId("p"),
            BranchName: "release/v1",
            Iteration: 1,
            AuditRunner: runner);

        await auditor.RunAsync(new VerdictSandbox("/audit/owasp-result.json"), "/work", ctx);

        Assert.True(runner.RunCalled);
        Assert.Null(runner.ObservedModelId);
        Assert.Null(runner.ObservedReasoningMode);
    }

    [Theory]
    [MemberData(nameof(DeepAuditorCases))]
    public async Task DeepAuditors_AgentFailure_PreserveAgentAuthMetadata(
        IDeepAuditor auditor,
        string resultFile)
    {
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var runner = new CapturingAgentRunner
        {
            Result = new AgentResult(false, "agent exited 1", transcript, "stderr auth prompt"),
        };

        var result = await auditor.RunAsync(
            new VerdictSandbox(resultFile),
            "/work",
            NewContext(runner));

        Assert.False(result.Passed);
        Assert.Equal(transcript, result.RawOutput);
        Assert.Equal(transcript, result.AgentStdout);
        Assert.Equal("stderr auth prompt", result.AgentStderr);
        Assert.Equal("agent exited 1", result.AgentSummary);
    }

    [Theory]
    [MemberData(nameof(DeepAuditorCases))]
    public async Task DeepAuditors_MissingResult_PreserveStderrOnlyAuthMetadata(
        IDeepAuditor auditor,
        string resultFile)
    {
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var runner = new CapturingAgentRunner
        {
            Result = new AgentResult(true, "ok", "ordinary stdout", transcript),
        };

        var result = await auditor.RunAsync(
            new VerdictSandbox(resultFile, resultJson: null),
            "/work",
            NewContext(runner));

        Assert.False(result.Passed);
        Assert.Equal("ordinary stdout", result.RawOutput);
        Assert.Equal("ordinary stdout", result.AgentStdout);
        Assert.Equal(transcript, result.AgentStderr);
        Assert.Equal("ok", result.AgentSummary);
    }

    [Theory]
    [MemberData(nameof(DeepAuditorCases))]
    public async Task DeepAuditors_InvalidJson_PreserveAgentAuthMetadata(
        IDeepAuditor auditor,
        string resultFile)
    {
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        var runner = new CapturingAgentRunner
        {
            Result = new AgentResult(true, "ok", "ordinary stdout", transcript),
        };

        var result = await auditor.RunAsync(
            new VerdictSandbox(resultFile, resultJson: "{not json"),
            "/work",
            NewContext(runner));

        Assert.False(result.Passed);
        Assert.Equal("ordinary stdout", result.RawOutput);
        Assert.Equal("ordinary stdout", result.AgentStdout);
        Assert.Equal(transcript, result.AgentStderr);
        Assert.Equal("ok", result.AgentSummary);
    }

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string? ObservedModelId { get; private set; }
        public string? ObservedReasoningMode { get; private set; }
        public bool RunCalled { get; private set; }
        public AgentResult Result { get; init; } = new(true, "ok", "stdout", null);

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
        {
            RunCalled = true;
            ObservedModelId = modelId;
            ObservedReasoningMode = reasoningMode;
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Sandbox that returns a passing verdict JSON when asked to <c>cat</c>
    /// the expected result-file path; succeeds silently for any other exec.
    /// </summary>
    private sealed class VerdictSandbox : ISandbox
    {
        private readonly string _resultFile;
        private readonly string? _resultJson;

        public VerdictSandbox(
            string resultFile,
            string? resultJson = "{\"passed\":true,\"findings\":[]}")
        {
            _resultFile = resultFile;
            _resultJson = resultJson;
        }

        public string Id => "verdict-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "cat" && exec.Argv[1] == _resultFile)
            {
                return _resultJson is null
                    ? Task.FromResult(new SandboxExecResult(1, "", "missing result"))
                    : Task.FromResult(new SandboxExecResult(0, _resultJson, ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DeepAuditContext NewContext(IAgentRunner runner)
        => new(
            ReleaseId.New(),
            new ProjectId("p"),
            BranchName: "release/v1",
            Iteration: 1,
            AuditRunner: runner);
}
