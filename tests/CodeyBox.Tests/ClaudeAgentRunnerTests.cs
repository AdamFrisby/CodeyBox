using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

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
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-opus-4-7",
            });
        var runner = new ClaudeAgentRunner(defaults);

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
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-opus-4-7",
            });
        var runner = new ClaudeAgentRunner(defaults);

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
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults);

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null, modelId: null);

        Assert.DoesNotContain("--model", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public void DefaultModelId_IsOpus47()
    {
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-opus-4-7",
            });
        var runner = new ClaudeAgentRunner(defaults);
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
    public async Task RunAsync_PromptIsPassedViaStdin()
    {
        // Linux's MAX_ARG_STRLEN is 128 KiB per single argv element; large rework
        // prompts that include many audit findings exceed this and surface as
        // exit 126 from the sandbox wrapper's `exec "$@"`. The runner now passes
        // the prompt via stdin so it isn't bounded by argv-string limits. claude
        // --print reads stdin when no positional prompt is given.
        const string prompt = "write a test";
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.DoesNotContain(prompt, sandbox.CapturedExec!.Argv);
        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
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

    // ── Sandbox credentials-file materialisation ──────────────────────────────

    [Fact]
    public async Task RunAsync_WithOAuthJsonBundle_WritesCredentialsFileToSandbox()
    {
        // ClaudeAgentRunner.PrepareSandboxAsync must materialise the sanitised
        // OAuth bundle (access_token + expires_at, no refresh_token) into the
        // VM's ~/.claude/.credentials.json when the provider has shipped
        // CODEYBOX_CLAUDE_OAUTH_JSON. The in-VM CLI cannot self-refresh
        // (by design — see ClaudeOAuthFileCredentialProvider for the shared-
        // OAuth race rationale); this hook just gets the current access_token
        // onto disk in the canonical location so the CLI's auth probe
        // succeeds.
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new ClaudeAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat01-abc",
                [ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar] =
                    """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-abc","expiresAt":9999999999}}""",
            },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "prompt", credential);

        // First exec should be the bash hook that writes the creds file.
        var prep = sandbox.AllExecs[0];
        Assert.Equal("bash", prep.Argv[0]);
        Assert.Equal("-c", prep.Argv[1]);
        var script = prep.Argv[2];
        Assert.Contains("$HOME/.claude/.credentials.json", script);
        Assert.Contains("CODEYBOX_CLAUDE_OAUTH_JSON", script);
        Assert.Contains("chmod 600", script);
    }

    [Fact]
    public async Task RunAsync_WithoutOAuthJsonBundle_DoesNotRunPrepHook()
    {
        // ANTHROPIC_API_KEY / legacy-only auth flows must not invoke the
        // bash hook; CapturedExec stays the claude CLI invocation.
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new ClaudeAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.Single(sandbox.AllExecs);
        Assert.Equal("claude", sandbox.AllExecs[0].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_NullCredential_DoesNotRunPrepHook()
    {
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new ClaudeAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Single(sandbox.AllExecs);
        Assert.Equal("claude", sandbox.AllExecs[0].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_PrepHookFails_PropagatesAsAgentFailure()
    {
        // If the sandbox cannot write the creds file, surface the failure
        // rather than racing on to the claude invocation (which would 401).
        var sandbox = new MultiExecCapturingSandbox(prepExitCode: 1, prepStderr: "permission denied");
        var runner = new ClaudeAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string>
            {
                [ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar] =
                    """{"claudeAiOauth":{"accessToken":"x"}}""",
            },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.False(result.Success);
        Assert.Contains("claude auth", result.Summary);
        Assert.Single(sandbox.AllExecs);
    }

    // ── Rotation pusher registration ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RegistersSandboxWithRotationPusherForDurationOfRun()
    {
        // The runner must register the active sandbox with the pusher before
        // delegating to the CLI and unregister it on return so a host-side
        // rotation that lands mid-iteration pushes into THIS sandbox (and a
        // rotation that lands later does not).
        var sandbox = new CapturingSandbox();
        var pusher = new RecordingRotationPusher();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, pusher);

        Assert.Empty(pusher.ActiveDuringRun);

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Single(pusher.RegisterCalls);
        Assert.Same(sandbox, pusher.RegisterCalls[0]);
        // After the run completes the registration must be disposed.
        Assert.Equal(1, pusher.DisposedCount);
    }

    [Fact]
    public async Task RunAsync_NullPusher_DoesNotThrow()
    {
        // Default construction (no pusher injected) must work for tests and
        // for hosts that don't run the rotation watcher.
        var sandbox = new CapturingSandbox();
        var runner = new ClaudeAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunResumedAsync_RegistersSandboxWithRotationPusher()
    {
        // The preempt-resume code path also runs the CLI in the same sandbox
        // and is equally vulnerable to mid-run rotation; the override must
        // register too.
        var sandbox = new CapturingSandbox();
        var pusher = new RecordingRotationPusher();
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, pusher);

        await runner.RunResumedAsync(sandbox, "/work", "prompt", credential: null,
            resume: new AgentResumeContext(""));

        Assert.Single(pusher.RegisterCalls);
        Assert.Same(sandbox, pusher.RegisterCalls[0]);
        Assert.Equal(1, pusher.DisposedCount);
    }

    // ── No text-only path: account-safety contract ────────────────────────────
    //
    // ClaudeAgentRunner deliberately does NOT implement ITextOnlyAgentRunner.
    // The pre-agentic resolver had a text-only Claude path that POSTed directly
    // to https://api.anthropic.com/v1/messages — a usage shape outside the
    // Claude-Code client that Anthropic can flag and terminate the subscription
    // for. The pickup-time rebase + merge conflict resolvers now run through
    // the normal CLI shape inside the sandbox via the agentic resolver, so the
    // text-only path is gone. This test pins the contract: the type itself
    // does not advertise ITextOnlyAgentRunner.

    [Fact]
    public void ClaudeAgentRunner_DoesNotImplementITextOnlyAgentRunner()
    {
        var interfaces = typeof(ClaudeAgentRunner).GetInterfaces();
        Assert.DoesNotContain(typeof(ITextOnlyAgentRunner), interfaces);
    }

    // ── Text-only model plumbing ──────────────────────────────────────────────

    [Fact]
    public async Task RunTextOnlyAsync_ConfiguredDefault_SetsModelInRequestBody()
    {
        // When a default is configured, the text-only call proceeds past the
        // model-resolve guard (though the sandbox-less HTTP call will fail
        // against a bogus endpoint — we pin that the failure is NOT the new
        // "no default configured" guard).
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-haiku-4-5",
            });
        var runner = new ClaudeAgentRunner(defaults);
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("hello", cred);

        Assert.False(result.Success);
        Assert.DoesNotContain("no default configured", result.Summary);
        Assert.DoesNotContain("no default configured", result.Error);
    }

    [Fact]
    public async Task RunTextOnlyAsync_MissingDefault_ReturnsError()
    {
        // When no default is configured and no modelId is passed, the new
        // guard must return a meaningful error instead of sending model=null
        // to the Anthropic API.
        var runner = new ClaudeAgentRunner();
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("hello", cred);

        Assert.False(result.Success);
        Assert.Contains("no default configured", result.Error);
    }
}

internal sealed class RecordingRotationPusher : IClaudeTokenRotationPusher
{
    public List<ISandbox> RegisterCalls { get; } = new();
    public List<ISandbox> ActiveDuringRun { get; } = new();
    public int DisposedCount { get; private set; }

    public IDisposable RegisterActiveSandbox(ISandbox sandbox)
    {
        RegisterCalls.Add(sandbox);
        ActiveDuringRun.Add(sandbox);
        return new Releaser(this, sandbox);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly RecordingRotationPusher _owner;
        private readonly ISandbox _sandbox;
        private bool _disposed;

        public Releaser(RecordingRotationPusher owner, ISandbox sandbox)
        {
            _owner = owner;
            _sandbox = sandbox;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.ActiveDuringRun.Remove(_sandbox);
            _owner.DisposedCount++;
        }
    }
}

/// <summary>
/// Fake sandbox that records every <see cref="SandboxExec"/> it receives.
/// Used to verify multi-step runners that perform a prep exec before the
/// agent invocation.
/// </summary>
internal sealed class MultiExecCapturingSandbox : ISandbox
{
    private readonly int _prepExitCode;
    private readonly string _prepStderr;

    public MultiExecCapturingSandbox(int prepExitCode = 0, string prepStderr = "")
    {
        _prepExitCode = prepExitCode;
        _prepStderr = prepStderr;
    }

    public string Id => "fake-multi";
    public List<SandboxExec> AllExecs { get; } = new();

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        AllExecs.Add(exec);
        // The PrepareSandboxAsync hook runs bash; we distinguish by argv[0].
        if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            return Task.FromResult(new SandboxExecResult(_prepExitCode, string.Empty, _prepStderr));
        return Task.FromResult(new SandboxExecResult(0, "stdout", "stderr"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
