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

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string? ObservedModelId { get; private set; }
        public string? ObservedReasoningMode { get; private set; }
        public bool RunCalled { get; private set; }

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
            return Task.FromResult(new AgentResult(true, "ok", "stdout", null));
        }
    }

    /// <summary>
    /// Sandbox that returns a passing verdict JSON when asked to <c>cat</c>
    /// the expected result-file path; succeeds silently for any other exec.
    /// </summary>
    private sealed class VerdictSandbox : ISandbox
    {
        private readonly string _resultFile;

        public VerdictSandbox(string resultFile) => _resultFile = resultFile;

        public string Id => "verdict-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 2 && exec.Argv[0] == "cat" && exec.Argv[1] == _resultFile)
                return Task.FromResult(new SandboxExecResult(0, "{\"passed\":true,\"findings\":[]}", ""));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
