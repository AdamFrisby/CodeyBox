using CodeyBox.Agents.Crush;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushAgentRunner"/>. Argv pins encode the transport
/// decision verified against @charmland/crush 0.95.0: <c>run -q
/// -m &lt;model&gt;</c> (NO <c>--yolo</c> — root-only, rejected by
/// <c>run</c> — and no approval flag, because <c>run</c> already
/// auto-approves), the prompt on stdin with no positional argument
/// (MAX_ARG_STRLEN), <c>CRUSH_DISABLE_METRICS=1</c> on every dispatch, and
/// the dispatch model resolved from the explicit member model else the
/// config-sourced default. Quarantine tests use the scripted
/// <c>CrushDispatchSandbox</c> below: repo-local <c>.crushrc</c>/<c>crushrc</c>
/// (executed as Bash at startup — verified live) must move aside before
/// dispatch and restore afterwards, and any quarantine failure must fail
/// closed before the CLI ever starts.
/// </summary>
public sealed class CrushAgentRunnerTests
{
    private const string FreeModel = "openrouter/nvidia/nemotron-3.5-lightning:free";

    private static CrushAgentRunner Runner(AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults);

    private static CrushAgentRunner RunnerWithDefault(string model = FreeModel) =>
        Runner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["crush"] = model }));

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Crush,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    private static SandboxExec CrushExec(CrushDispatchSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");

    [Fact]
    public void Kind_IsCrush()
    {
        Assert.Equal(AgentKind.Crush, new CrushAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Crush_RoundTrips()
    {
        Assert.Equal(AgentKind.Crush, new AgentKind("crush"));
    }

    [Fact]
    public async Task RunAsync_Argv_UsesRunQuietModelTransport()
    {
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = CrushExec(sandbox).Argv.ToList();
        Assert.Equal(["crush", "run", "-q", "-m", FreeModel], argv);
        Assert.DoesNotContain("--yolo", argv);
        Assert.DoesNotContain("--format", argv);
        Assert.DoesNotContain("--reasoning-effort", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: crush run accepts a stdin-only prompt (no
        // positional argument).
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        var crush = CrushExec(sandbox);
        Assert.Equal(prompt, crush.Stdin);
        Assert.DoesNotContain(crush.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Dispatch_CarriesMetricsOptOut()
    {
        // Telemetry is on by default; automated sandbox runs must not phone
        // home. The variable rides the dispatch environment, never argv.
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var crush = CrushExec(sandbox);
        Assert.NotNull(crush.ExtraEnvironment);
        Assert.Equal("1", crush.ExtraEnvironment[CrushAgentRunner.MetricsOptOutVariable]);
        Assert.DoesNotContain(crush.Argv, a => a.Contains("METRICS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_WinsOverDefault()
    {
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();
        const string explicitModel = "openrouter/z-ai/glm-5.2:free";

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: explicitModel);

        var argv = CrushExec(sandbox).Argv.ToList();
        Assert.Contains(explicitModel, argv);
        Assert.DoesNotContain(FreeModel, argv);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_FailsFastWithoutDispatch()
    {
        // Neither member nor default supplies a model: the CLI would fall
        // back to its own paid default and fail as quota confusion on a
        // :free-only key. A model id is never invented here.
        var sandbox = new CrushDispatchSandbox();
        var runner = Runner();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("CodeyBox:AgentDefaults[crush]", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");
    }

    [Fact]
    public async Task RunAsync_Argv_NeverMapsReasoningMode()
    {
        // --reasoning-effort levels are model-dependent and unsupported
        // values are rejected at dispatch; emitting one could fail runs.
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), reasoningMode: "high");

        var argv = CrushExec(sandbox).Argv.ToList();
        Assert.DoesNotContain("--reasoning-effort", argv);
        Assert.DoesNotContain("--effort", argv);
        Assert.DoesNotContain("--thinking", argv);
    }

    [Fact]
    public async Task RunAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        // No OPENROUTER_API_KEY in the bundle: the runner must fail here
        // naming the host variable rather than dispatch into a provider
        // call that can only fail at request time.
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Crush, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "x", empty);

        Assert.False(result.Success);
        Assert.Contains("CODEYBOX_CRUSH_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");
    }

    [Fact]
    public async Task RunAsync_NonRootedWorkingDirectory_FailsClosedWithoutExecs()
    {
        // Without a rooted guest path the crushrc quarantine cannot run, so
        // dispatching would risk executing untrusted repo shell — refuse.
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "relative/path", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("quarantine", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sandbox.Execs);
    }

    [Fact]
    public async Task RunAsync_QuotaFailure_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (@charmland/crush 0.95.0, $0-spend-limit key
        // against a paid model): exit 1, styled ERROR block on stderr.
        const string stderr =
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).\n" +
            "Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058.";
        var sandbox = new CrushDispatchSandbox { CrushExitCode = 1, CrushStderr = stderr, CrushStdout = "\n" };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Key limit exceeded", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoProvidersConfigured_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (bare machine, no key): exit 1.
        var sandbox = new CrushDispatchSandbox
        {
            CrushExitCode = 1,
            CrushStderr = "No providers configured - please run 'crush' to set up a provider interactively.",
            CrushStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("No providers configured", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        // Recorded real output (@charmland/crush 0.95.0, plain-text reply):
        // free text is not a failure.
        var sandbox = new CrushDispatchSandbox { CrushStdout = "hello-crush-ok\n", CrushStderr = string.Empty };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_EmptyReply_LeavesTerminalDiagnosticNull()
    {
        // A run whose work landed in files may reply with empty stdout
        // (exit 0). Empty output is success, not failure.
        var sandbox = new CrushDispatchSandbox { CrushStdout = string.Empty, CrushStderr = string.Empty };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailureNamingCause()
    {
        // A guest without the CLI must surface as an infrastructure failure
        // naming the cause — never as "no changes". The shell reports the
        // missing binary as exit 127 + command-not-found.
        var sandbox = new CrushDispatchSandbox
        {
            CrushExitCode = 127,
            CrushStderr = "bash: line 1: crush: command not found",
            CrushStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("crush", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public async Task RunAsync_QuarantinesPresentCrushrc_BeforeDispatchAndRestoresAfter()
    {
        // A repo shipping .crushrc (executed as Bash at startup — verified
        // live) must move aside before the CLI starts and come back after,
        // with dispatch proceeding normally in between.
        var sandbox = new CrushDispatchSandbox();
        sandbox.Files.Add("/work/.crushrc");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        var moves = sandbox.Execs.Where(e =>
            e.Argv.Count == 4 && e.Argv[0] == "mv" && (e.Argv[2] == "/work/.crushrc" || e.Argv[3] == "/work/.crushrc")).ToList();
        Assert.Equal(2, moves.Count);
        Assert.StartsWith("/work/.crushrc.codeybox-quarantined-", moves[0].Argv[3], StringComparison.Ordinal);
        Assert.EndsWith("/work/.crushrc", moves[1].Argv[3], StringComparison.Ordinal);
        // Quarantine precedes dispatch; restore follows it.
        Assert.True(sandbox.Execs.IndexOf(moves[0]) < sandbox.Execs.IndexOf(CrushExec(sandbox)));
        Assert.True(sandbox.Execs.IndexOf(CrushExec(sandbox)) < sandbox.Execs.IndexOf(moves[1]));
        // Restored: the repo file is back, the backup is gone, and no
        // restore note pollutes stderr.
        Assert.Contains("/work/.crushrc", sandbox.Files);
        Assert.DoesNotContain(sandbox.Files, f => f.StartsWith("/work/.crushrc.codeybox-quarantined-", StringComparison.Ordinal));
        Assert.DoesNotContain("quarantine", result.Stderr ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_QuarantinesBothCrushrcNames()
    {
        // Both ./.crushrc and ./crushrc execute at startup — quarantining
        // only the dotted form would leave the other live.
        var sandbox = new CrushDispatchSandbox();
        sandbox.Files.Add("/work/.crushrc");
        sandbox.Files.Add("/work/crushrc");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Contains("/work/.crushrc", sandbox.Files);
        Assert.Contains("/work/crushrc", sandbox.Files);
        Assert.DoesNotContain(sandbox.Files, f => f.Contains(".codeybox-quarantined-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_AbsentCrushrc_CostsOnlyProbes()
    {
        // No repo-local shell present: quarantine is two cheap `test -f`
        // probes and no `mv` at all.
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "mv");
        Assert.Equal(2, sandbox.Execs.Count(e => e.Argv.Count > 0 && e.Argv[0] == "test"));
    }

    [Fact]
    public async Task RunAsync_QuarantineMoveFailure_FailsClosedWithoutDispatch()
    {
        // A present file that cannot move: dispatching with live repo shell
        // is worse than refusing the run.
        var sandbox = new CrushDispatchSandbox { FailMoves = true };
        sandbox.Files.Add("/work/.crushrc");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("quarantine", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");
    }

    [Fact]
    public async Task RunAsync_RestoreSkippedWhenFileReappears_NotesButPreservesOutcome()
    {
        // Something (the agent or the CLI) recreated the path during the
        // run: the run's own outcome stands and the unrestored backup is
        // named visibly rather than overwriting run output or failing the
        // item over repo hygiene.
        var sandbox = new CrushDispatchSandbox { ReappearMovedAfterDispatch = true };
        sandbox.Files.Add("/work/.crushrc");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.NotNull(result.Stderr);
        Assert.Contains("crushrc restore incomplete", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("/work/.crushrc", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunResumedAsync_QuarantinesAndLiftsTerminalDiagnostic()
    {
        // The resume path re-dispatches the CLI in the same working
        // directory, so it carries the same quarantine and diagnostic
        // obligations as the initial dispatch.
        var sandbox = new CrushDispatchSandbox
        {
            CrushExitCode = 1,
            CrushStderr = "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).",
            CrushStdout = string.Empty,
        };
        sandbox.Files.Add("/work/.crushrc");
        var runner = RunnerWithDefault();
        var resume = new AgentResumeContext("checkpoint-ref");

        var result = await runner.RunResumedAsync(sandbox, "/work", "x", Cred(), resume);

        Assert.False(result.Success);
        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Key limit exceeded", result.TerminalDiagnostic, StringComparison.Ordinal);
        Assert.Contains(sandbox.Execs, e => e.Argv.Count == 4 && e.Argv[0] == "mv");
        Assert.Contains("/work/.crushrc", sandbox.Files);
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_ExposesGateKey()
    {
        // The gate key rides the sandbox process environment — the CLI
        // reads it directly, with no config file involved.
        IAgentCredentialEnvironmentPolicy policy = new CrushAgentRunner();

        Assert.Contains("OPENROUTER_API_KEY", policy.DirectCredentialEnvironmentVariables);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = RunnerWithDefault("openrouter/z-ai/glm-5.2:free");

        Assert.Equal("openrouter/z-ai/glm-5.2:free", runner.DefaultModelId);
    }

    [Fact]
    public async Task RunTextOnlyAsync_DispatchesInSandbox()
    {
        var sandbox = new CrushDispatchSandbox { CrushStdout = "text answer", CrushStderr = string.Empty };
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred(), sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        Assert.Equal("text answer", result.Output);
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");
    }

    [Fact]
    public async Task RunTextOnlyAsync_WithoutSandbox_FailsNamingSandboxRequirement()
    {
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred());

        Assert.False(result.Success);
        Assert.Contains("sandbox", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTextOnlyAsync_MissingCredential_FailsFastWithoutDispatch()
    {
        var sandbox = new CrushDispatchSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Crush, new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("summarise", empty, sandbox: sandbox, workingDirectory: "/work");

        Assert.False(result.Success);
        Assert.Contains("CODEYBOX_CRUSH_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "crush");
    }
}

/// <summary>
/// Scripted <see cref="ISandbox"/> for the Crush runner tests: answers
/// <c>test -f/-e</c> probes from a fake guest file set, simulates
/// <c>mv</c> (recording the move, optionally failing), and serves a canned
/// result for the <c>crush</c> dispatch. All other execs succeed empty.
/// </summary>
internal sealed class CrushDispatchSandbox : ISandbox
{
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

    public int CrushExitCode { get; set; }

    public string CrushStdout { get; set; } = "stdout";

    public string CrushStderr { get; set; } = "stderr";

    public bool FailMoves { get; set; }

    public bool ReappearMovedAfterDispatch { get; set; }

    public string Id => "fake-crush";

    public List<SandboxExec> Execs { get; } = [];

    private readonly List<(string Original, string Backup)> _moved = [];

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        Execs.Add(exec);
        var argv = exec.Argv;
        if (argv.Count == 3 && argv[0] == "test" && (argv[1] == "-f" || argv[1] == "-e"))
        {
            return Task.FromResult(Files.Contains(argv[2])
                ? new SandboxExecResult(0, string.Empty, string.Empty)
                : new SandboxExecResult(1, string.Empty, string.Empty));
        }

        if (argv.Count == 4 && argv[0] == "mv" && argv[1] == "--")
        {
            if (FailMoves || !Files.Contains(argv[2]))
            {
                return Task.FromResult(new SandboxExecResult(1, string.Empty, $"mv: cannot stat '{argv[2]}'"));
            }

            Files.Remove(argv[2]);
            Files.Add(argv[3]);
            _moved.Add((argv[2], argv[3]));
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        if (argv.Count > 0 && argv[0] == "crush")
        {
            if (ReappearMovedAfterDispatch)
            {
                foreach (var (original, _) in _moved)
                    Files.Add(original);
            }

            exec.StdoutChunkCallback?.Invoke(CrushStdout);
            return Task.FromResult(new SandboxExecResult(CrushExitCode, CrushStdout, CrushStderr));
        }

        return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
