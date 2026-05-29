using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Integration.AgentSuspendResilience;

/// <summary>
/// Outcome of one agent × suspend-duration smoke scenario.
/// </summary>
public enum AgentSuspendSmokeOutcome
{
    /// <summary>Agent exited 0 without needing orchestrator recovery.</summary>
    Completed,

    /// <summary>
    /// Agent exited non-zero but stderr matches transient network patterns that
    /// CodeyBox classifies as recoverable via in-iteration retry / stranded recovery.
    /// </summary>
    RecoverableFailure,

    /// <summary>Non-recoverable failure or hang past the harness deadline.</summary>
    Failed,
}

/// <summary>
/// Runs one suspend-during-LLM-call scenario against a real multipass VM.
/// </summary>
internal static class AgentSuspendSmokeHarness
{
    internal const string SmokePrompt =
        "Reply with exactly OK. Do not use tools. Do not edit any files.";

    private static readonly TimeSpan PreSuspendDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AgentRunTimeout = TimeSpan.FromMinutes(15);

    private static readonly string[] AgentInstallRuncmd =
    [
        "apt-get update",
        "DEBIAN_FRONTEND=noninteractive apt-get install -y curl ca-certificates nodejs npm",
        "npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli",
        "curl -fsSL https://cursor.com/install | bash",
        "curl -fsSL https://opencode.ai/install | bash",
    ];

    public static async Task<AgentSuspendSmokeOutcome> RunScenarioAsync(
        AgentKind agent,
        int suspendDurationSeconds,
        CancellationToken ct = default)
    {
        var env = AgentSuspendSmokeEnvironment.TryBuildSandboxEnvironment(agent)
            ?? throw new InvalidOperationException($"no credential for {agent.Value}");

        var home = Environment.GetEnvironmentVariable("HOME");
        var snapCommon = home is null ? null : Path.Combine(home, "snap", "multipass", "common");
        var stagingRoot = (snapCommon is not null && Directory.Exists(snapCommon))
            ? Path.Combine(snapCommon, "codeybox-suspend-smoke")
            : Path.Combine(Path.GetTempPath(), "codeybox-suspend-smoke");
        Directory.CreateDirectory(stagingRoot);

        var workspace = Path.Combine(stagingRoot, $"wi-{agent.Value}-{suspendDurationSeconds}-{Guid.NewGuid():N}"[..48]);
        Directory.CreateDirectory(workspace);

        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions
            {
                StagingDirectory = stagingRoot,
                ExtraRuncmd = AgentInstallRuncmd,
                UseBaselineImages = true,
            },
            NullLogger<MultipassSandboxProvider>.Instance);

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = workspace, ReadOnly = false }],
            Environment = new Dictionary<string, string>(env),
            WorkingDirectory = "/work",
        };

        await using var sandbox = await provider.CreateAsync(spec, ct);
        if (sandbox is not ISuspendableSandbox suspendable)
            throw new InvalidOperationException("multipass sandbox must implement ISuspendableSandbox");

        var runner = AgentSuspendSmokeEnvironment.CreateRunner(agent);
        var modelId = AgentSuspendSmokeEnvironment.LowCostModelId(agent);
        var logPath = Path.Combine(workspace, "agent-smoke.log");
        var vmLogPath = SandboxConventions.AgentLogDir + "/suspend-smoke.log";

        using var logScope = AgentInvocationLogContext.BeginScope(vmLogPath);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(AgentRunTimeout);

        var agentTask = runner.RunAsync(
            sandbox,
            "/work",
            SmokePrompt,
            credential: new AgentCredential(agent, new Dictionary<string, string>(env), new Dictionary<string, string>()),
            modelId: modelId,
            ct: runCts.Token);

        await Task.Delay(PreSuspendDelay, ct);
        await suspendable.SuspendAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(suspendDurationSeconds), ct);
        await provider.ResumeSandboxAsync(sandbox.Id, ct);

        AgentResult result;
        try
        {
            result = await agentTask;
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return AgentSuspendSmokeOutcome.Failed;
        }

        try
        {
            await File.WriteAllTextAsync(
                logPath,
                $"agent={agent.Value}\nseconds={suspendDurationSeconds}\nsuccess={result.Success}\nsummary={result.Summary}\nstderr={result.Stderr}\n",
                ct);
        }
        catch
        {
            // Best-effort artifact for CI; do not fail the scenario.
        }

        return Classify(result);
    }

    internal static AgentSuspendSmokeOutcome Classify(AgentResult result)
    {
        if (result.Success)
            return AgentSuspendSmokeOutcome.Completed;

        var classification = AgentFailureClassifier.Classify(result.Stderr, result.Stdout);
        if (classification.Kind == AgentFailureKind.TransientNetwork)
            return AgentSuspendSmokeOutcome.RecoverableFailure;

        return AgentSuspendSmokeOutcome.Failed;
    }
}
