using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CavemanCodeAgentRunner"/>. Uses the shared
/// CapturingSandbox to inspect the argv and stdin the runner forwards.
/// </summary>
public sealed class CavemanCodeAgentRunnerTests
{
    [Fact]
    public void Kind_IsCavemanCode()
    {
        Assert.Equal(AgentKind.CavemanCode, new CavemanCodeAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_CavemanCode_RoundTrips()
    {
        Assert.Equal(AgentKind.CavemanCode, new AgentKind("caveman"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesCavemanCodeBinaryWithPrintFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Equal("caveman-code", argv[0]);
        Assert.Equal("-p", argv[1]);
    }

    [Fact]
    public async Task RunAsync_Prompt_GoesToStdinNotArgv()
    {
        // MAX_ARG_STRLEN is 128 KiB per argv element; the runner feeds the
        // prompt via stdin (verified: `echo ... | caveman-code -p` reaches
        // agent init with no positional message).
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();
        const string prompt = "a prompt far too interesting to inline in argv";

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains(prompt));
    }

    [Fact]
    public async Task RunAsync_DefaultModel_FromAgentDefaults()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["caveman"] = "openai/gpt-5.5",
            });
        var runner = new CavemanCodeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        var argv = sandbox.CapturedExec!.Argv;
        Assert.Contains("--model", argv);
        Assert.Contains("openai/gpt-5.5", argv);
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_OverridesDefault()
    {
        var sandbox = new CapturingSandbox();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["caveman"] = "openai/gpt-5.5",
            });
        var runner = new CavemanCodeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "x", credential: null,
            modelId: "anthropic/claude-opus-4-6");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("anthropic/claude-opus-4-6", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        // Without an explicit id or a configured default the CLI resolves
        // its own per-provider default — the runner must not invent one.
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_ReasoningModeHigh_ForwardsThinkingFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: "high");

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var thinkingIdx = argv.IndexOf("--thinking");
        Assert.True(thinkingIdx >= 0);
        Assert.Equal("high", argv[thinkingIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_UnknownReasoningMode_DropsFlag()
    {
        // An unrecognised level must not reach the CLI: it would only add a
        // startup warning while silently running at the CLI default.
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", credential: null, reasoningMode: "ultra");

        Assert.DoesNotContain("--thinking", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_NeverPassesApiKeyFlag()
    {
        // Secrets must not ride argv (visible in process listings); keys
        // arrive via the sandbox environment as direct credential vars.
        var sandbox = new CapturingSandbox();
        var runner = new CavemanCodeAgentRunner();
        var cred = new AgentCredential(AgentKind.CavemanCode,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "x", credential: cred);

        Assert.DoesNotContain("--api-key", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain(sandbox.CapturedExec.Argv, a => a.Contains("sk-test"));
    }
}
