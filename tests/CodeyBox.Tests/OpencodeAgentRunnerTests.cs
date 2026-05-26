using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="OpencodeAgentRunner"/>. Uses the shared
/// CapturingSandbox to inspect the argv and stdin the runner forwards.
/// </summary>
public sealed class OpencodeAgentRunnerTests
{
    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, new OpencodeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Opencode_RoundTrips()
    {
        Assert.Equal(AgentKind.Opencode, new AgentKind("opencode"));
    }

    [Fact]
    public async Task RunAsync_Argv_StartsWithBinaryAndRunSubcommand()
    {
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Equal("opencode", argv[0]);
        Assert.Equal("run", argv[1]);
    }

    [Fact]
    public async Task RunAsync_DefaultModel_IsDeepSeekVariant()
    {
        // The default agent-class slot ships routed at a DeepSeek model id
        // because that is the differentiating capability opencode adds vs
        // the other registered agents. Operators retune the exact id via
        // `opencode models`; this test pins the prefix only.
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Contains("--model", argv);
        Assert.Contains(argv, a => a.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_OverridesDefault()
    {
        // Confirms the multi-provider routing slot: opencode is also usable
        // as an Anthropic/OpenAI fallback when the DeepSeek path is gated.
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-sonnet-4-6");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-sonnet-4-6", argv[modelIdx + 1]);
        Assert.DoesNotContain(argv, a => a.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_OmitsModelFlag_WhenDefaultModelClearedAndNoOverride()
    {
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner { DefaultModelId = null };

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_PromptArrivesOnStdin_NotArgv()
    {
        // MAX_ARG_STRLEN is 128 KiB; rework prompts can exceed it. stdin
        // bypasses the ceiling and matches Codex/Gemini's pattern.
        const string prompt = "write a fizzbuzz in Go";
        var sandbox = new CapturingSandbox();
        var runner = new OpencodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.DoesNotContain(prompt, argv);
        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
    }

    [Fact]
    public async Task RunAsync_NoReasoningFlag_WhenEnvOverrideUnset()
    {
        // No OPENCODE_REASONING_FLAG → reasoning mode is dropped on the
        // floor rather than guessed at, per the vendor-api-drift rule.
        var prior = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", null);
            var sandbox = new CapturingSandbox();
            var runner = new OpencodeAgentRunner();
            await runner.RunAsync(sandbox, "/work", "x", credential: null,
                reasoningMode: "high");
            Assert.DoesNotContain("high", sandbox.CapturedExec!.Argv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", prior);
        }
    }

    [Fact]
    public async Task RunAsync_ReasoningFlag_AppendedWhenEnvOverrideSet()
    {
        var prior = Environment.GetEnvironmentVariable("OPENCODE_REASONING_FLAG");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", "--reasoning-effort");
            var sandbox = new CapturingSandbox();
            var runner = new OpencodeAgentRunner();
            await runner.RunAsync(sandbox, "/work", "x", credential: null,
                reasoningMode: "high");
            var argv = sandbox.CapturedExec!.Argv.ToList();
            var idx = argv.IndexOf("--reasoning-effort");
            Assert.True(idx >= 0);
            Assert.Equal("high", argv[idx + 1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_REASONING_FLAG", prior);
        }
    }
}
