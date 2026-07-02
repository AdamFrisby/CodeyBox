using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;
using CodeyBox.Sandbox;

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
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);

        Assert.Equal("agy", sandbox.CapturedExec!.Argv[0]);
    }

    [Fact]
    public async Task RunAsync_Argv_ContainsPrintAndSkipPermissions()
    {
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        Assert.Contains("--print", sandbox.CapturedExec!.Argv);
        Assert.Contains("--dangerously-skip-permissions", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_Argv_PassesModelWhenSet()
    {
        var sandbox = new AntigravityCapturingSandbox();
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
        var sandbox = new AntigravityCapturingSandbox();
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
        var sandbox = new AntigravityCapturingSandbox();
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
        var sandbox = new AntigravityCapturingSandbox();
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
        var sandbox = new AntigravityCapturingSandbox();
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

        var prepAndAgyExecs = NonInfraExecs(sandbox);
        Assert.Equal(2, prepAndAgyExecs.Count);
        var prep = prepAndAgyExecs[0];
        Assert.Equal("bash", prep.Argv[0]);
        Assert.Equal("-c", prep.Argv[1]);
        var script = prep.Argv[2];
        Assert.Contains("$HOME/.gemini/antigravity-cli/antigravity-oauth-token", script);
        Assert.Contains(AntigravityConstants.OAuthCredsEnvVar, script);
        Assert.Contains("chmod 600", script);
        // Second exec is the agy CLI invocation, not the prep hook.
        Assert.Equal("agy", prepAndAgyExecs[1].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_WithoutOAuthCredsBundle_DoesNotRunPrepHook()
    {
        // Credentials with no OAuth-creds env var (e.g. operators using only
        // legacy auth paths or with creds already on the image) must skip the
        // prep hook entirely — single exec is just the agy CLI.
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Antigravity,
            new Dictionary<string, string> { ["UNRELATED"] = "x" },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "prompt", credential);

        var prepAndAgyExecs = NonInfraExecs(sandbox);
        Assert.Single(prepAndAgyExecs);
        Assert.Equal("agy", prepAndAgyExecs[0].Argv[0]);
    }

    [Fact]
    public async Task RunAsync_NullCredential_DoesNotRunPrepHook()
    {
        // No credential at all must also skip the prep hook — the runner
        // tolerates running without injected auth (e.g. image-baked creds).
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var prepAndAgyExecs = NonInfraExecs(sandbox);
        Assert.Single(prepAndAgyExecs);
        Assert.Equal("agy", prepAndAgyExecs[0].Argv[0]);
    }

    // Filters out the infrastructure execs the runner emits around the agy
    // invocation — the mkdir that guarantees the glog directory exists and the
    // tail that reads the glog back — so a test can assert on just the prep
    // hook + agy CLI calls.
    private static List<SandboxExec> NonInfraExecs(AntigravityCapturingSandbox sandbox) =>
        sandbox.AllExecs
            .Where(e => e.Argv.Count > 0 && e.Argv[0] != "tail" && e.Argv[0] != "mkdir")
            .ToList();

    [Fact]
    public async Task RunAsync_WhenCaptureStructuredStreamTrue_AppendsOutputFormatStreamJson()
    {
        // PipelineRunner only sets captureStructuredStream=true after
        // SupportsStructuredStreamAsync confirmed `agy --help` advertises the
        // flag. The runner must then ask for stream-json so the captured
        // .jsonl is structured (and AntigravityStreamParser /
        // AntigravityCostExtractor can decode it). A regression that dropped
        // --output-format / stream-json would silently downgrade the run to
        // plaintext-fallback capture — invisible to the new parser tests,
        // which hand-write the NDJSON themselves.
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            captureStructuredStream: true);

        var argv = sandbox.CapturedExec!.Argv;
        var formatIdx = IndexOf(argv, "--output-format");
        Assert.True(formatIdx >= 0, "expected --output-format in argv when captureStructuredStream=true");
        Assert.Equal("stream-json", argv[formatIdx + 1]);
    }

    [Fact]
    public async Task RunAsync_WhenCaptureStructuredStreamFalse_OmitsOutputFormatStreamJson()
    {
        // Plaintext-capture path: SupportsStructuredStreamAsync said no (older
        // agy without the flag, or help-text probe failed), so the runner must
        // NOT pass --output-format. Passing it on a CLI that doesn't recognise
        // it bombs the run with "unknown option" — exactly the cascade the
        // gated capability check was added to prevent.
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            captureStructuredStream: false);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunResumedAsync_DoesNotRequestStructuredStream()
    {
        // Resume turns deliberately drop captureStructuredStream — see
        // CliAgentRunnerBase.RunResumedAsync wiring. Verify the resume argv
        // never carries the flag even when SupportsStructuredStreamAsync would
        // have said yes, so a resumed agy run doesn't introduce a
        // capture-format change mid-conversation.
        var sandbox = new AntigravityCapturingSandbox();
        var runner = new AntigravityAgentRunner();

        var resume = new AgentResumeContext(
            CheckpointRef: "agy-conversation:conv-7",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        await runner.RunResumedAsync(sandbox, "/work", "next turn", credential: null, resume);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
    }

    [Fact]
    public async Task RunAsync_PrepHookFails_PropagatesAsAgentFailure()
    {
        // If the sandbox can't write the creds file (chmod / quota / read-only
        // home), surface the failure rather than racing on to the agy
        // invocation, which would 401 against the gateway with a confusing
        // shape and chew through a request slot.
        var sandbox = new AntigravityCapturingSandbox(prepExitCode: 1, prepStderr: "permission denied");
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
        // The prep hook failed before agy could run, so no agy invocation was
        // dispatched (the mkdir that guarantees the log dir may run first).
        Assert.DoesNotContain(sandbox.AllExecs, e => e.Argv.Count > 0 && e.Argv[0] == "agy");
        Assert.Contains(sandbox.AllExecs, e => e.Argv.Count > 0 && e.Argv[0] == "bash");
    }

    [Fact]
    public async Task RunAsync_FailedRun_CapturesAgyLogFileIntoStderrAndStreams()
    {
        // A FAILED agy run is the case where the glog must reach the
        // failure/quota/auth classifiers — those read result.Stderr. Verify the
        // glog is both merged into Stderr (classifier input) and archived to the
        // stream (operator-facing capture).
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Model resolved: gemini-3.5-flash\nRESOURCE_EXHAUSTED (code 429): Individual quota reached",
            agyExitCode: 1
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: false);

        // 1. tail reads back a /work-rooted log path (provider-agnostic, under
        //    SandboxConventions.AgentLogDir), NOT a hardcoded /home/ubuntu path.
        var tailExec = sandbox.AllExecs.FirstOrDefault(e => e.Argv.Count > 0 && e.Argv[0] == "tail");
        Assert.NotNull(tailExec);
        Assert.Equal("-c", tailExec!.Argv[1]);
        Assert.Equal((256 * 1024).ToString(), tailExec.Argv[2]);
        var tailedPath = tailExec.Argv[3];
        Assert.StartsWith("/work/.codeybox/agent-logs/agy-run-", tailedPath);
        Assert.EndsWith(".log", tailedPath);

        // 2. agy was invoked with --log-file pointing at the SAME path tail read.
        //    Without this a regression that dropped/mismatched --log-file would
        //    leave agy logging to its default file and go undetected.
        var agyExec = sandbox.CapturedAgyExec;
        Assert.NotNull(agyExec);
        var logFileIdx = IndexOf(agyExec!.Argv, "--log-file");
        Assert.True(logFileIdx >= 0, "expected --log-file in agy argv");
        Assert.Equal(tailedPath, agyExec.Argv[logFileIdx + 1]);

        // 3. On a failed run the glog is merged into Stderr for the classifiers.
        Assert.Contains("RESOURCE_EXHAUSTED", result.Stderr);
        Assert.Contains("Model resolved: gemini-3.5-flash", result.Stderr);

        // 4. And archived to the stream for observability.
        Assert.Contains("Model resolved: gemini-3.5-flash\n", streamedChunks);
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_SuccessfulRun_DoesNotMergeGlogIntoStderr()
    {
        // Classifier-safety: agy's glog is cumulative and records transient
        // errors it later recovered from. The pipeline runs the auth classifier
        // over Stderr even on SUCCESS, so a recovered "RESOURCE_EXHAUSTED" /
        // "API Error: 401" line in the glog must NOT reach Stderr on a
        // successful run — else the member gets falsely benched/parked. The
        // glog is still archived to the stream for observability.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "applyAuthResult: authMethod=consumer\nRESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)",
            agyExitCode: 0
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: false);

        Assert.True(result.Success);
        // Stderr must NOT carry the recovered-transient glog lines.
        Assert.DoesNotContain("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("applyAuthResult", result.Stderr ?? string.Empty);
        // But the stream archive still surfaces the diagnostics to operators.
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)\n", streamedChunks);
        Assert.Contains("applyAuthResult: authMethod=consumer\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_CapturedGlog_RedactsGoogleOAuthTokens()
    {
        // agy's auth glog is the most likely place a Google OAuth access/refresh
        // token surfaces in plaintext. The capture must scrub ya29./1// tokens
        // (via the normal RedactText path) before they land in the stream/audit.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "fileTokenStorage: access_token=ya29.aVeryLongGoogleAccessTokenValue0123456789 refresh=1//0longRefreshTokenValue0123456789",
            agyExitCode: 1
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: false);

        Assert.DoesNotContain("ya29.aVeryLong", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("1//0longRefresh", result.Stderr ?? string.Empty);
        Assert.DoesNotContain(streamedChunks, chunk => chunk.Contains("ya29.aVeryLong"));
        Assert.DoesNotContain(streamedChunks, chunk => chunk.Contains("1//0longRefresh"));
    }

    [Fact]
    public async Task RunResumedAsync_FailedRun_CapturesAgyLogFileIntoStderr()
    {
        // The resume override has the same log-capture wiring as RunAsync;
        // exercise its glog merge on a failed resumed run so the branch is not
        // left unverified.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "resumed conversation\nRESOURCE_EXHAUSTED (code 429): Individual quota reached",
            agyExitCode: 1
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var resume = new AgentResumeContext(
            CheckpointRef: "agy-conversation:conv-7",
            ScratchpadArchivePath: "/nonexistent/.codeybox/preempt-scratchpad.tgz");

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "next turn", credential: null, resume,
            stdoutChunkCallback: stdoutChunkCallback);

        // agy invoked with --log-file matching the tailed path on the resume path too.
        var tailExec = sandbox.AllExecs.FirstOrDefault(e => e.Argv.Count > 0 && e.Argv[0] == "tail");
        Assert.NotNull(tailExec);
        var tailedPath = tailExec!.Argv[3];
        var agyExec = sandbox.CapturedAgyExec;
        Assert.NotNull(agyExec);
        var logFileIdx = IndexOf(agyExec!.Argv, "--log-file");
        Assert.True(logFileIdx >= 0, "expected --log-file in resumed agy argv");
        Assert.Equal(tailedPath, agyExec.Argv[logFileIdx + 1]);

        Assert.Contains("RESOURCE_EXHAUSTED", result.Stderr);
        Assert.Contains("resumed conversation\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_WithStructuredStream_EnvelopesLogLines()
    {
        // Structured-stream transport: the glog is folded into the NDJSON stream
        // as codeybox.stderr envelopes. Failed run so the merge is exercised on
        // both the stream and Stderr.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Line 1\nLine 2",
            agyExitCode: 1
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: true);

        // Streamed as JSON envelopes keyed on the shared envelope type.
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"Line 1\"}\n", streamedChunks);
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"Line 2\"}\n", streamedChunks);
        // The classifier-facing Stderr merge is populated regardless of transport.
        Assert.Contains("Line 1", result.Stderr);
        Assert.Contains("Line 2", result.Stderr);
    }

    private sealed class AntigravityLogCapturingSandbox : ISandbox
    {
        private readonly string _logFileContent;
        private readonly int _agyExitCode;

        public AntigravityLogCapturingSandbox(string logFileContent, int agyExitCode = 0)
        {
            _logFileContent = logFileContent;
            _agyExitCode = agyExitCode;
        }

        public string Id => "fake-antigravity-log-sandbox";
        public List<SandboxExec> AllExecs { get; } = new();
        public SandboxExec? CapturedAgyExec => AllExecs.FirstOrDefault(e => e.Argv.Count > 0 && e.Argv[0] == "agy");

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
            {
                return Task.FromResult(new SandboxExecResult(0, _logFileContent, string.Empty));
            }
            if (exec.Argv.Count > 0 && (exec.Argv[0] == "mkdir" || exec.Argv[0] == "bash"))
            {
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            }
            return Task.FromResult(new SandboxExecResult(_agyExitCode, "stdout-response", _agyExitCode == 0 ? string.Empty : "agy failed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AntigravityCapturingSandbox : ISandbox
    {
        private readonly int _prepExitCode;
        private readonly string _prepStderr;
        private readonly int _exitCode;
        private readonly string _stdout;
        private readonly string _stderr;
        private readonly string? _logFileContent;

        public AntigravityCapturingSandbox(
            int prepExitCode = 0,
            string prepStderr = "",
            int exitCode = 0,
            string stdout = "stdout",
            string stderr = "stderr",
            string? logFileContent = null)
        {
            _prepExitCode = prepExitCode;
            _prepStderr = prepStderr;
            _exitCode = exitCode;
            _stdout = stdout;
            _stderr = stderr;
            _logFileContent = logFileContent;
        }

        public string Id => "fake-antigravity";
        public List<SandboxExec> AllExecs { get; } = new();
        public SandboxExec? CapturedExec => AllExecs.FirstOrDefault(e => e.Argv.Count > 0 && e.Argv[0] == "agy");

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            {
                return Task.FromResult(new SandboxExecResult(_prepExitCode, string.Empty, _prepStderr));
            }
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
            {
                return Task.FromResult(new SandboxExecResult(0, _logFileContent ?? string.Empty, string.Empty));
            }
            return Task.FromResult(new SandboxExecResult(_exitCode, _stdout, _stderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
