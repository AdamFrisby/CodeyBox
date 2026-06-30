using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AntigravityAgentRunner"/>. Uses local sandbox
/// fakes to inspect the argv, stdin, resume shape, structured-stream probe,
/// and the extra glog-capture execs around the agy invocation.
/// </summary>
public sealed class AntigravityAgentRunnerTests
{
    private const string StructuredStreamProbePrompt =
        "Reply with exactly CODEYBOX_STRUCTURED_STREAM_PROBE. Do not inspect or modify files.";

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
            .Where(e => e.Argv.Count > 0 && e.Argv[0] != "tail" && e.Argv[0] != "mkdir"
                // The .git/info/exclude glog-leak guard (ExcludeGlogFromWorkTreeGitAsync)
                // is capture infrastructure, not a prep/agy exec — filter it too.
                && !(e.Argv[0] == "sh" && e.Argv.Count >= 3 && e.Argv[2].Contains(".git/info/exclude")))
            .ToList();

    [Fact]
    public async Task RunAsync_WhenCaptureStructuredStreamTrue_AppendsOutputFormatStreamJson()
    {
        AntigravityAgentRunner.ClearStructuredStreamSupportCacheForTests();
        // The runner enables structured capture only after a real print-mode
        // probe emits NDJSON. Help text alone is not enough because agy 1.0.x
        // can mention stream-json in unrelated help text while rejecting the
        // flag for --print.
        var sandbox = new AntigravityCapturingSandbox
        {
            VersionOutput = "agy version test-supported",
            HelpOutput = "Usage: agy --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };
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
    public async Task RunAsync_WhenHelpMentionsStreamJsonButPrintModeEmitsUsage_OmitsOutputFormatStreamJson()
    {
        AntigravityAgentRunner.ClearStructuredStreamSupportCacheForTests();
        var sandbox = new AntigravityCapturingSandbox(stdout: "plain output", stderr: "")
        {
            VersionOutput = "agy version test-broken-help",
            HelpOutput = "Usage: agy --output-format stream-json",
            StructuredProbeOutput = """
                Available subcommands:
                  install   Configure environment paths and shell settings
                  models    List available models
                """,
        };
        var runner = new AntigravityAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
        Assert.Contains("structured stream capture was disabled", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_WhenHelpDoesNotAdvertiseStreamJson_OmitsOutputFormatStreamJson()
    {
        AntigravityAgentRunner.ClearStructuredStreamSupportCacheForTests();
        var sandbox = new AntigravityCapturingSandbox
        {
            VersionOutput = "agy version test-no-flag",
            HelpOutput = "Usage: agy",
        };
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            captureStructuredStream: true);

        Assert.DoesNotContain("--output-format", sandbox.CapturedExec!.Argv);
        Assert.DoesNotContain("stream-json", sandbox.CapturedExec!.Argv);
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
    public async Task RunAsync_FailedRun_FoldsTerminalErrorIntoStderr_AndArchivesFullGlogToStream()
    {
        // Un-blinding the classifiers on the FAILURE path: agy writes its terminal
        // RESOURCE_EXHAUSTED ONLY to its glog (process stderr is ~0 bytes), so the
        // quota detector — which substring-scans result.Stderr — could never see it
        // before. On failure the runner folds the glog's TERMINAL error region into
        // Stderr so the item can finally park in WaitingForQuotaReset off an agy
        // cap. The FULL glog is still archived to the observability stream.
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

        // 3. The TERMINAL error region IS folded into the classifier-facing Stderr
        //    so the quota detector sees the 429 — the whole point of the task. agy's
        //    own process stderr ("agy failed") is preserved ahead of it, and the
        //    NON-terminal noise line ("Model resolved: …", before the terminal
        //    marker) is excluded so only the terminal cause reaches the classifiers.
        Assert.Contains("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.Contains("agy failed", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("Model resolved: gemini-3.5-flash", result.Stderr ?? string.Empty);

        // 4. The FULL glog (including the non-terminal line) is archived to the
        //    stream for observability / audit.
        Assert.Contains("Model resolved: gemini-3.5-flash\n", streamedChunks);
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_FailedRun_RecoveredEarly429ThenNonQuotaFailure_DoesNotFoldIntoStderr()
    {
        // Classifier-safety, the exact false-positive the design guards against: agy
        // hits a 429 early, retries past it, does a lot more work (many glog lines),
        // then FAILS for an unrelated reason (a timeout with no known marker). The
        // recovered-then-cleared 429 has scrolled OUT of the terminal window, so it
        // is NOT folded into Stderr and cannot falsely park/bench the member. The
        // full glog is still archived to the stream.
        var lines = new List<string> { "RESOURCE_EXHAUSTED (code 429): recovered after retry" };
        for (var i = 0; i < 60; i++)
            lines.Add($"tool call {i}: edited file src/module_{i}.cs");
        lines.Add("Error: timed out waiting for response");
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: string.Join("\n", lines),
            agyExitCode: 1);
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: false);

        // The early recovered 429 is outside the terminal window (the run ended on a
        // markerless timeout) so it never reaches the classifiers.
        Assert.DoesNotContain("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.Equal("agy failed", result.Stderr);
        // Still archived in full to the stream.
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): recovered after retry\n", streamedChunks);
        Assert.Contains("Error: timed out waiting for response\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_WithOrchestratorAssignedLogPath_DerivesAgyLogPathFromAssignedPath()
    {
        // PRODUCTION shape: in the real pipeline the orchestrator always sets
        // AgentInvocationLogContext.CurrentLogPath before dispatch, so
        // ComputeAgyLogPath takes the "<assigned>.agy.log" branch — never the
        // Guid fallback the other tests exercise. Pin the assigned-path
        // derivation (the shape that actually ships): a regression in the
        // suffix concatenation or its correlation with the orchestrator log
        // path would otherwise pass every test. Mirrors the covering pattern in
        // CliAgentLogFileEnvInjectionTests (BeginScope around the invocation).
        const string assignedLogPath = "/work/.codeybox/agent-logs/agent-4242.log";
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Model resolved: gemini-3.5-flash\nRESOURCE_EXHAUSTED (code 429): Individual quota reached",
            agyExitCode: 1);
        var runner = new AntigravityAgentRunner();

        using (AgentInvocationLogContext.BeginScope(assignedLogPath))
        {
            await runner.RunAsync(sandbox, "/work", "go", credential: null);
        }

        // agy is told to write its glog to the assigned path + ".agy.log"...
        var agyExec = sandbox.CapturedAgyExec;
        Assert.NotNull(agyExec);
        var logFileIdx = IndexOf(agyExec!.Argv, "--log-file");
        Assert.True(logFileIdx >= 0, "expected --log-file in agy argv");
        Assert.Equal(assignedLogPath + ".agy.log", agyExec.Argv[logFileIdx + 1]);

        // ...and tail reads back that same derived path (NOT a Guid fallback).
        var tailExec = sandbox.AllExecs.FirstOrDefault(e => e.Argv.Count > 0 && e.Argv[0] == "tail");
        Assert.NotNull(tailExec);
        Assert.Equal(assignedLogPath + ".agy.log", tailExec!.Argv[3]);
    }

    [Fact]
    public async Task RunAsync_SuccessfulRun_DoesNotMergeGlogIntoStderr()
    {
        // Classifier-safety on SUCCESS: agy's glog is cumulative and records
        // transient errors it later recovered from. On a SUCCESSFUL run there is no
        // terminal failure to classify, so the runner folds NOTHING into
        // result.Stderr — a recovered "RESOURCE_EXHAUSTED" / "API Error: 401" line
        // in the glog can't reach the quota/auth classifiers and falsely bench/park
        // the member. The glog is still archived to the stream for observability.
        // The failure-path fold is pinned by
        // RunAsync_FailedRun_FoldsTerminalErrorIntoStderr_AndArchivesFullGlogToStream.
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
        // Stderr must NOT carry the glog lines: the success-path auth classifier
        // reads Stderr, so a recovered-transient 401/429 there would falsely bench.
        Assert.DoesNotContain("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("applyAuthResult", result.Stderr ?? string.Empty);
        // But the terminal region IS surfaced on the TerminalDiagnostic side-channel
        // even on this exit-0 run — this is the un-blinding that lets the pipeline's
        // no-changes branch classify agy's exit-0 give-up 429 and park the item in
        // WaitingForQuotaReset instead of terminal-failing it as "produced no changes".
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)",
            result.TerminalDiagnostic ?? string.Empty);
        // And the stream archive still surfaces the diagnostics to operators.
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached (Resets in 8m14s)\n", streamedChunks);
        Assert.Contains("applyAuthResult: authMethod=consumer\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_ExitZeroNoMarker_LeavesTerminalDiagnosticNull()
    {
        // A genuine no-op (exit 0, no quota/auth marker anywhere in the glog) must
        // leave TerminalDiagnostic null so the pipeline's no-changes branch finds no
        // detection and still terminal-fails as "produced no changes" — no false park.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Model resolved: gemini-3.5-flash\nread 12 files, wrote 0 files\ndone",
            agyExitCode: 0
        );
        var runner = new AntigravityAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: null, captureStructuredStream: false);

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_CapturedGlog_RedactsGoogleOAuthTokens()
    {
        // agy's auth glog is the most likely place a Google OAuth access/refresh
        // token surfaces in plaintext. The capture must scrub ya29./1// tokens
        // (via the normal RedactText path) before they land in the stream/audit AND
        // before they land in the folded Stderr. The token rides on the terminal
        // error line so the FAILURE-path fold into Stderr is exercised too (a
        // markerless glog would leave Stderr untouched and make the Stderr
        // assertions vacuous).
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "RESOURCE_EXHAUSTED (code 429): quota reached; "
                + "access_token=ya29.aVeryLongGoogleAccessTokenValue0123456789 "
                + "refresh=1//0longRefreshTokenValue0123456789",
            agyExitCode: 1
        );
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: false);

        // The terminal region DID fold into Stderr (proves the assertions aren't
        // vacuous)…
        Assert.Contains("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        // …but the tokens were scrubbed on the way, in BOTH the folded Stderr and
        // the stream copy.
        Assert.DoesNotContain("ya29.aVeryLong", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("1//0longRefresh", result.Stderr ?? string.Empty);
        Assert.DoesNotContain(streamedChunks, chunk => chunk.Contains("ya29.aVeryLong"));
        Assert.DoesNotContain(streamedChunks, chunk => chunk.Contains("1//0longRefresh"));
    }

    [Fact]
    public async Task RunResumedAsync_FailedRun_FoldsTerminalErrorIntoStderr_AndArchivesFullGlogToStream()
    {
        // The resume override has the same log-capture wiring as RunAsync; exercise
        // it on a failed resumed run so the branch is not left unverified —
        // including the same behaviour: full glog to the stream AND the terminal
        // error region folded into the classifier-facing result.Stderr on failure.
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

        // The terminal error region is folded into the classifier-facing Stderr on
        // resume too; the non-terminal "resumed conversation" line is excluded.
        Assert.Contains("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("resumed conversation", result.Stderr ?? string.Empty);
        Assert.Contains("resumed conversation\n", streamedChunks);
        Assert.Contains("RESOURCE_EXHAUSTED (code 429): Individual quota reached\n", streamedChunks);
    }

    [Fact]
    public async Task RunAsync_WithStructuredStream_EnvelopesLogLines()
    {
        // Structured-stream transport: the glog is folded into the NDJSON stream
        // as codeybox.stderr envelopes. Failed run so the archive path is
        // exercised alongside a non-zero agy exit.
        AntigravityAgentRunner.ClearStructuredStreamSupportCacheForTests();
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Line 1\nLine 2",
            agyExitCode: 1
        )
        {
            VersionOutput = "agy version structured-log-failure",
            HelpOutput = "Usage: agy --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: true);

        // Streamed as JSON envelopes keyed on the shared envelope type.
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"Line 1\"}\n", streamedChunks);
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"Line 2\"}\n", streamedChunks);
        // These glog lines carry no quota/auth marker, so the failure-path fold
        // surfaces nothing — Stderr stays agy's own process output ("agy failed").
        Assert.DoesNotContain("Line 1", result.Stderr ?? string.Empty);
        Assert.DoesNotContain("Line 2", result.Stderr ?? string.Empty);
        Assert.Equal("agy failed", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_WithStructuredStream_SuccessfulRun_EnvelopesLogLinesToStreamOnly()
    {
        // Success + structured stream: the glog must still be archived as
        // codeybox.stderr envelopes to the stream while leaving result.Stderr
        // untouched (agy exited 0, empty stderr). Covers the success/structured
        // combination the failed-run test above does not.
        AntigravityAgentRunner.ClearStructuredStreamSupportCacheForTests();
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "applyAuthResult: authMethod=consumer\nModel resolved: gemini-3.5-flash",
            agyExitCode: 0
        )
        {
            VersionOutput = "agy version structured-log-success",
            HelpOutput = "Usage: agy --output-format stream-json",
            StructuredProbeOutput = "{\"type\":\"result\",\"result\":\"ok\"}\n",
        };
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        Action<string> stdoutChunkCallback = chunk => streamedChunks.Add(chunk);

        var result = await runner.RunAsync(
            sandbox, "/work", "do something", credential: null,
            stdoutChunkCallback: stdoutChunkCallback, captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"applyAuthResult: authMethod=consumer\"}\n", streamedChunks);
        Assert.Contains("{\"type\":\"codeybox.stderr\",\"text\":\"Model resolved: gemini-3.5-flash\"}\n", streamedChunks);
        Assert.DoesNotContain("applyAuthResult", result.Stderr ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_CreatesLogDirectory_BeforeInvokingAgy()
    {
        // The mkdir -p that guarantees the glog's parent dir exists is
        // load-bearing: agy's --log-file open fails on a missing directory. Pin
        // that a `mkdir -p <logdir>` exec is dispatched for the log file's parent
        // AND runs before the agy invocation. A regression that dropped or
        // mis-ordered it would break real agy runs while the fakes stayed green.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Model resolved: gemini-3.5-flash",
            agyExitCode: 0);
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        var mkdirIdx = sandbox.AllExecs.FindIndex(
            e => e.Argv.Count >= 3 && e.Argv[0] == "mkdir" && e.Argv[1] == "-p");
        var agyIdx = sandbox.AllExecs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "agy");
        Assert.True(mkdirIdx >= 0, "expected a `mkdir -p` exec for the glog directory");
        Assert.True(agyIdx >= 0, "expected an agy invocation");
        Assert.True(mkdirIdx < agyIdx, "mkdir must run before agy so --log-file can open");
        Assert.Equal("/work/.codeybox/agent-logs", sandbox.AllExecs[mkdirIdx].Argv[2]);
    }

    [Fact]
    public async Task RunAsync_ExcludesGlogScratchFromGit_BeforeInvokingAgy()
    {
        // Leak guard: agy's --log-file glog lands under .codeybox/agent-logs
        // inside the /work git tree and is UNREDACTED on disk (auth material,
        // refresh_token). The rework prompt asks agents to make their own
        // commits, so an agy `git add -A` could bake the glog into an
        // agent-authored commit that the orchestrator's post-run --cached strip
        // can no longer rewrite. The runner must add the scratch dir to
        // .git/info/exclude BEFORE agy runs so `git add -A` skips it. Pin that a
        // sh exec writing the exclude entry is dispatched and runs before agy.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Model resolved: gemini-3.5-flash",
            agyExitCode: 0);
        var runner = new AntigravityAgentRunner();

        await runner.RunAsync(sandbox, "/work", "go", credential: null);

        var excludeIdx = sandbox.AllExecs.FindIndex(e =>
            e.Argv.Count >= 3 && e.Argv[0] == "sh" && e.Argv[1] == "-c"
            && e.Argv[2].Contains(".git/info/exclude")
            && e.Argv[2].Contains(".codeybox/agent-logs/"));
        var agyIdx = sandbox.AllExecs.FindIndex(e => e.Argv.Count > 0 && e.Argv[0] == "agy");
        Assert.True(excludeIdx >= 0, "expected a .git/info/exclude write for the glog scratch dir");
        Assert.True(agyIdx >= 0, "expected an agy invocation");
        Assert.True(excludeIdx < agyIdx, "exclude must be written before agy runs so an agy self-commit skips the glog");
        // The write targets the work tree root, where .git lives.
        Assert.Equal("/work", sandbox.AllExecs[excludeIdx].WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_EmptyGlog_ReturnsResultUnchanged_NoForwardedChunks()
    {
        // Degradation path: agy never wrote a glog (file missing / empty), so tail
        // returns empty stdout. ProcessResultAsync must pass the base result
        // through untouched and forward nothing to the stream — no spurious empty
        // envelope, no crash on the empty split.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: string.Empty,
            agyExitCode: 1);
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        var result = await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => streamedChunks.Add(chunk), captureStructuredStream: false);

        Assert.False(result.Success);
        Assert.Equal("agy failed", result.Stderr);
        Assert.Empty(streamedChunks);
    }

    [Fact]
    public async Task RunAsync_TailExitsNonZero_ReturnsResultUnchanged_NoForwardOrFold()
    {
        // Degradation path: `tail -c N <path>` exits non-zero (missing / rotated
        // glog, permission fault). ProcessResultAsync gates ingestion on
        // `!tailCmd.Success` and must pass the base result through untouched —
        // forwarding NOTHING to the stream and folding NOTHING into Stderr even
        // though tail's stdout carried content (a partial/error read must not be
        // mistaken for glog and reach the classifiers).
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "RESOURCE_EXHAUSTED (code 429): looks like a quota error but tail failed",
            agyExitCode: 1,
            tailExitCode: 1);
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        var result = await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => streamedChunks.Add(chunk), captureStructuredStream: false);

        Assert.False(result.Success);
        Assert.Equal("agy failed", result.Stderr);
        Assert.DoesNotContain("RESOURCE_EXHAUSTED", result.Stderr ?? string.Empty);
        Assert.Empty(streamedChunks);
    }

    [Fact]
    public async Task RunAsync_GlogEndingInNewline_DoesNotForwardTrailingEmptyLine()
    {
        // Real glog files always end in a trailing newline; ForwardLogToStream
        // drops the resulting empty final segment. Every other test authors
        // content WITHOUT a trailing newline, so this production-always branch is
        // otherwise uncovered. Assert the last real line survives and no empty /
        // blank-only chunk is forwarded.
        var sandbox = new AntigravityLogCapturingSandbox(
            logFileContent: "Line A\nLine B\n",
            agyExitCode: 0);
        var runner = new AntigravityAgentRunner();

        var streamedChunks = new List<string>();
        await runner.RunAsync(
            sandbox, "/work", "go", credential: null,
            stdoutChunkCallback: chunk => streamedChunks.Add(chunk), captureStructuredStream: false);

        Assert.Contains("Line A\n", streamedChunks);
        Assert.Contains("Line B\n", streamedChunks);
        Assert.DoesNotContain("\n", streamedChunks);          // no bare-newline (empty-line) chunk
        Assert.DoesNotContain(string.Empty, streamedChunks);  // no empty chunk
    }

    private sealed class AntigravityLogCapturingSandbox : ISandbox
    {
        private readonly string _logFileContent;
        private readonly int _agyExitCode;
        private readonly int _tailExitCode;

        public AntigravityLogCapturingSandbox(string logFileContent, int agyExitCode = 0, int tailExitCode = 0)
        {
            _logFileContent = logFileContent;
            _agyExitCode = agyExitCode;
            _tailExitCode = tailExitCode;
        }

        public string Id => "fake-antigravity-log-sandbox";
        public List<SandboxExec> AllExecs { get; } = new();
        public SandboxExec? CapturedAgyExec => AllExecs.LastOrDefault(IsAgyWorkInvocation);
        public string? HelpOutput { get; init; }
        public string? VersionOutput { get; init; }
        public string? StructuredProbeOutput { get; init; }
        public string? StructuredProbeStderr { get; init; }
        public int StructuredProbeExitCode { get; init; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (VersionOutput is not null && exec.Argv.Contains("--version"))
            {
                return Task.FromResult(new SandboxExecResult(0, VersionOutput, string.Empty));
            }
            if (HelpOutput is not null && exec.Argv.Contains("--help"))
            {
                return Task.FromResult(new SandboxExecResult(0, HelpOutput, string.Empty));
            }
            if (StructuredProbeOutput is not null && IsStructuredStreamProbe(exec))
            {
                return Task.FromResult(new SandboxExecResult(
                    StructuredProbeExitCode,
                    StructuredProbeOutput,
                    StructuredProbeStderr ?? string.Empty));
            }
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
            {
                return Task.FromResult(new SandboxExecResult(_tailExitCode, _logFileContent, string.Empty));
            }
            if (exec.Argv.Count > 0 && (exec.Argv[0] == "mkdir" || exec.Argv[0] == "bash" || exec.Argv[0] == "sh"))
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
        public SandboxExec? CapturedExec => AllExecs.LastOrDefault(IsAgyWorkInvocation);
        public string? HelpOutput { get; init; }
        public string? VersionOutput { get; init; }
        public string? StructuredProbeOutput { get; init; }
        public string? StructuredProbeStderr { get; init; }
        public int StructuredProbeExitCode { get; init; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            AllExecs.Add(exec);
            if (VersionOutput is not null && exec.Argv.Contains("--version"))
            {
                return Task.FromResult(new SandboxExecResult(0, VersionOutput, string.Empty));
            }
            if (HelpOutput is not null && exec.Argv.Contains("--help"))
            {
                return Task.FromResult(new SandboxExecResult(0, HelpOutput, string.Empty));
            }
            if (StructuredProbeOutput is not null && IsStructuredStreamProbe(exec))
            {
                return Task.FromResult(new SandboxExecResult(
                    StructuredProbeExitCode,
                    StructuredProbeOutput,
                    StructuredProbeStderr ?? string.Empty));
            }
            if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            {
                return Task.FromResult(new SandboxExecResult(_prepExitCode, string.Empty, _prepStderr));
            }
            if (exec.Argv.Count > 0 && (exec.Argv[0] == "mkdir" || exec.Argv[0] == "sh"))
            {
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            }
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
            {
                return Task.FromResult(new SandboxExecResult(0, _logFileContent ?? string.Empty, string.Empty));
            }
            return Task.FromResult(new SandboxExecResult(_exitCode, _stdout, _stderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool IsAgyWorkInvocation(SandboxExec exec) =>
        exec.Argv.Count > 0
        && exec.Argv[0] == "agy"
        && exec.Argv.Contains("--print")
        && !IsStructuredStreamProbe(exec);

    private static bool IsStructuredStreamProbe(SandboxExec exec) =>
        string.Equals(exec.Stdin, StructuredStreamProbePrompt, StringComparison.Ordinal);
}

/// <summary>
/// Coverage for <see cref="AntigravityAgentRunner"/>'s glog-capture FAILURE
/// path — the "observable failures" half of the change. When the post-run
/// <c>tail</c> exec throws, <c>ProcessResultAsync</c> must (a) emit the
/// <c>agent.log_capture_failed</c> audit event so a broken diagnostics-capture
/// path is visible rather than silently degrading back to zero diagnostics,
/// and (b) return the base run's result unchanged so the work item is never
/// stranded. A requested cancellation must instead propagate, not be masked as
/// a normal completion. These assertions need the Serilog sink, so the class
/// wires the global logger and shares the <c>GlobalSerilog</c> collection with
/// the other static-logger tests.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AntigravityAgentRunnerLogCaptureFailureTests : IDisposable
{
    private readonly TestSink _sink = new();

    public AntigravityAgentRunnerLogCaptureFailureTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public async Task RunAsync_WhenTailThrows_EmitsAuditAndReturnsResultUnchanged()
    {
        // Force the post-run tail read to throw (a sandbox/provider fault). The
        // runner must catch it, emit agent.log_capture_failed, and hand back the
        // base run's result untouched (no glog merged, run not stranded).
        var sandbox = new TailBehaviourSandbox(
            tailBehaviour: () => throw new IOException("sandbox exec channel died"),
            agyExitCode: 1,
            agyStderr: "original-agy-stderr");
        var runner = new AntigravityAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "do something", credential: null);

        // Result is returned unchanged: failure preserved, stderr NOT augmented
        // with any glog (tail never produced content).
        Assert.False(result.Success);
        Assert.Equal("original-agy-stderr", result.Stderr);

        // The failure is observable via the audit event, carrying the agent kind
        // and the thrown exception's type.
        var evt = Assert.Single(_sink.Events, e => EventName(e) == "agent.log_capture_failed");
        Assert.Equal("antigravity", Scalar(evt, "Agent"));
        Assert.Equal(nameof(IOException), Scalar(evt, "ExceptionType"));
    }

    [Fact]
    public async Task RunAsync_WhenMkdirFails_EmitsAuditAndStillRuns()
    {
        // agy's --log-file open fails if the parent dir is missing, so a failed
        // `mkdir -p` means the whole capture silently yields nothing. Surface it via
        // the same agent.log_capture_failed audit event the tail-failure path uses,
        // rather than degrading invisibly to zero diagnostics. The run still proceeds
        // (agy is still invoked; the tail below returns empty).
        var sandbox = new TailBehaviourSandbox(
            tailBehaviour: () => new SandboxExecResult(0, string.Empty, string.Empty),
            agyExitCode: 0,
            agyStderr: string.Empty,
            mkdirExitCode: 1,
            mkdirStderr: "mkdir: cannot create directory: Read-only file system");
        var runner = new AntigravityAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "do something", credential: null);

        Assert.True(result.Success); // run not stranded by the capture-dir failure

        var evt = Assert.Single(_sink.Events, e => EventName(e) == "agent.log_capture_failed");
        Assert.Equal("antigravity", Scalar(evt, "Agent"));
        Assert.Equal("mkdir", Scalar(evt, "ExceptionType"));
    }

    [Fact]
    public async Task RunAsync_WhenTailCancelled_PropagatesCancellationAndEmitsNoAudit()
    {
        // A requested cancellation surfacing from the tail exec must NOT be
        // swallowed by the generic capture-failure catch (which would mask it as
        // a normal completion). It rethrows, and no log-capture-failed audit is
        // emitted for the cancellation.
        var sandbox = new TailBehaviourSandbox(
            tailBehaviour: () => throw new OperationCanceledException(),
            agyExitCode: 0,
            agyStderr: string.Empty);
        var runner = new AntigravityAgentRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(sandbox, "/work", "do something", credential: null));

        Assert.DoesNotContain(_sink.Events, e => EventName(e) == "agent.log_capture_failed");
    }

    private static string? EventName(LogEvent evt) => Scalar(evt, "EventName");

    private static string? Scalar(LogEvent evt, string key) =>
        evt.Properties.TryGetValue(key, out var prop) && prop is ScalarValue sv
            ? sv.Value as string
            : null;

    // ISandbox fake whose `tail` exec runs an operator-supplied behaviour
    // (throw), letting a test drive the runner's capture-failure catch paths.
    // mkdir/bash succeed; the agy invocation returns the configured exit/stderr.
    private sealed class TailBehaviourSandbox : ISandbox
    {
        private readonly Func<SandboxExecResult> _tailBehaviour;
        private readonly int _agyExitCode;
        private readonly string _agyStderr;
        private readonly int _mkdirExitCode;
        private readonly string _mkdirStderr;

        public TailBehaviourSandbox(
            Func<SandboxExecResult> tailBehaviour,
            int agyExitCode,
            string agyStderr,
            int mkdirExitCode = 0,
            string mkdirStderr = "")
        {
            _tailBehaviour = tailBehaviour;
            _agyExitCode = agyExitCode;
            _agyStderr = agyStderr;
            _mkdirExitCode = mkdirExitCode;
            _mkdirStderr = mkdirStderr;
        }

        public string Id => "fake-tail-behaviour";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
                return Task.FromResult(_tailBehaviour());
            if (exec.Argv.Count > 0 && exec.Argv[0] == "mkdir")
                return Task.FromResult(new SandboxExecResult(_mkdirExitCode, string.Empty, _mkdirStderr));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            return Task.FromResult(new SandboxExecResult(_agyExitCode, "stdout-response", _agyStderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
