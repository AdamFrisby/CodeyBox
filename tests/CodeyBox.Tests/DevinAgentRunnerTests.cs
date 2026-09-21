using CodeyBox.Agents.Devin;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinAgentRunner"/>. Argv pins encode the transport
/// decision verified against devin 3000.11.1: <c>devin -p --permission-mode
/// dangerous --respect-workspace-trust false --prompt-file /dev/stdin</c> with
/// the prompt on stdin (bare <c>-p</c> ignores piped stdin; MAX_ARG_STRLEN
/// caps positional prompts), <c>--model</c> only when a model is configured,
/// and the credentials.toml contents materialised in-guest from
/// <c>CODEYBOX_DEVIN_AUTH_TOML</c>.
/// </summary>
public sealed class DevinAgentRunnerTests
{
    private const string ConfiguredModel = "claude-sonnet-4";

    private static DevinAgentRunner RunnerWithDefault(string model = ConfiguredModel) =>
        new(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["devin"] = model }));

    private static AgentCredential Cred(string toml = "api_key = \"sk-test\"") =>
        new(AgentKind.Devin,
            new Dictionary<string, string> { [DevinAgentRunner.AuthTomlEnvironmentVariable] = toml },
            new Dictionary<string, string>());

    private static SandboxExec DevinExec(RecordingSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "devin");

    [Fact]
    public void Kind_IsDevin()
    {
        Assert.Equal(AgentKind.Devin, new DevinAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Devin_RoundTrips()
    {
        Assert.Equal(AgentKind.Devin, new AgentKind("devin"));
    }

    [Fact]
    public async Task RunAsync_Argv_IsPrintModeWithDangerousPermissions()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "do the thing", Cred());

        var argv = DevinExec(sandbox).Argv.ToList();
        Assert.Equal(["devin", "-p", "--permission-mode", "dangerous",
            "--respect-workspace-trust", "false",
            "--model", ConfiguredModel, "--prompt-file", "/dev/stdin"], argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_NotArgv()
    {
        // Linux MAX_ARG_STRLEN is 128 KiB per argv element; rework prompts can
        // exceed it. Verified: bare `-p` ignores a piped prompt —
        // --prompt-file /dev/stdin is required.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        const string prompt = "implement the widget with extra care";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        var devin = DevinExec(sandbox);
        Assert.Equal(prompt, devin.Stdin);
        Assert.DoesNotContain(devin.Argv, a => a.Contains("widget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExplicitModelId_WinsOverDefault()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "x", Cred(), modelId: "claude-opus-4.6");

        var argv = DevinExec(sandbox).Argv.ToList();
        Assert.Contains("claude-opus-4.6", argv);
        Assert.DoesNotContain(ConfiguredModel, argv);
    }

    [Fact]
    public async Task RunAsync_NoModelAnywhere_OmitsModelFlag()
    {
        // Unlike crush, devin has an account-side server default, so an
        // unset model is a legal dispatch — the flag is simply omitted.
        var sandbox = new RecordingSandbox();
        var runner = new DevinAgentRunner();

        await runner.RunAsync(sandbox, "/work", "x", Cred());

        var argv = DevinExec(sandbox).Argv.ToList();
        Assert.DoesNotContain("--model", argv);
    }

    [Fact]
    public async Task RunAsync_MaterialisesCredentialsBeforeDispatch()
    {
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        const string toml = "api_key = \"sk-x\"\napi_server_url = \"https://u.example\"";

        await runner.RunAsync(sandbox, "/work", "x", Cred(toml));

        var materialise = Assert.Single(sandbox.Execs, e =>
            CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                e, ".local/share/devin/credentials.toml"));
        Assert.True(sandbox.Execs.IndexOf(materialise) < sandbox.Execs.IndexOf(DevinExec(sandbox)));
    }

    [Fact]
    public async Task RunAsync_MissingCredential_DispatchesWithImageProvisionedAuth()
    {
        // MaterialiseFromSandboxEnvironmentWhenCredentialMissing: an absent
        // CODEYBOX_DEVIN_AUTH_TOML is NOT a failure — the sandbox image may
        // provision its own credentials.toml. The runner skips the
        // materialisation script and dispatches anyway.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Devin,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "x", empty);

        Assert.True(result.Success);
        Assert.DoesNotContain(sandbox.Execs, e =>
            CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                e, ".local/share/devin/credentials.toml"));
        DevinExec(sandbox);
    }

    [Fact]
    public async Task RunAsync_MaterialisationFailure_FailsClosedWithoutDispatch()
    {
        var sandbox = new RecordingSandbox { MaterialiseExitCode = 1 };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == "devin");
    }

    [Fact]
    public async Task RunAsync_ErrorLine_LiftsTerminalDiagnostic()
    {
        // Recorded real shape (devin 3000.11.1, logged-out credentials): exit
        // nonzero with "Error: Not logged in" on stderr.
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 1,
            DevinStderr = "Error: Not logged in. Run `devin auth login` to authenticate.",
            DevinStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Not logged in", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ErrorOnStdout_LiftsTerminalDiagnostic()
    {
        // Some CLI paths print the error to stdout; diagnoser scans both.
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 1,
            DevinStderr = string.Empty,
            DevinStdout = "Error: Quota exhausted. Wait for your quota to reset.",
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.NotNull(result.TerminalDiagnostic);
        Assert.Contains("Quota exhausted", result.TerminalDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HealthyRun_LeavesTerminalDiagnosticNull()
    {
        var sandbox = new RecordingSandbox { DevinStdout = "done\n", DevinStderr = string.Empty };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.True(result.Success);
        Assert.Null(result.TerminalDiagnostic);
    }

    [Fact]
    public async Task RunAsync_MissingCliInGuest_SurfacesAsInfrastructureFailure()
    {
        var sandbox = new RecordingSandbox
        {
            DevinExitCode = 127,
            DevinStderr = "bash: line 1: devin: command not found",
            DevinStdout = string.Empty,
        };
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "x", Cred());

        Assert.False(result.Success);
        Assert.Contains("command not found", result.Stderr, StringComparison.OrdinalIgnoreCase);
        var classification = ((IAgentRunner)runner).ClassifyFailure(result);
        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
    }

    [Fact]
    public void DefaultModelId_ReadsAgentDefaultsSnapshot()
    {
        var runner = RunnerWithDefault("claude-opus-4.6");

        Assert.Equal("claude-opus-4.6", runner.DefaultModelId);
    }

    [Fact]
    public async Task RunTextOnlyAsync_DispatchesInSandbox_WithoutDangerousMode()
    {
        // Text-only omits --permission-mode so the CLI default (auto:
        // read-only tools only) applies — the conservative shape for
        // answering questions on untrusted resolver input.
        var sandbox = new RecordingSandbox();
        var runner = RunnerWithDefault();

        var result = await runner.RunTextOnlyAsync("summarise", Cred(), sandbox: sandbox, workingDirectory: "/work");

        Assert.True(result.Success);
        var argv = DevinExec(sandbox).Argv.ToList();
        Assert.Equal(["devin", "-p", "--respect-workspace-trust", "false",
            "--model", ConfiguredModel, "--prompt-file", "/dev/stdin"], argv);
        Assert.DoesNotContain("--permission-mode", argv);
        Assert.DoesNotContain("dangerous", argv);
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
    public async Task RunTextOnlyAsync_MissingCredential_ReportsUnavailable()
    {
        var runner = RunnerWithDefault();
        var empty = new AgentCredential(AgentKind.Devin,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var reason = ((ITextOnlyAgentRunner)runner).GetTextOnlyUnavailabilityReason(empty);

        Assert.NotNull(reason);
        Assert.Contains(DevinAgentRunner.AuthTomlEnvironmentVariable, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectCredentialEnvironmentVariables_IsEmpty()
    {
        // The auth TOML is consumed by the materialisation script, not read
        // by the devin binary directly — nothing is exposed as a direct
        // credential env var (the CLI has no DEVIN_API_KEY equivalent).
        IAgentCredentialEnvironmentPolicy policy = new DevinAgentRunner();

        Assert.Empty(policy.DirectCredentialEnvironmentVariables);
    }

    /// <summary>
    /// Scripted <see cref="ISandbox"/> for the Devin runner tests: the
    /// credential-materialisation bash script and the <c>devin</c> dispatch
    /// get independently canned results so tests can fail either stage.
    /// </summary>
    private sealed class RecordingSandbox : ISandbox
    {
        public int MaterialiseExitCode { get; set; }
        public int DevinExitCode { get; set; }
        public string DevinStdout { get; set; } = "stdout";
        public string DevinStderr { get; set; } = "stderr";

        public string Id => "fake-devin";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (CredentialMaterialisationTestHelper.IsStdinMaterialisation(
                    exec, ".local/share/devin/credentials.toml"))
            {
                return Task.FromResult(new SandboxExecResult(MaterialiseExitCode, "", "auth stderr"));
            }
            if (exec.Argv.Count > 0 && exec.Argv[0] == "devin")
                return Task.FromResult(new SandboxExecResult(DevinExitCode, DevinStdout, DevinStderr));
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
