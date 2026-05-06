using CodeyBox.Audit.Llm;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class LlmReviewAuditorTests
{
    [Fact]
    public async Task RunAsync_PassesAuditCredentialToResolvedAgent()
    {
        var runner = new CredentialCapturingRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "security:llm-review",
            Agent = runner,
            ReviewFocus = "- verify credentials are available",
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

    private sealed class CredentialCapturingRunner : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Codex;
        public AgentCredential? ObservedCredential { get; private set; }

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
