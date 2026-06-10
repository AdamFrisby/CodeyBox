using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AntigravityAgentRunner"/>. Uses the shared
/// <c>CapturingSandbox</c> fake from the Gemini suite to inspect the argv,
/// stdin, and resume shape — same pattern as the Claude/Gemini runner tests.
/// </summary>
public sealed class AntigravityAgentRunnerTests
{
    [Fact]
    public void Kind_IsAntigravity()
    {
        var runner = new AntigravityAgentRunner();
        Assert.Equal(AgentKind.Antigravity, runner.Kind);
    }

    [Fact]
    public async Task RunAsync_Argv_StartsWithAgyBinary()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Equal("agy", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsPrintAndSkipPermissions()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        Assert.Contains("--print", sandbox.CapturedExec!.Argv);
        Assert.Contains("--dangerously-skip-permissions", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_PassesModelWhenSet()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null, modelId: "gemini-3.5-flash-high");

        var argv = sandbox.CapturedExec!.Argv;
        var modelIdx = IndexOf(argv, "--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gemini-3.5-flash-high", argv[modelIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_PromptIsPassedViaStdin()
    {
        // Big rework prompts (audit findings, multi-file diffs) can exceed
        // Linux's 128 KiB MAX_ARG_STRLEN per single argv element. Verify the
        // prompt flows through stdin, not argv.
        const string prompt = "rebuild the build pipeline";
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", prompt, credential: null);

        Assert.Equal(prompt, sandbox.CapturedExec!.Stdin);
        Assert.DoesNotContain(prompt, sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public void TryParseConversationId_ExtractsIdFromCheckpointRef()
    {
        Assert.Equal("abc123", AntigravityAgentRunner.TryParseConversationId("agy-conversation:abc123"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId("agy-conversation:"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId("other-prefix:x"));
        Assert.Null(AntigravityAgentRunner.TryParseConversationId(null));
    }

    [Fact]
    public async Task RunResumedAsync_WithCheckpointId_PassesConversationFlag()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        var resume = new AgentResumeContext(
            CheckpointRef: "agy-conversation:conv-7",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        await runner.RunResumedAsync(sandbox, "/work", "next turn", credential: null, resume);

        var argv = sandbox.CapturedExec!.Argv;
        var convIdx = IndexOf(argv, "--conversation");
        Assert.True(convIdx >= 0);
        Assert.Equal("conv-7", argv[convIdx + 1]);
        Assert.DoesNotContain("--continue", argv);
    }

    private static int IndexOf(IReadOnlyList<string> argv, string needle)
    {
        for (var i = 0; i < argv.Count; i++)
            if (argv[i] == needle) return i;
        return -1;
    }

    [Fact]
    public async Task RunResumedAsync_WithoutCheckpointId_FallsBackToContinue()
    {
        var sandbox = new CapturingSandbox();
        var runner = new AntigravityAgentRunner();

        var resume = new AgentResumeContext(
            CheckpointRef: "some-other-ref",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        await runner.RunResumedAsync(sandbox, "/work", "next turn", credential: null, resume);

        Assert.Contains("--continue", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("--conversation", sandbox.CapturedExec!.Argv);
    }

    // ── PrepareSandboxAsync OAuth-creds materialisation ──────────────────────

    [Fact]
    public async Task RunAsync_WithOAuthCredsBundle_WritesCredentialsFileToSandbox()
    {
        // PrepareSandboxAsync must materialise the agy token bundle into
        // ~/.gemini/antigravity-cli/antigravity-oauth-token (chmod 600) — agy's
        // fileTokenStorage path when no system keyring is present (every headless
        // sandbox). The bundle is shipped verbatim by upstream credential
        // providers (refresh_token retained so the in-VM agy can self-refresh —
        // see AntigravityEnvironmentCredentialProvider / AgentInstanceCredentialResolver
        // / CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle).
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new AntigravityAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Antigravity,
            new Dictionary<string, string>
            {
                [AntigravityConstants.OAuthCredsEnvVar] =
                    """{"auth_method":"consumer","token":{"access_token":"ya29.abc","expiry":"2099-01-01T00:00:00Z"}}""",
            },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.Equal(2, sandbox.AllExecs.Count);
        var prep = sandbox.AllExecs[0];
        Assert.Equal("bash", prep.Argv[0]);
        Assert.Equal("-c", prep.Argv[1]);
        var script = prep.Argv[2];
        Assert.Contains("$HOME/.gemini/antigravity-cli/antigravity-oauth-token", script);
        Assert.Contains(AntigravityConstants.OAuthCredsEnvVar, script);
        Assert.Contains("chmod 600", script);
        // Second exec is the agy CLI invocation, not the prep hook.
        Assert.Equal("agy", sandbox.AllExecs[1].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_WithoutOAuthCredsBundle_DoesNotRunPrepHook()
    {
        // Credentials with no OAuth-creds env var (e.g. operators using only
        // legacy auth paths or with creds already on the image) must skip the
        // prep hook entirely — single exec is just the agy CLI.
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new AntigravityAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Antigravity,
            new Dictionary<string, string> { ["UNRELATED"] = "x" },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.Single(sandbox.AllExecs);
        Assert.Equal("agy", sandbox.AllExecs[0].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_NullCredential_DoesNotRunPrepHook()
    {
        // No credential at all must also skip the prep hook — the runner
        // tolerates running without injected auth (e.g. image-baked creds).
        var sandbox = new MultiExecCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.Single(sandbox.AllExecs);
        Assert.Equal("agy", sandbox.AllExecs[0].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_PrepHookFails_PropagatesAsAgentFailure()
    {
        // If the sandbox can't write the creds file (chmod / quota / read-only
        // home), surface the failure rather than racing on to the agy
        // invocation, which would 401 against the gateway with a confusing
        // shape and chew through a request slot.
        var sandbox = new MultiExecCapturingSandbox(prepExitCode: 1, prepStderr: "permission denied");
        var runner = new AntigravityAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Antigravity,
            new Dictionary<string, string>
            {
                [AntigravityConstants.OAuthCredsEnvVar] = """{"access_token":"ya29.abc"}""",
            },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.False(result.Success);
        Assert.Contains("antigravity auth", result.Summary);
        Assert.Single(sandbox.AllExecs);
    }
}
