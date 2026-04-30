using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="GeminiAgentRunner"/>. Uses a capturing fake
/// sandbox to inspect the argv and environment that RunAsync forwards to
/// the sandbox — the same pattern as the ClaudeQuotaProbe / router tests.
/// </summary>
public sealed class GeminiAgentRunnerTests
{
    // ── Kind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Kind_IsGemini()
    {
        var runner = new GeminiAgentRunner();
        Assert.Equal(AgentKind.Gemini, runner.Kind);
    }

    [Fact]
    public void AgentKind_Gemini_RoundTrips()
    {
        var parsed = new AgentKind("gemini");
        Assert.Equal(AgentKind.Gemini, parsed);
    }

    // ── Argv construction ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Argv_StartsWithBinary()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Equal("gemini", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsYoloFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Contains("--yolo", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_PassesPromptAfterDashP()
    {
        const string prompt = "write a fizzbuzz in Go";
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var pIdx = argv.IndexOf("-p");
        Assert.True(pIdx >= 0, "argv must contain -p flag");
        Assert.Equal(prompt, argv[pIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WithModelId_InjectsModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "gemini-2.5-pro");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0, "argv must contain --model flag");
        Assert.Equal("gemini-2.5-pro", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WithoutModelId_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_EmptyModelId_NoModelFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: "");

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    // ── Binary override ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CustomBinary_UsesOverride()
    {
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner { Binary = "/opt/gemini/bin/gemini" };

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Equal("/opt/gemini/bin/gemini", sandbox.CapturedExec!.Argv[0]);
    }

    // ── Prompt not on argv before -p ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PromptIsLastArgument()
    {
        const string prompt = "my prompt";
        var sandbox = new CapturingSandbox();
        var runner = new GeminiAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Argv[^1]);
    }

    // ── Success / failure propagation ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SandboxExitZero_ReturnsSuccess()
    {
        var runner = new GeminiAgentRunner();
        var result = await runner.RunAsync(new CapturingSandbox(exitCode: 0), "/work", "p", null);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunAsync_SandboxExitNonZero_ReturnsFailure()
    {
        var runner = new GeminiAgentRunner();
        var result = await runner.RunAsync(new CapturingSandbox(exitCode: 1), "/work", "p", null);

        Assert.False(result.Success);
    }
}

/// <summary>
/// Fake sandbox that records the most recent <see cref="SandboxExec"/> it
/// received and returns a configurable exit code.
/// </summary>
internal sealed class CapturingSandbox : ISandbox
{
    private readonly int _exitCode;

    public CapturingSandbox(int exitCode = 0) { _exitCode = exitCode; }

    public string Id => "fake";
    public SandboxExec? CapturedExec { get; private set; }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        CapturedExec = exec;
        return Task.FromResult(new SandboxExecResult(_exitCode, "stdout", "stderr"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
