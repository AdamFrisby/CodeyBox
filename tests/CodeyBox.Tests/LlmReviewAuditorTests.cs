using CodeyBox.Audit.Llm;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class LlmReviewAuditorTests
{
    [Fact]
    public void PromptFrame_Render_ReplacesWhitespacePaddedPlaceholders()
    {
        var rendered = LlmPromptFrameTemplate.Render(
            "{{ reviewFocus }}\n{{resultFile}}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reviewFocus"] = "focus text",
                ["resultFile"] = "/audit/result.json",
            });

        Assert.Equal("focus text\n/audit/result.json", rendered);
    }

    [Fact]
    public async Task RunAsync_PassesAuditCredentialToResolvedAgent()
    {
        var runner = new CredentialCapturingRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "security:llm-review",
            Agent = runner,
            ReviewFocus = "- verify credentials are available",
            FrameTemplate = "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}",
        });
        var credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "{\"tokens\":{\"access_token\":\"test\"}}" },
            new Dictionary<string, string>());
        var ctx = new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work",
            AuditRunner: runner,
            AuditCredential: credential);

        var result = await auditor.RunAsync(new ResultFileSandbox(), "/work", ctx);

        Assert.True(result.Passed);
        Assert.Same(credential, runner.ObservedCredential);
    }

    [Fact]
    public async Task RunAsync_ForwardsModelIdAndReasoningModeToRunner()
    {
        var runner = new CredentialCapturingRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "security:llm-review",
            Agent = runner,
            ReviewFocus = "- verify",
            FrameTemplate = "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}",
        });
        var ctx = new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work",
            AuditRunner: runner,
            ModelId: "claude-opus-4-7",
            ReasoningMode: "high");

        await auditor.RunAsync(new ResultFileSandbox(), "/work", ctx);

        Assert.Equal("claude-opus-4-7", runner.ObservedModelId);
        Assert.Equal("high", runner.ObservedReasoningMode);
    }

    [Fact]
    public async Task RunAsync_NullModelAndReasoning_StillForwardsNullsNotLiterals()
    {
        var runner = new CredentialCapturingRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "security:llm-review",
            Agent = runner,
            ReviewFocus = "- verify",
            FrameTemplate = "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}",
        });
        var ctx = new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work",
            AuditRunner: runner);

        await auditor.RunAsync(new ResultFileSandbox(), "/work", ctx);

        Assert.True(runner.RunCalled);
        Assert.Null(runner.ObservedModelId);
        Assert.Null(runner.ObservedReasoningMode);
    }

    private sealed class CredentialCapturingRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Codex;
        public AgentCredential? ObservedCredential { get; private set; }
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
            ObservedCredential = credential;
            ObservedModelId = modelId;
            ObservedReasoningMode = reasoningMode;
            RunCalled = true;
            return Task.FromResult(new AgentResult(true, "ok", "review complete", null));
        }
    }

    private sealed class ResultFileSandbox : ISandbox
    {
        public string Id => "result-file-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, "{\"passed\":true,\"findings\":[]}", ""));

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
