using System.Collections.Concurrent;
using System.Text.Json;
using CodeyBox.Agents.Crock;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CrockAgentRunner"/>'s submit→poll lifecycle and
/// for the <see cref="CrockStatusParser"/> terminal-state mapping. The tests
/// drive the runner through a scripted sandbox so the poll loop's mapping of
/// in-progress, succeeded, and failed status outputs can be exercised without
/// a live <c>crock</c> binary.
/// </summary>
public sealed class CrockAgentRunnerTests
{
    [Fact]
    public void Kind_IsCrock()
    {
        Assert.Equal(AgentKind.Crock, new CrockAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Crock_RoundTrips()
    {
        // Pins the kind value so a rename in AgentKind.cs would fail a test
        // here before it ripples through host-side config and DI keying.
        Assert.Equal(AgentKind.Crock, new AgentKind("crock"));
    }

    // --- Status parser terminal-state mapping ----------------------------
    // These cover the explicit acceptance criterion ("one in-progress, one
    // succeeded, one failed") on the bare classifier so the test stays
    // robust to future tweaks to the poll loop's wiring.

    [Fact]
    public void Classify_InProgressOutput_ReturnsInProgress()
    {
        var status = CrockStatusParser.Classify("state: running\ntask-id: task-abc\n");
        Assert.Equal(CrockTaskStateKind.InProgress, status.StateKind);
    }

    [Fact]
    public void Classify_SucceededOutput_ReturnsSucceeded()
    {
        var status = CrockStatusParser.Classify("state: succeeded\nresult: ok\n");
        Assert.Equal(CrockTaskStateKind.Succeeded, status.StateKind);
    }

    [Fact]
    public void Classify_FailedOutput_ReturnsFailed()
    {
        var status = CrockStatusParser.Classify("state: failed\nerror: timeout\n");
        Assert.Equal(CrockTaskStateKind.Failed, status.StateKind);
    }

    [Fact]
    public void Classify_LastStateWinsOverHistorical()
    {
        // The poll loop should resolve to the LAST observed state line; a
        // status containing earlier in-progress history followed by a final
        // 'failed' state is Failed, and an earlier 'failed' history followed
        // by a final 'succeeded' state is Succeeded. Pins the
        // last-state-wins ordering so neither side silently flips.
        var failed = CrockStatusParser.Classify(
            "history:\n - state: running\n - state: failed\ncurrent: failed\n");
        Assert.Equal(CrockTaskStateKind.Failed, failed.StateKind);

        var succeeded = CrockStatusParser.Classify(
            "history:\n - state: running\n - state: failed\ncurrent: succeeded\n");
        Assert.Equal(CrockTaskStateKind.Succeeded, succeeded.StateKind);
    }

    [Fact]
    public void Classify_EmptyOutput_ReturnsUnknown()
    {
        Assert.Equal(CrockTaskStateKind.Unknown, CrockStatusParser.Classify("").StateKind);
        Assert.Equal(CrockTaskStateKind.Unknown, CrockStatusParser.Classify(null).StateKind);
    }

    // --- Status parser false-positive surface ---------------------------
    // The earlier blob-scanning parser short-circuited Failed on any blob
    // containing the bare word 'error' (e.g. 'last error: none') and
    // Succeeded on any blob containing 'ok' / 'done' (e.g. 'result: ok').
    // The tightened state-prefix anchoring eliminates that surface; these
    // tests pin the new behavior so a future loosening regresses loudly.

    [Theory]
    [InlineData("state: running\nlast error: none\n")]
    [InlineData("state: running\nerror_count: 0\n")]
    [InlineData("state: running\nlast_error: \"\"\n")]
    [InlineData("state: running\nFailed attempts: 0\n")]
    [InlineData("state: running\ncancelled_subtasks: 0\n")]
    [InlineData("state: running\ntimed out: never\n")]
    public void Classify_InProgressWithDiagnosticErrorWords_StaysInProgress(string blob)
    {
        Assert.Equal(CrockTaskStateKind.InProgress, CrockStatusParser.Classify(blob).StateKind);
    }

    [Theory]
    [InlineData("state: running\nresult: ok\n")]
    [InlineData("state: running\ntokens done so far: 8\n")]
    [InlineData("state: running\nSetup is done, now batching...\n")]
    [InlineData("state: running\nconnection: ok\n")]
    public void Classify_InProgressWithDiagnosticSuccessWords_StaysInProgress(string blob)
    {
        Assert.Equal(CrockTaskStateKind.InProgress, CrockStatusParser.Classify(blob).StateKind);
    }

    [Fact]
    public void Classify_JsonStateFieldShape_IsRecognised()
    {
        // JSON-style status output — '"state":"value"' separated by ':' and
        // wrapped in double quotes — should classify the same as the
        // 'state: value' shape so the parser is robust to CLI re-shaping.
        Assert.Equal(CrockTaskStateKind.Succeeded,
            CrockStatusParser.Classify("{\"state\":\"succeeded\"}").StateKind);
        Assert.Equal(CrockTaskStateKind.Failed,
            CrockStatusParser.Classify("{\"state\":\"failed\"}").StateKind);
        Assert.Equal(CrockTaskStateKind.InProgress,
            CrockStatusParser.Classify("{\"state\":\"running\",\"connection\":\"ok\"}").StateKind);
    }

    [Fact]
    public void Classify_BareBenignWordsWithoutStatePrefix_AreUnknown()
    {
        // Free-form CLI noise without a state declaration line never resolves
        // to a terminal kind — including outputs that incidentally mention
        // 'ok', 'done', or 'error' but never as a state field. Pins the
        // 'state prefix required' contract so the loose-keyword surface
        // cannot regress without a failing test.
        Assert.Equal(CrockTaskStateKind.Unknown,
            CrockStatusParser.Classify("crock daemon: ok\n").StateKind);
        Assert.Equal(CrockTaskStateKind.Unknown,
            CrockStatusParser.Classify("Done loading config\n").StateKind);
        Assert.Equal(CrockTaskStateKind.Unknown,
            CrockStatusParser.Classify("connection error: retrying\n").StateKind);
    }

    // --- Task-id extraction -----------------------------------------------

    [Fact]
    public void TryExtractTaskId_LabeledLine_ReturnsId()
    {
        Assert.Equal("abc123",
            CrockStatusParser.TryExtractTaskId("submitted!\nTask-Id: abc123\n"));
    }

    [Fact]
    public void TryExtractTaskId_BareTaskPrefix_ReturnsId()
    {
        Assert.Equal("task-9f8a7b",
            CrockStatusParser.TryExtractTaskId("task-9f8a7b\n"));
    }

    [Fact]
    public void TryExtractTaskId_BareUuid_ReturnsId()
    {
        // Pins the UUID branch of BareTaskIdPattern that ships untested.
        const string uuid = "1234abcd-5678-90ef-1234-567890abcdef";
        Assert.Equal(uuid, CrockStatusParser.TryExtractTaskId($"submitted\n{uuid}\n"));
    }

    [Fact]
    public void TryExtractTaskId_NoMatch_ReturnsNull()
    {
        Assert.Null(CrockStatusParser.TryExtractTaskId(""));
        Assert.Null(CrockStatusParser.TryExtractTaskId(null));
    }

    [Theory]
    [InlineData("ok\n")]
    [InlineData("done\n")]
    [InlineData("bye\n")]
    [InlineData("v1.2.3\n")]
    [InlineData("--anthropic-api-key\n")]
    [InlineData("-h\n")]
    [InlineData("Submitted\nDetached\n")]
    public void TryExtractTaskId_InnocuousTrailingText_ReturnsNull(string stdout)
    {
        // The old permissive last-line fallback would return any
        // alphanumeric/-/_/. token. That risked feeding argv-injection
        // candidates (e.g. '--anthropic-api-key') back into 'crock status',
        // and silently polling fabricated ids for innocuous CLI tails. The
        // tightened extractor must return null on every shape above so the
        // runner fails the work item cleanly.
        Assert.Null(CrockStatusParser.TryExtractTaskId(stdout));
    }

    // --- Runner end-to-end: submit + scripted polls ----------------------

    private static AgentCredential CrockCred(string configJson = "{}")
        => new(
            AgentKind.Crock,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CrockAgentRunner.ConfigEnvVar] = configJson,
            },
            new Dictionary<string, string>());


    [Fact]
    public async Task RunAsync_NoCredential_FailsWithoutExecutingSubmit()
    {
        var sandbox = new ScriptedSandbox(submit: ("task-abc", 0));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.False(result.Success);
        Assert.Contains(CrockAgentRunner.ConfigEnvVar, result.Summary, StringComparison.Ordinal);
        Assert.False(sandbox.SubmitExecuted);
    }

    [Fact]
    public async Task RunAsync_WhitespaceOnlyCredential_FailsLikeMissing()
    {
        // PrepareSandboxAsync short-circuits on IsNullOrWhiteSpace, not just
        // on the env var being absent. A whitespace-only payload must hit
        // the same unavailability shape rather than crashing inside the
        // materialise script.
        var sandbox = new ScriptedSandbox(submit: ("task-abc", 0));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt",
            credential: CrockCred(configJson: "   \n\t  "));

        Assert.False(result.Success);
        Assert.Contains(CrockAgentRunner.ConfigEnvVar, result.Summary, StringComparison.Ordinal);
        Assert.False(sandbox.SubmitExecuted);
    }

    [Fact]
    public async Task RunAsync_MissingCredential_ClassifiesAsAuthError()
    {
        // The unavailability shape must match the shared classifier's auth
        // patterns; otherwise the orchestrator would treat a missing
        // credential as a Normal work-item failure rather than routing it
        // through the auth-recovery / class-walk path. The runner achieves
        // this by routing the marker through Stderr (which the shared
        // classifier scans for "credentials are invalid").
        var sandbox = new ScriptedSandbox(submit: ("task-abc", 0));
        var runner = new CrockAgentRunner();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var classification = AgentFailureClassifier.Classify(
            result.Stderr, result.Stdout, result.Summary);
        Assert.Equal(AgentFailureKind.AuthError, classification.Kind);
    }

    [Fact]
    public async Task RunAsync_MaterialiseScriptFailure_ReturnsInfrastructureShape()
    {
        // A non-zero exit from the auth materialise step must surface as a
        // structured failure (not a thrown exception) and use the
        // "failed to materialise" summary the shared classifier recognises.
        var sandbox = new ScriptedSandbox(
            submit: ("task-abc", 0),
            authExit: (stdout: "", stderr: "permission denied", exit: 17));
        var runner = MakeRunnerWithZeroDelays();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("failed to materialise crock config", result.Summary, StringComparison.Ordinal);
        Assert.Equal("permission denied", result.Stderr);
        Assert.False(sandbox.SubmitExecuted);

        var classification = AgentFailureClassifier.Classify(
            result.Stderr, result.Stdout, result.Summary);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task RunAsync_SubmitFailure_ReturnsFailedResult()
    {
        // Non-zero exit on `crock submit` is a hard failure of the work item
        // — the runner must NOT proceed to poll a synthetic task-id.
        var sandbox = new ScriptedSandbox(submit: ("", 13));
        var runner = MakeRunnerWithZeroDelays();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("crock submit failed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_SubmitNoTaskId_FailsAndDoesNotPoll()
    {
        // Submit succeeded but emitted no parseable task-id; the runner
        // must fail rather than fabricate one.
        var sandbox = new ScriptedSandbox(submit: ("nothing here\n", 0));
        var runner = MakeRunnerWithZeroDelays();

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("task-id", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_FastSucceededPoll_ReturnsSuccess()
    {
        // First poll returns SUCCEEDED -> runner resolves the work item
        // positively and surfaces the status stdout through AgentResult.
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-fast\n", 0),
            statuses: new[] { ("state: succeeded\nresult: ok\n", 0) });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.True(result.Success);
        Assert.Contains("succeeded", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_InProgressThenSucceeded_KeepsPollingUntilTerminal()
    {
        // Pins the in-progress branch: at least one running poll must occur
        // before the runner resolves on the succeeded poll.
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-slow\n", 0),
            statuses: new[]
            {
                ("state: running\n", 0),
                ("state: running\n", 0),
                ("state: completed\n", 0),
            });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.True(result.Success);
        Assert.Equal(3, sandbox.StatusPollCount);
    }

    [Fact]
    public async Task RunAsync_FailedPoll_ReturnsFailedAgentResult()
    {
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-doomed\n", 0),
            statuses: new[] { ("state: failed\nerror: model_error\n", 0) });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("failed", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_UnknownStreakExceeded_FailsWithStreakSummary()
    {
        // When the CLI repeatedly returns output the parser cannot classify,
        // the runner must terminate at MaxUnknownStreak rather than poll
        // forever. Uses a low cap so the test wall-clock stays tight.
        var runner = new CrockAgentRunner
        {
            InitialPollInterval = TimeSpan.FromMilliseconds(1),
            MaxPollInterval = TimeSpan.FromMilliseconds(1),
            MaxUnknownStreak = 3,
            SandboxOptions = static () => new CrockSandboxOptions
            {
                HostDaemonSocketPath = "/tmp/test-crock-daemon.sock",
            },
        };
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-mute\n", 0),
            // No state declaration -> Unknown on every poll.
            statuses: new[] { ("daemon banner\n", 0) });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        Assert.Contains("consecutive unknown states", result.Summary, StringComparison.Ordinal);
        Assert.Equal(3, sandbox.StatusPollCount);
    }

    [Fact]
    public async Task RunAsync_InProgressResetsUnknownStreak()
    {
        // Pins the streak-reset contract: an InProgress observation in the
        // middle of an Unknown run must reset the counter so a healthy task
        // that occasionally prints unparseable diagnostics doesn't trip
        // the cap. With cap=3, two consecutive Unknown observations stay
        // below the trip point; the InProgress poll then resets the
        // counter so a third Unknown later still has runway.
        var runner = new CrockAgentRunner
        {
            InitialPollInterval = TimeSpan.FromMilliseconds(1),
            MaxPollInterval = TimeSpan.FromMilliseconds(1),
            MaxUnknownStreak = 3,
            SandboxOptions = static () => new CrockSandboxOptions
            {
                HostDaemonSocketPath = "/tmp/test-crock-daemon.sock",
            },
        };
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-bumpy\n", 0),
            statuses: new[]
            {
                ("daemon banner\n", 0),       // unknown #1
                ("daemon banner\n", 0),       // unknown #2 (still below cap=3)
                ("state: running\n", 0),      // RESET
                ("daemon banner\n", 0),       // unknown #1 again
                ("state: succeeded\n", 0),    // terminal
            });

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.True(result.Success);
        Assert.Equal(5, sandbox.StatusPollCount);
    }

    [Fact]
    public async Task RunAsync_CancellationMidPoll_ReturnsStructuredFailure()
    {
        // Cancellation through the poll loop must surface as a structured
        // AgentResult, not an unhandled OperationCanceledException, so
        // callers see a consistent shape regardless of which await site
        // wins the race.
        var runner = MakeRunnerWithZeroDelays();
        using var cts = new CancellationTokenSource();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-cancel\n", 0),
            statuses: new[] { ("state: running\n", 0) },
            onStatusPoll: () => cts.Cancel());

        var result = await runner.RunAsync(sandbox, "/work", "prompt",
            credential: CrockCred(), ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task-cancel", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ProgressCallback_FiresOnEachPoll()
    {
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-stream\n", 0),
            statuses: new[]
            {
                ("state: running\n", 0),
                ("state: succeeded\n", 0),
            });
        var chunks = new ConcurrentQueue<string>();

        var result = await runner.RunAsync(sandbox, "/work", "prompt",
            credential: CrockCred(), stdoutChunkCallback: chunks.Enqueue);

        Assert.True(result.Success);
        // Submission + 2 polls = 3 progress envelopes minimum.
        Assert.True(chunks.Count >= 3, $"expected ≥3 progress chunks, got {chunks.Count}");
        // Each envelope must be valid newline-terminated JSONL carrying the
        // expected discriminator; otherwise downstream stream consumers would
        // misframe the data even though the marker substring is present.
        foreach (var chunk in chunks)
        {
            Assert.EndsWith("\n", chunk);
            using var doc = JsonDocument.Parse(chunk.TrimEnd('\n'));
            Assert.Equal("codeybox.crock.progress",
                doc.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task RunAsync_PromptIsPassedOnStdin_NotArgv()
    {
        // MAX_ARG_STRLEN is 128 KiB on Linux; rework prompts can exceed it.
        // The runner must put the prompt on the SandboxExec.Stdin channel
        // for the submit step, mirroring the other CLI runners.
        const string prompt = "do the thing in great detail";
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-stdin\n", 0),
            statuses: new[] { ("state: succeeded\n", 0) });

        await runner.RunAsync(sandbox, "/work", prompt, credential: CrockCred());

        Assert.NotNull(sandbox.CapturedSubmitExec);
        Assert.DoesNotContain(prompt, sandbox.CapturedSubmitExec!.Argv);
        Assert.Equal(prompt, sandbox.CapturedSubmitExec!.Stdin);
        Assert.Equal("crock", sandbox.CapturedSubmitExec!.Argv[0]);
        Assert.Equal("submit", sandbox.CapturedSubmitExec!.Argv[1]);
    }

    [Fact]
    public async Task RunAsync_StatusArgv_UsesDashDashSeparatorBeforeTaskId()
    {
        // Defence-in-depth: a `--` argv separator before the task-id ensures
        // `crock status` cannot interpret a dash-prefixed token (which the
        // parser already rejects) as a flag.
        var runner = MakeRunnerWithZeroDelays();
        var sandbox = new ScriptedSandbox(
            submit: ("task-id: task-flag\n", 0),
            statuses: new[] { ("state: succeeded\n", 0) });

        await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.NotNull(sandbox.CapturedStatusExec);
        Assert.Equal(new[] { "crock", "status", "--", "task-flag" },
            sandbox.CapturedStatusExec!.Argv.ToArray());
    }

    [Fact]
    public async Task RunResumedAsync_FailsExplicitly_DoesNotResubmit()
    {
        // Pins the resume contract: the base class's RunResumedAsync would
        // re-exec `crock submit` once and report success on the bare submit
        // exit (silently double-billing the Anthropic batch quota). Until
        // checkpoint shape is wired, RunResumedAsync must fail explicitly
        // without touching the sandbox.
        var sandbox = new ScriptedSandbox(submit: ("task-id: task-resume\n", 0));
        var runner = new CrockAgentRunner();
        var resume = new AgentResumeContext(CheckpointRef: "checkpoint:abc");

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "prompt", credential: CrockCred(), resume: resume);

        Assert.False(result.Success);
        Assert.Contains("resume not yet supported", result.Summary, StringComparison.Ordinal);
        Assert.False(sandbox.SubmitExecuted);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public void MaterialiseScript_ReferencesConfigEnvVar_StaysInLockStep()
    {
        // The bash script must reference ConfigEnvVar's current value; a
        // rename of the constant must propagate without a separate edit.
        // Also pins the fd-based no-follow security pattern so a regression
        // to a looser mode or symlink-following write is caught.
        Assert.Contains(
            CrockAgentRunner.ConfigEnvVar,
            CrockAgentRunner.ConfigMaterialiseScript,
            StringComparison.Ordinal);
        Assert.Contains("0o600", CrockAgentRunner.ConfigMaterialiseScript, StringComparison.Ordinal);
        Assert.Contains(".crockcode/config.json", CrockAgentRunner.ConfigMaterialiseScript, StringComparison.Ordinal);
        Assert.Contains("O_NOFOLLOW", CrockAgentRunner.ConfigMaterialiseScript, StringComparison.Ordinal);
        Assert.Contains("os.replace(tmp_name, file_name", CrockAgentRunner.ConfigMaterialiseScript, StringComparison.Ordinal);
        Assert.Contains("credential destination parent is a symlink", CrockAgentRunner.ConfigMaterialiseScript, StringComparison.Ordinal);
    }

    // --- In-VM smoke probe step shape -----------------------------------

    [Fact]
    public void CrockInVmSmokeProbe_NoCredential_OnlyRunsBinaryPresenceStep()
    {
        var probe = new CrockInVmSmokeProbe();

        var steps = probe.BuildSteps(credential: null);

        Assert.Single(steps);
        Assert.Equal(new[] { "crock", "--version" }, steps[0].Argv.ToArray());
    }

    [Fact]
    public void CrockInVmSmokeProbe_WithCredential_RunsVersionMaterialiseAndDoctor()
    {
        var probe = new CrockInVmSmokeProbe();
        var credential = new AgentCredential(
            AgentKind.Crock,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CrockAgentRunner.ConfigEnvVar] = "{}",
            },
            new Dictionary<string, string>());

        var steps = probe.BuildSteps(credential);

        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { "crock", "--version" }, steps[0].Argv.ToArray());
        Assert.Equal("bash", steps[1].Argv[0]);
        Assert.Equal("-c", steps[1].Argv[1]);
        // The materialise step must run the same script the runner uses;
        // otherwise drift between the two would silently de-sync the auth
        // path tested by the probe and the auth path used at dispatch.
        Assert.Equal(CrockAgentRunner.ConfigMaterialiseScript, steps[1].Argv[2]);
        Assert.Equal(new[] { "crock", "doctor" }, steps[2].Argv.ToArray());
    }

    [Fact]
    public void CrockInVmSmokeProbe_Kind_IsCrock()
    {
        Assert.Equal(AgentKind.Crock, new CrockInVmSmokeProbe().Kind);
    }

    // --- Quota probe contract --------------------------------------------

    [Fact]
    public async Task CrockQuotaProbe_NoArgCtor_ReturnsUnknownForMissingCredential()
    {
        // The parameter-less constructor is preserved for the scaffold DI
        // registration shape, but it has no HTTP client and no API key. It
        // must still return a structured Unknown snapshot (with NoCredential
        // when no token is supplied) so the router's QuotaUnknownPolicy gates
        // dispatch instead of treating the probe as silently OK.
        var probe = new CrockQuotaProbe();
        var member = new AgentMembership
        {
            Agent = AgentKind.Crock,
            Billing = AgentBilling.PayPerApi,
            QualityScore = 50,
        };

        var snapshot = await probe.GetAvailabilityAsync(member, ct: CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        // The default credentials provider for the scaffold path returns
        // AccessToken=null, so the probe must return NoCredential — not a
        // generic Permanent — so the last-known-good layer discards any
        // stale reading (a missing key is not a "transient blip").
        Assert.Equal(QuotaUnknownReason.NoCredential, snapshot.Unknown);
    }

    [Fact]
    public void CrockQuotaProbe_Kind_IsCrock()
    {
        Assert.Equal(AgentKind.Crock, new CrockQuotaProbe().Kind);
    }

    // --- API-key extraction from CrockCode config JSON -------------------

    [Theory]
    [InlineData("{\"anthropic_api_key\":\"sk-ant-test\",\"tunnel_provider\":\"cloudflared\"}", "sk-ant-test")]
    [InlineData("{\"anthropicApiKey\":\"sk-camel\"}", "sk-camel")]
    [InlineData("{\"ANTHROPIC_API_KEY\":\"sk-upper\"}", "sk-upper")]
    [InlineData("{\"anthropic_api_key\":\"  sk-trimmed  \"}", "sk-trimmed")]
    public void CrockQuotaProbe_TryExtractApiKey_RecognisesKeyVariants(string json, string expected)
    {
        Assert.Equal(expected, CrockQuotaProbe.TryExtractApiKey(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{\"tunnel_provider\":\"cloudflared\"}")]
    [InlineData("{\"anthropic_api_key\":\"\"}")]
    [InlineData("{\"anthropic_api_key\":\"   \"}")]
    [InlineData("not json at all")]
    [InlineData("[\"anthropic_api_key\",\"sk-array\"]")]
    public void CrockQuotaProbe_TryExtractApiKey_ReturnsNullOnAbsentOrInvalid(string? json)
    {
        Assert.Null(CrockQuotaProbe.TryExtractApiKey(json));
    }

    // --- Pre-flight host-daemon gate ------------------------------------

    [Fact]
    public async Task RunAsync_NoHostDaemonConfigured_FailsBeforeSubmit()
    {
        // Dispatching crock without a host-side daemon socket would leave the
        // Anthropic batch worker with no callback path. The runner must
        // refuse to dispatch and surface a clear MissingHostDaemonMarker
        // through Stderr so the failure shows up in lastError instead of as
        // a multi-hour batch hang.
        var sandbox = new ScriptedSandbox(submit: ("task-id: task-x\n", 0));
        var runner = new CrockAgentRunner
        {
            InitialPollInterval = TimeSpan.FromMilliseconds(1),
            MaxPollInterval = TimeSpan.FromMilliseconds(1),
            // No SandboxOptions accessor — defaults to no daemon socket.
        };

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.False(result.Success);
        // Marker prefix is "failed to materialise " so AgentFailureClassifier
        // routes the work item to Infrastructure (operator triage) instead of
        // bench-and-retry.
        Assert.StartsWith(
            "failed to materialise crock host daemon socket",
            result.Summary, StringComparison.Ordinal);
        Assert.Contains("HostDaemonSocketPath", result.Summary, StringComparison.Ordinal);
        Assert.False(sandbox.SubmitExecuted);
        Assert.False(sandbox.StatusPolled);
    }

    [Fact]
    public async Task RunAsync_NoHostDaemonConfigured_StderrCarriesMarker()
    {
        // The pre-flight marker must travel through Stderr (mirroring the
        // missing-credential shape) so the operator sees the same lastError
        // marker class regardless of which guard fired.
        var sandbox = new ScriptedSandbox(submit: ("task-id: task-x\n", 0));
        var runner = new CrockAgentRunner
        {
            InitialPollInterval = TimeSpan.FromMilliseconds(1),
            MaxPollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: CrockCred());

        Assert.NotNull(result.Stderr);
        Assert.StartsWith(
            "failed to materialise crock host daemon socket",
            result.Stderr!, StringComparison.Ordinal);
    }

    // --- Credential-provider behaviour ----------------------------------

    [Fact]
    public async Task CrockCredentialProvider_NoConfig_ReturnsNull()
    {
        var previous = Environment.GetEnvironmentVariable(
            CrockEnvironmentCredentialProvider.HostConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, null);
            var provider = new CrockEnvironmentCredentialProvider(
                static () => new CrockSandboxOptions());
            var cred = await provider.GetAsync(AgentKind.Crock);
            Assert.Null(cred);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, previous);
        }
    }

    [Fact]
    public async Task CrockCredentialProvider_DaemonConfigured_AddsBindMountAndEnvVar()
    {
        var previous = Environment.GetEnvironmentVariable(
            CrockEnvironmentCredentialProvider.HostConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar,
                "{\"anthropic_api_key\":\"sk-test\"}");
            var provider = new CrockEnvironmentCredentialProvider(
                static () => new CrockSandboxOptions
                {
                    HostDaemonSocketPath = "/run/host/crock-daemon.sock",
                    SandboxDaemonSocketPath = "/run/vm/crock-daemon.sock",
                    DaemonSocketEnvVar = "CROCK_DAEMON_SOCKET",
                });

            var cred = await provider.GetAsync(AgentKind.Crock);

            Assert.NotNull(cred);
            // Config JSON ships through the runner's expected env var.
            Assert.Equal("{\"anthropic_api_key\":\"sk-test\"}",
                cred!.EnvironmentVariables[CrockAgentRunner.ConfigEnvVar]);
            // Daemon-socket env var present at the in-VM path.
            Assert.Equal("/run/vm/crock-daemon.sock",
                cred.EnvironmentVariables["CROCK_DAEMON_SOCKET"]);
            // A read-write bind-mount maps the socket's PARENT DIRECTORY.
            // Mounting the socket file itself would be rejected by Multipass
            // (multipass mount --type=native requires a directory source);
            // virtiofs/9p passthrough faithfully exposes the socket node
            // inside the mounted directory so the in-VM connect() reaches
            // the host daemon.
            var mount = Assert.Single(cred.Mounts);
            Assert.Equal("/run/host", mount.HostPath);
            Assert.Equal("/run/vm", mount.SandboxPath);
            Assert.False(mount.ReadOnly);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, previous);
        }
    }

    [Fact]
    public async Task CrockCredentialProvider_NoDaemonConfigured_ShipsConfigWithoutMount()
    {
        // When config is present but no daemon socket is configured, the
        // credential ships so the runner's pre-flight check fires with the
        // expected MissingHostDaemonMarker (not the credential-missing one).
        // Mount list is empty.
        var previous = Environment.GetEnvironmentVariable(
            CrockEnvironmentCredentialProvider.HostConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, "{}");
            var provider = new CrockEnvironmentCredentialProvider(
                static () => new CrockSandboxOptions());

            var cred = await provider.GetAsync(AgentKind.Crock);

            Assert.NotNull(cred);
            Assert.Empty(cred!.Mounts);
            Assert.False(cred.EnvironmentVariables.ContainsKey("CROCK_DAEMON_SOCKET"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, previous);
        }
    }

    [Fact]
    public async Task CrockCredentialProvider_NonCrockAgent_ReturnsNull()
    {
        var provider = new CrockEnvironmentCredentialProvider(
            static () => new CrockSandboxOptions { HostDaemonSocketPath = "/run/host/crock-daemon.sock" });
        Assert.Null(await provider.GetAsync(AgentKind.Claude));
        Assert.Null(await provider.GetAsync(AgentKind.Codex));
    }

    [Theory]
    [InlineData("/crock-daemon.sock")]       // parent → "/" (whole host root!)
    [InlineData("/etc/crock-daemon.sock")]    // parent → "/etc"
    [InlineData("/run/crock-daemon.sock")]    // parent → "/run"
    [InlineData("/var/run/crock-daemon.sock")] // parent → "/var/run"
    [InlineData("/tmp/crock-daemon.sock")]    // parent → "/tmp"
    [InlineData("/root/crock-daemon.sock")]   // parent → "/root"
    [InlineData("/proc/crock-daemon.sock")]   // parent → "/proc"
    [InlineData("/dev/crock-daemon.sock")]    // parent → "/dev"
    public async Task CrockCredentialProvider_ForbiddenParentDirectory_RefusesCredential(string hostSocketPath)
    {
        // Hard catastrophe gate: a misconfigured HostDaemonSocketPath whose
        // parent directory is a system shared root must NOT produce a
        // credential bundle. Returning null forces the runner's pre-flight
        // gate to fire the MissingHostDaemonMarker; the alternative would be
        // bind-mounting / (or /etc, /run, etc.) read-write into the sandbox
        // and exposing every host secret to a prompt-injected agent on a
        // single-character operator typo.
        var previous = Environment.GetEnvironmentVariable(
            CrockEnvironmentCredentialProvider.HostConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, "{}");
            var provider = new CrockEnvironmentCredentialProvider(
                () => new CrockSandboxOptions { HostDaemonSocketPath = hostSocketPath });

            var cred = await provider.GetAsync(AgentKind.Crock);

            Assert.Null(cred);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, previous);
        }
    }

    [Fact]
    public async Task CrockCredentialProvider_RelativeHostSocketPath_RefusesCredential()
    {
        // A bare relative filename (no parent directory) resolves
        // Path.GetDirectoryName to empty; the provider must refuse rather
        // than ship a daemon env var pointing at an unmounted location and
        // letting the in-VM CLI fail late with an opaque connection error.
        var previous = Environment.GetEnvironmentVariable(
            CrockEnvironmentCredentialProvider.HostConfigEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, "{}");
            var provider = new CrockEnvironmentCredentialProvider(
                static () => new CrockSandboxOptions
                {
                    HostDaemonSocketPath = "crock-daemon.sock",
                });

            var cred = await provider.GetAsync(AgentKind.Crock);

            Assert.Null(cred);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CrockEnvironmentCredentialProvider.HostConfigEnvVar, previous);
        }
    }

    [Fact]
    public void CrockCredentialProvider_ForbiddenParentDirectory_PinsAllowedShape()
    {
        // The "good" shape — an operator-dedicated subdirectory under
        // /run/codeybox — must NOT be flagged as forbidden. Pins the
        // contract so a future tightening of the catastrophe gate can't
        // accidentally lock operators out of the documented setup.
        Assert.False(CrockEnvironmentCredentialProvider.IsForbiddenParentDirectory("/run/codeybox"));
        Assert.False(CrockEnvironmentCredentialProvider.IsForbiddenParentDirectory("/var/run/codeybox"));
        Assert.False(CrockEnvironmentCredentialProvider.IsForbiddenParentDirectory("/run/host"));
    }

    // --- Model-list probe contract --------------------------------------

    [Fact]
    public async Task CrockModelListProbe_ReturnsCuratedClaudeModels()
    {
        var probe = new CrockModelListProbe();
        Assert.Equal(AgentKind.Crock, probe.Kind);
        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Null(result.FailureReason);
        Assert.NotEmpty(result.ModelIds);
        // Smoke-check the curated set includes the canonical tiers.
        Assert.Contains(result.ModelIds, m => m.Contains("opus", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ModelIds, m => m.Contains("sonnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ModelIds, m => m.Contains("haiku", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrockKnownModels_IsKnown_HandlesCasingAndNulls()
    {
        Assert.True(CrockKnownModels.IsKnown("claude-opus-4-7"));
        Assert.True(CrockKnownModels.IsKnown("CLAUDE-OPUS-4-7"));
        Assert.False(CrockKnownModels.IsKnown(null));
        Assert.False(CrockKnownModels.IsKnown(""));
        Assert.False(CrockKnownModels.IsKnown("   "));
        Assert.False(CrockKnownModels.IsKnown("not-a-real-model"));
    }

    private static CrockAgentRunner MakeRunnerWithZeroDelays() => new()
    {
        // Sub-tick poll intervals so the test wall-clock stays in microseconds.
        InitialPollInterval = TimeSpan.FromMilliseconds(1),
        MaxPollInterval = TimeSpan.FromMilliseconds(1),
        // Satisfy the runner's host-daemon pre-flight check; tests that
        // specifically exercise the missing-daemon path supply their own
        // runner without this accessor.
        SandboxOptions = static () => new CrockSandboxOptions
        {
            HostDaemonSocketPath = "/tmp/test-crock-daemon.sock",
        },
    };

    /// <summary>
    /// Sandbox that scripts a `crock submit` response and an ordered queue of
    /// `crock status` responses. After the queue is drained the sandbox keeps
    /// repeating the last status to mimic a CLI that returns the same
    /// terminal state on every subsequent poll. An optional callback fires on
    /// each status poll so tests can drive cancellation mid-loop.
    /// </summary>
    private sealed class ScriptedSandbox : ISandbox
    {
        private readonly (string Stdout, int ExitCode) _submit;
        private readonly Queue<(string Stdout, int ExitCode)> _statuses;
        private readonly (string Stdout, string Stderr, int Exit) _authExit;
        private readonly Action? _onStatusPoll;
        private (string Stdout, int ExitCode)? _lastStatus;

        public ScriptedSandbox(
            (string Stdout, int ExitCode) submit,
            IEnumerable<(string Stdout, int ExitCode)>? statuses = null,
            (string stdout, string stderr, int exit)? authExit = null,
            Action? onStatusPoll = null)
        {
            _submit = submit;
            _statuses = new Queue<(string, int)>(
                statuses ?? Array.Empty<(string, int)>());
            _authExit = authExit ?? (string.Empty, string.Empty, 0);
            _onStatusPoll = onStatusPoll;
        }

        public string Id => "scripted-crock";
        public bool SubmitExecuted { get; private set; }
        public bool StatusPolled => StatusPollCount > 0;
        public int StatusPollCount { get; private set; }
        public SandboxExec? CapturedSubmitExec { get; private set; }
        public SandboxExec? CapturedStatusExec { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var argv = exec.Argv;
            // Auth materialisation bash script — pass through with configured exit.
            if (CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                    exec, ".crockcode/config.json")
                || CredentialMaterialisationTestHelper.IsEnvironmentMaterialisation(
                    exec, CrockAgentRunner.ConfigEnvVar, ".crockcode/config.json"))
            {
                return Task.FromResult(new SandboxExecResult(
                    _authExit.Exit, _authExit.Stdout, _authExit.Stderr));
            }

            if (argv.Count >= 2 && argv[0] == "crock" && argv[1] == "submit")
            {
                SubmitExecuted = true;
                CapturedSubmitExec = exec;
                return Task.FromResult(new SandboxExecResult(_submit.ExitCode, _submit.Stdout, ""));
            }

            if (argv.Count >= 2 && argv[0] == "crock" && argv[1] == "status")
            {
                StatusPollCount++;
                CapturedStatusExec = exec;
                if (_statuses.Count > 0)
                {
                    _lastStatus = _statuses.Dequeue();
                }
                var resp = _lastStatus ?? ("state: running\n", 0);
                _onStatusPoll?.Invoke();
                return Task.FromResult(new SandboxExecResult(resp.ExitCode, resp.Stdout, ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
