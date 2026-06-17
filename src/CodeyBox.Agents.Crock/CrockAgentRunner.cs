using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Drives the <c>crock</c> CLI from <c>github.com/AdamFrisby/CrockCode</c>: a
/// headless C# coding agent that runs tasks ASYNCHRONOUSLY against Anthropic's
/// Message Batches API. Submit-then-poll, NOT a synchronous attached stream.
///
/// <para>Wire-protocol summary the runner implements:</para>
/// <list type="number">
///   <item><c>crock submit -p &lt;prompt&gt; [-C &lt;working-dir&gt;]</c> — prints a task-id then detaches.</item>
///   <item><c>crock status &lt;task-id&gt;</c> — polled on a bounded exponential backoff until terminal.</item>
/// </list>
///
/// <para>Per-task latency is minutes-to-hours (vs. seconds-to-minutes for the
/// other registered agents), so this runner is registered as light-duty
/// overflow only. It is NOT a member of any shipped <c>AgentClass</c>;
/// operators opt it in via host config once the dependent follow-up
/// (cost/usage accounting, watchdog accommodation, credential/tunnel
/// provisioning) lands.</para>
///
/// <para>DESIGN NOTE — DEPENDENT FOLLOW-UP MUST SOLVE BEFORE ENABLING:</para>
/// <list type="bullet">
///   <item>
///     <c>WorkerProgressWatchdog</c>'s default ProgressTimeout (~60 minutes)
///     is shorter than crock's worst-case per-task latency. The poll loop
///     here emits a per-poll heartbeat through the agent stream (via the
///     <see cref="IAgentRunner.RunAsync"/> <c>stdoutChunkCallback</c>) so
///     liveness is observable; but the watchdog must either (a) recognise
///     those heartbeats as live progress or (b) be reconfigured per-agent
///     with a crock-appropriate ProgressTimeout. Without that change a long
///     batch looks stalled and gets killed mid-flight.
///   </item>
///   <item>
///     crock needs BOTH an Anthropic API key (so the batch model can run)
///     AND a public tunnel (cloudflared/ngrok) so the batch worker can call
///     back to local MCP tools in the sandbox. Provisioning a per-sandbox
///     ephemeral tunnel — and authorising the callback path without
///     widening the sandbox's network policy beyond what its
///     internet-only profile already allows — is the harder half of the
///     follow-up. <see cref="PrepareSandboxAsync"/> here only materialises
///     the credential file; the tunnel side is intentionally NOT wired.
///   </item>
/// </list>
/// </summary>
public sealed class CrockAgentRunner : CliAgentRunnerBase
{
    /// <summary>Default crock CLI binary name inside the sandbox.</summary>
    public const string DefaultBinary = "crock";

    /// <summary>
    /// Credential env var read by the in-sandbox materialisation script. The
    /// host-side credential provider is expected to fetch this from
    /// <c>CODEYBOX_CROCK_CONFIG_JSON</c> (or an equivalent host-namespaced
    /// source) and ship it inside the credential bundle. The follow-up will
    /// add an explicit <c>AgentCredentialMapping</c> alongside the
    /// other agents' mappings in <c>Program.cs</c>.
    /// </summary>
    public const string ConfigEnvVar = "CROCK_CONFIG_JSON";

    /// <summary>
    /// Bash that materialises crock's <c>~/.crockcode/config.json</c> from
    /// <see cref="ConfigEnvVar"/>. Mirrors the umask-077 / chmod-600 pattern
    /// used by <see cref="OpencodeAgentRunner"/> and the Codex runner so the
    /// credential never sits at world-readable modes inside the VM. Exposed
    /// as a constant so an in-VM smoke probe can run it verbatim and stay in
    /// lock-step with the runner.
    /// </summary>
    public const string ConfigMaterialiseScript =
        "set -eu\n" +
        "dest=\"$HOME/.crockcode/config.json\"\n" +
        "umask 077\n" +
        "mkdir -p \"$(dirname \"$dest\")\"\n" +
        "if [ -n \"${CROCK_CONFIG_JSON:-}\" ]; then\n" +
        "  printf '%s' \"$CROCK_CONFIG_JSON\" > \"$dest\"\n" +
        "  chmod 600 \"$dest\"\n" +
        "fi\n";

    /// <summary>Path to the crock binary inside the sandbox.</summary>
    public string Binary { get; init; } = DefaultBinary;

    /// <summary>
    /// Initial delay before the first <c>crock status</c> poll, and the floor
    /// for the exponential backoff. Crock's batch latency is minutes-to-hours
    /// so polling sub-second would just burn sandbox exec cycles.
    /// </summary>
    public TimeSpan InitialPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling for the exponential backoff between polls.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Hard ceiling on the number of consecutive Unknown status observations
    /// the runner tolerates before failing the work item. A mute or
    /// reshaped CLI would otherwise keep the poll loop alive forever; the
    /// cancellation token is the primary stop signal, this is the backstop.
    /// </summary>
    public int MaxUnknownStreak { get; init; } = 20;

    public override AgentKind Kind => AgentKind.Crock;

    /// <summary>
    /// Materialises <c>~/.crockcode/config.json</c> from <see cref="ConfigEnvVar"/>.
    /// When the credential bundle does not carry the env var the runner
    /// short-circuits with an unavailability <see cref="AgentResult"/> rather
    /// than letting the CLI crash on its own missing-config error — keeps the
    /// failure shape consistent with the other subscription runners.
    /// </summary>
    protected override async Task<AgentResult?> PrepareSandboxAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        AgentResumeContext? resume,
        CancellationToken ct = default)
    {
        if (credential is null
            || !credential.EnvironmentVariables.TryGetValue(ConfigEnvVar, out var json)
            || string.IsNullOrWhiteSpace(json))
        {
            return new AgentResult(
                Success: false,
                Summary: $"{ConfigEnvVar} is required to run crock",
                Stdout: null,
                Stderr: null);
        }

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", ConfigMaterialiseScript],
        }, ct);
        if (!write.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"failed to materialise crock config: exit {write.ExitCode}",
                Stdout: write.Stdout,
                Stderr: write.Stderr);
        }
        return null;
    }

    /// <summary>
    /// Builds the <c>crock submit</c> argv. The prompt is delivered via stdin
    /// (matching the Opencode / Codex / Gemini pattern) so rework prompts
    /// that exceed Linux's 128 KiB MAX_ARG_STRLEN ceiling keep working.
    ///
    /// <para>The exact stdin-marker convention for <c>crock submit</c> has
    /// not been verified against a live binary; the follow-up will swap this
    /// for whatever shape <c>crock submit --help</c> documents.</para>
    /// </summary>
    protected override AgentInvocation BuildInvocation(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        bool captureStructuredStream = false)
    {
        // `-p -` is the common Unix convention for "read prompt from stdin".
        // The follow-up should verify against the real CLI; if crock uses a
        // different shape (e.g. `--prompt-stdin`) the change is one line.
        var argv = new List<string> { Binary, "submit", "-p", "-" };

        _ = modelId;
        _ = reasoningMode;
        _ = captureStructuredStream;
        return new AgentInvocation(argv, Stdin: prompt);
    }

    /// <summary>
    /// Drives the submit→poll lifecycle so the orchestrator still sees a
    /// single <see cref="RunAsync"/> call. The base class's retry loop is
    /// intentionally bypassed for the crock CLI shape — the submit step
    /// detaches and the poll loop reads CLI state from a separate exec.
    /// </summary>
    public override async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        var preparation = await PrepareSandboxAsync(sandbox, workingDirectory, credential, resume: null, ct);
        if (preparation is not null)
            return preparation;

        // STEP 1 — Submit the task. Stdin carries the prompt; argv is short.
        var submitInvocation = BuildInvocation(
            prompt, credential, modelId, reasoningMode, captureStructuredStream);
        var submit = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = submitInvocation.Argv,
            WorkingDirectory = workingDirectory,
            Stdin = submitInvocation.Stdin,
            ExtraEnvironment = submitInvocation.ExtraEnvironment,
        }, ct);

        if (!submit.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"crock submit failed: exit {submit.ExitCode}",
                Stdout: submit.Stdout,
                Stderr: submit.Stderr);
        }

        var taskId = CrockStatusParser.TryExtractTaskId(submit.Stdout);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new AgentResult(
                Success: false,
                Summary: "crock submit emitted no recognisable task-id",
                Stdout: submit.Stdout,
                Stderr: submit.Stderr);
        }

        EmitProgress(stdoutChunkCallback, $"crock submitted task {taskId}");

        // STEP 2 — Poll status on bounded exponential backoff until terminal.
        return await PollUntilTerminalAsync(
            sandbox, workingDirectory, taskId, stdoutChunkCallback, ct);
    }

    private async Task<AgentResult> PollUntilTerminalAsync(
        ISandbox sandbox,
        string workingDirectory,
        string taskId,
        Action<string>? stdoutChunkCallback,
        CancellationToken ct)
    {
        var delay = InitialPollInterval;
        var unknownStreak = 0;
        var pollCount = 0;
        SandboxExecResult? lastStatus = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return new AgentResult(
                    Success: false,
                    Summary: $"crock poll cancelled while waiting on task {taskId}",
                    Stdout: lastStatus?.Stdout,
                    Stderr: lastStatus?.Stderr);
            }

            pollCount++;
            lastStatus = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = [Binary, "status", taskId],
                WorkingDirectory = workingDirectory,
            }, ct);

            // A non-zero exit on `crock status` may be transient (daemon
            // hiccup) or terminal (task gone); rather than guess, we feed
            // the stderr blob through the same classifier so a terminal
            // FAILED token still resolves the loop, and otherwise treat
            // it as an Unknown observation counted toward the streak cap.
            var status = CrockStatusParser.Classify(lastStatus.Stdout, lastStatus.Stderr);

            switch (status.StateKind)
            {
                case CrockTaskStateKind.Succeeded:
                    EmitProgress(stdoutChunkCallback,
                        $"crock task {taskId} {status.Summary} after {pollCount} polls");
                    return new AgentResult(
                        Success: true,
                        Summary: $"ok ({status.Summary})",
                        Stdout: lastStatus.Stdout,
                        Stderr: lastStatus.Stderr);

                case CrockTaskStateKind.Failed:
                    EmitProgress(stdoutChunkCallback,
                        $"crock task {taskId} {status.Summary} after {pollCount} polls");
                    return new AgentResult(
                        Success: false,
                        Summary: $"crock task failed ({status.Summary})",
                        Stdout: lastStatus.Stdout,
                        Stderr: lastStatus.Stderr);

                case CrockTaskStateKind.InProgress:
                    unknownStreak = 0;
                    EmitProgress(stdoutChunkCallback,
                        $"crock task {taskId} {status.Summary} (poll {pollCount})");
                    break;

                case CrockTaskStateKind.Unknown:
                default:
                    unknownStreak++;
                    EmitProgress(stdoutChunkCallback,
                        $"crock task {taskId} unknown state (poll {pollCount}, streak {unknownStreak})");
                    if (unknownStreak >= MaxUnknownStreak)
                    {
                        return new AgentResult(
                            Success: false,
                            Summary: $"crock poll gave up after {unknownStreak} consecutive unknown states",
                            Stdout: lastStatus.Stdout,
                            Stderr: lastStatus.Stderr);
                    }
                    break;
            }

            // Exponential backoff with the configured ceiling. Bounded ticks
            // come from doubling InitialPollInterval; capping at MaxPollInterval
            // keeps a long batch from drifting to multi-minute poll gaps.
            var next = TimeSpan.FromTicks(delay.Ticks * 2);
            delay = next > MaxPollInterval ? MaxPollInterval : next;
        }
    }

    private static void EmitProgress(Action<string>? sink, string message)
    {
        if (sink is null) return;
        try
        {
            var envelope = JsonSerializer.Serialize(new
            {
                type = "codeybox.crock.progress",
                message,
            }) + "\n";
            sink(envelope);
        }
        catch
        {
            // Progress emission is observability-only; never block the poll loop.
        }
    }
}
