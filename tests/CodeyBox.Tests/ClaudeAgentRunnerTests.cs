using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeAgentRunner"/> focusing on the load-bearing
/// <see cref="ClaudeAgentRunner.DefaultModelId"/> pin (claude-opus-4-7) that
/// prevents the CLI from falling back to a lighter default model.
/// </summary>
public sealed class ClaudeAgentRunnerTests
{
    // ── Default model pin ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoModelIdOverride_PassesDefaultModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner(); // DefaultModelId = "claude-opus-4-7"

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "argv must contain --model when DefaultModelId is set");
        Assert.Equal("claude-opus-4-7", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WithExplicitModelId_UsesOverrideNotDefault()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "claude-sonnet-4-6");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("claude-sonnet-4-6", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModelId_OverriddenToNull_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner { DefaultModelId = null };

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public void DefaultModelId_IsOpus47()
    {
        var runner = new ClaudeAgentRunner();
        Assert.Equal("claude-opus-4-7", runner.DefaultModelId);
    }

    // ── Core argv shape ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Argv_ContainsPrintFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Contains("--print", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsDangerouslySkipPermissions()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Contains("--dangerously-skip-permissions", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_PromptIsLastArgument()
    {
        const string prompt = "write a test";
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Argv[^1]);
    }

    // ── Reasoning-mode plumbing ───────────────────────────────────────────────

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task RunAsync_WithReasoningMode_AppendsEffortFlag(string mode)
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null,
            modelId: null, reasoningMode: mode);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var effortIdx = argv.IndexOf("--effort");
        Assert.True(effortIdx >= 0, $"argv must contain --effort when reasoningMode='{mode}'");
        Assert.Equal(mode, argv[effortIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_NoReasoningMode_OmitsEffortFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null,
            modelId: null, reasoningMode: null);

        Assert.DoesNotContain("--effort", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_EmptyReasoningMode_OmitsEffortFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null,
            modelId: null, reasoningMode: "");

        Assert.DoesNotContain("--effort", sandbox.CapturedExec!.Argv);
    }
}
