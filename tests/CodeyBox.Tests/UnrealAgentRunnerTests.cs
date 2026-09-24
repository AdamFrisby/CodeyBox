using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Agents.Unreal;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="UnrealAgentRunner"/>:
/// - Transport contract (Trap 1): NO -p, NO /dev/stdin, prompt on stdin as JSON
/// - Directories (Trap 2): -log-directory and -session-directory outside workspace
/// - Workspace .env neutralisation (Trap 3): quarantine before dispatch, restore in finally
/// - Prompt > 128 KiB delivered intact on stdin
/// - ReasoningMode mapped to thinking_level; model mapped to model
/// - Codex subscription credentials rejected
/// - Exit code classification: 0 ok, 1 error, 130 infrastructure (SIGINT)
/// </summary>
public sealed class UnrealAgentRunnerTests
{
    private const string FreeModel = "openrouter/nvidia/nemotron-3.5-lightning:free";

    private static UnrealAgentRunner Runner(AgentDefaultsSnapshot? defaults = null) =>
        new(defaults: defaults);

    private static UnrealAgentRunner RunnerWithDefault(string model = FreeModel) =>
        Runner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["unreal"] = model }));

    private static AgentCredential Cred(string key = "test-key") =>
        new(AgentKind.Unreal,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = key },
            new Dictionary<string, string>());

    private static SandboxExec UnrealExec(UnrealDispatchSandbox sandbox) =>
        Assert.Single(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == UnrealAgentRunner.DefaultBinary);

    [Fact]
    public void Kind_IsUnreal()
    {
        Assert.Equal(AgentKind.Unreal, new UnrealAgentRunner().Kind);
    }

    [Fact]
    public void AgentKind_Unreal_RoundTrips()
    {
        Assert.Equal(AgentKind.Unreal, new AgentKind("unreal"));
    }

    [Fact]
    public void ScratchpadHomeDirectories_IncludesSessions_ExcludesLogs()
    {
        // Unreal sessions live in ~/.unreal-agent/sessions which belongs in scratchpad.
        // Logs are unbounded and captured in DB, so they must NOT be in scratchpad.
        var runner = new UnrealAgentRunner();
        // Invoke protected ScratchpadHomeDirectories via reflection or subclass
        var prop = typeof(UnrealAgentRunner).GetProperty("ScratchpadHomeDirectories",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        var dirs = (IReadOnlyList<string>)prop!.GetValue(runner)!;

        Assert.Contains(".unreal-agent/sessions", dirs);
        Assert.DoesNotContain(".unreal-agent/logs", dirs);
    }

    [Fact]
    public async Task RunAsync_Argv_SetsWorkspaceAndDirectoriesStrictlyOutsideWorkspace()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();
        const string workspace = "/sandbox/work/my-repo";

        await runner.RunAsync(sandbox, workspace, "do the work", Cred());

        var exec = UnrealExec(sandbox);
        var argv = exec.Argv.ToList();

        // Must invoke binary with -workspace, -log-directory, -session-directory
        Assert.Equal(UnrealAgentRunner.DefaultBinary, argv[0]);
        Assert.Contains("-workspace", argv);
        var wsIndex = argv.IndexOf("-workspace");
        Assert.Equal(workspace, argv[wsIndex + 1]);

        Assert.Contains("-log-directory", argv);
        var logIndex = argv.IndexOf("-log-directory");
        var logDir = argv[logIndex + 1];
        Assert.False(logDir.StartsWith(workspace, StringComparison.Ordinal), "log directory must be strictly outside workspace");

        Assert.Contains("-session-directory", argv);
        var sessionIndex = argv.IndexOf("-session-directory");
        var sessionDir = argv[sessionIndex + 1];
        Assert.False(sessionDir.StartsWith(workspace, StringComparison.Ordinal), "session directory must be strictly outside workspace");

        // Trap 1: NO -p, NO --prompt, NO /dev/stdin
        Assert.DoesNotContain("-p", argv);
        Assert.DoesNotContain("--prompt", argv);
        Assert.DoesNotContain("/dev/stdin", argv);
    }

    [Fact]
    public async Task RunAsync_Prompt_TravelsViaStdin_AsJsonRequest()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();
        const string prompt = "fix the bug in program.cs";

        await runner.RunAsync(sandbox, "/work", prompt, Cred());

        var exec = UnrealExec(sandbox);
        Assert.NotNull(exec.Stdin);

        // Parse stdin as JSON
        using var doc = JsonDocument.Parse(exec.Stdin);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("prompt", out var promptProp));
        Assert.Equal(prompt, promptProp.GetString());

        // Argv must NOT contain prompt text
        Assert.DoesNotContain(exec.Argv, a => a.Contains("fix the bug", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_LargePrompt_Over128KiB_DeliveredIntactOnStdin()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();
        // Generate prompt larger than Linux MAX_ARG_STRLEN (128 KiB)
        var largePrompt = new string('A', 140 * 1024);

        await runner.RunAsync(sandbox, "/work", largePrompt, Cred());

        var exec = UnrealExec(sandbox);
        Assert.NotNull(exec.Stdin);

        using var doc = JsonDocument.Parse(exec.Stdin);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("prompt", out var promptProp));
        Assert.Equal(largePrompt, promptProp.GetString());
    }

    [Fact]
    public async Task RunAsync_ModelAndThinkingLevel_MappedInJson()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = Runner();

        await runner.RunAsync(sandbox, "/work", "test", Cred(), modelId: "openrouter/anthropic/claude-3.5-sonnet", reasoningMode: "HIGH");

        var exec = UnrealExec(sandbox);
        using var doc = JsonDocument.Parse(exec.Stdin!);
        var root = doc.RootElement;

        Assert.Equal("openrouter/anthropic/claude-3.5-sonnet", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("thinking_level").GetString());
    }

    [Fact]
    public async Task RunAsync_InvalidThinkingLevel_IsOmitted()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();

        await runner.RunAsync(sandbox, "/work", "test", Cred(), reasoningMode: "ultra-extreme-invalid");

        var exec = UnrealExec(sandbox);
        using var doc = JsonDocument.Parse(exec.Stdin!);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("thinking_level", out _));
    }

    [Fact]
    public async Task RunAsync_ProviderInferredFromApiKey_WhenNotSet()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();

        var openrouterCred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "sk-or-test" },
            new Dictionary<string, string>());
        await runner.RunAsync(sandbox, "/work", "test", openrouterCred);

        var exec = UnrealExec(sandbox);
        Assert.NotNull(exec.ExtraEnvironment);
        Assert.Equal("openrouter", exec.ExtraEnvironment["UNREAL_HARNESS_LLM_PROVIDER"]);
    }

    [Fact]
    public async Task RunAsync_CodexSubscriptionCredential_ProhibitedAndRejectedFast()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();

        var codexEnvCred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string>
            {
                ["OPENROUTER_API_KEY"] = "key",
                ["OPENAI_CODEX_ACCESS_TOKEN"] = "token123"
            },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "test", codexEnvCred);

        Assert.False(result.Success);
        Assert.Equal(UnrealAgentRunner.ProhibitedCodexCredentialMarker, result.Summary);
        Assert.Empty(sandbox.Execs);
    }

    [Fact]
    public async Task RunAsync_CodexAuthJsonFile_ProhibitedAndRejectedFast()
    {
        var sandbox = new UnrealDispatchSandbox();
        var runner = RunnerWithDefault();

        var codexFileCred = new AgentCredential(AgentKind.Unreal,
            new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "key" },
            new Dictionary<string, string> { ["~/.config/codex/auth.json"] = "{}" });

        var result = await runner.RunAsync(sandbox, "/work", "test", codexFileCred);

        Assert.False(result.Success);
        Assert.Equal(UnrealAgentRunner.ProhibitedCodexCredentialMarker, result.Summary);
        Assert.Empty(sandbox.Execs);
    }

    [Fact]
    public async Task RunAsync_WorkspaceDotEnv_QuarantinedDuringDispatchAndRestored()
    {
        var sandbox = new UnrealDispatchSandbox();
        sandbox.Files.Add("/work/.env");
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "test", Cred());

        Assert.True(result.Success);
        // .env was moved aside during execution and restored afterwards
        Assert.Contains("/work/.env", sandbox.Files);

        // Verify mv operations: first to quarantine backup, then back to original
        var moves = sandbox.Execs.Where(e => e.Argv.Count >= 2 && e.Argv[0] == "mv").ToList();
        Assert.Equal(2, moves.Count);

        // Move 1: /work/.env -> /work/.env.codeybox-quarantined-...
        Assert.Equal("/work/.env", moves[0].Argv[2]);
        Assert.StartsWith("/work/.env.codeybox-quarantined-", moves[0].Argv[3]);

        // Move 2: restore backup -> /work/.env
        Assert.Equal(moves[0].Argv[3], moves[1].Argv[2]);
        Assert.Equal("/work/.env", moves[1].Argv[3]);
    }

    [Fact]
    public async Task RunAsync_WorkspaceDotEnv_QuarantineFailure_FailsClosed()
    {
        var sandbox = new UnrealDispatchSandbox();
        sandbox.Files.Add("/work/.env");
        sandbox.FailMoves = true;
        var runner = RunnerWithDefault();

        var result = await runner.RunAsync(sandbox, "/work", "test", Cred());

        Assert.False(result.Success);
        Assert.Contains("refusing Unreal dispatch", result.Summary);
        // Unreal was NEVER dispatched
        Assert.DoesNotContain(sandbox.Execs, e => e.Argv.Count > 0 && e.Argv[0] == UnrealAgentRunner.DefaultBinary);
    }

    [Fact]
    public void ClassifyFailure_Exit0_IsNormal()
    {
        var runner = Runner();
        var result = new AgentResult(Success: true, Summary: "ok", Stdout: null, Stderr: null);
        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Normal, classification.Kind);
    }

    [Fact]
    public void ClassifyFailure_Exit1_IsNormalWorkFailure()
    {
        var runner = Runner();
        var result = new AgentResult(Success: false, Summary: "agent exited 1", Stdout: null, Stderr: "some error");
        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Normal, classification.Kind);
    }

    [Fact]
    public void ClassifyFailure_Exit130_IsInfrastructureInterrupted()
    {
        var runner = Runner();
        var result = new AgentResult(Success: false, Summary: "agent exited 130", Stdout: null, Stderr: null);
        var classification = runner.ClassifyFailure(result);

        Assert.Equal(AgentFailureKind.Infrastructure, classification.Kind);
        Assert.Contains("130", classification.Reason);
    }
}

/// <summary>
/// Scripted <see cref="ISandbox"/> for Unreal runner tests:
/// manages test -f/-e, mv, and unreal-agent-runner dispatch.
/// </summary>
internal sealed class UnrealDispatchSandbox : ISandbox
{
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    public int UnrealExitCode { get; set; }
    public string UnrealStdout { get; set; } = "stdout";
    public string UnrealStderr { get; set; } = "stderr";
    public bool FailMoves { get; set; }
    public string Id => "fake-unreal";
    public List<SandboxExec> Execs { get; } = [];

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
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

        if (argv.Count > 0 && argv[0] == UnrealAgentRunner.DefaultBinary)
        {
            exec.StdoutChunkCallback?.Invoke(UnrealStdout);
            return Task.FromResult(new SandboxExecResult(UnrealExitCode, UnrealStdout, UnrealStderr));
        }

        return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
