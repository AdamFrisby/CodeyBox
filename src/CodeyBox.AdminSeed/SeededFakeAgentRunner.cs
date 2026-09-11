using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.AdminSeed;

/// <summary>
/// Deterministic fake <see cref="IAgentRunner"/> for the seeded CodeyBox
/// admin instance. No LLM, no VM, no network: the success path stages one
/// deterministic markdown file through the sandbox's own exec channel
/// (argv <c>tee</c> + stdin — never a shell string), and scripted branches
/// reproduce quota-park, auth, failure, and empty-diff outcomes so the admin
/// web can observe the full lifecycle without real agents.
///
/// <para>Behavior is a pure function of (seed, prompt): prompts carrying an
/// explicit <c>[seeded-fake:*]</c> marker take that branch, everything else
/// hashes into a success/failure bucket. The effective options snapshot is
/// read per-invocation from <see cref="IOptionsMonitor{T}"/> so seed and
/// behavior tuning hot-reload.</para>
/// </summary>
public sealed class SeededFakeAgentRunner : IAgentRunner
{
    public static AgentKind FakeKind { get; } = new("seeded-fake");

    private readonly IOptionsMonitor<SeededFakeAgentOptions> _options;
    private readonly TimeProvider _timeProvider;

    public SeededFakeAgentRunner(
        IOptionsMonitor<SeededFakeAgentOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AgentKind Kind => FakeKind;

    public async Task<AgentResult> RunAsync(
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
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(prompt);

        var options = _options.CurrentValue;
        var behavior = SeededFakeBehaviorSelector.Select(prompt, options);

        return behavior switch
        {
            SeededFakeBehavior.Success => await RunSuccessAsync(
                sandbox, workingDirectory, prompt, options, stdoutChunkCallback, ct).ConfigureAwait(false),
            SeededFakeBehavior.QuotaPark => QuotaParkResult(options),
            SeededFakeBehavior.AuthFailure => AuthFailureResult(),
            SeededFakeBehavior.EmptyDiff => EmptyDiffResult(options),
            _ => NormalFailureResult(),
        };
    }

    private async Task<AgentResult> RunSuccessAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        SeededFakeAgentOptions options,
        Action<string>? stdoutChunkCallback,
        CancellationToken ct)
    {
        var fileName = ValidateArtifactFileName(options.ArtifactFileName);
        var content = BuildArtifactContent(prompt, options.Seed);

        Emit(stdoutChunkCallback, $"[seeded-fake] seed={options.Seed} behavior=success\n");
        Emit(stdoutChunkCallback, $"[seeded-fake] staging {fileName}\n");

        SandboxExecResult staged;
        try
        {
            staged = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["tee", "--", fileName],
                WorkingDirectory = workingDirectory,
                Stdin = content,
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
            }, ct).ConfigureAwait(false);
        }
        catch (SandboxExecutionUnavailableException ex)
        {
            return new AgentResult(
                Success: false,
                Summary: "seeded-fake: sandbox execution was unavailable",
                Stdout: null,
                Stderr: ex.Message)
            {
                ExecutionUnavailable = true,
            };
        }

        if (!staged.Success)
        {
            return new AgentResult(
                Success: false,
                Summary: $"seeded-fake: failed to stage {fileName} (exit {staged.ExitCode})",
                Stdout: staged.Stdout,
                Stderr: staged.Stderr);
        }

        Emit(stdoutChunkCallback, "[seeded-fake] done\n");
        return new AgentResult(
            Success: true,
            Summary: $"seeded-fake success (seed {options.Seed}): staged {fileName}",
            Stdout: $"[seeded-fake] staged {fileName}\n",
            Stderr: null);
    }

    private AgentResult QuotaParkResult(SeededFakeAgentOptions options)
    {
        var resetSeconds = Math.Max(1, options.QuotaResetSeconds);
        var resetAt = _timeProvider.GetUtcNow().AddSeconds(resetSeconds);
        return new AgentResult(
            Success: true,
            Summary: "seeded-fake: quota exhausted, no changes produced",
            Stdout: "[seeded-fake] RESOURCE_EXHAUSTED: no changes produced\n",
            Stderr: null)
        {
            TerminalDiagnostic =
                $"seeded-fake: RESOURCE_EXHAUSTED (429) quota exhausted; " +
                $"reset in {resetSeconds}s at {resetAt:O}; no file changes produced",
        };
    }

    private static AgentResult AuthFailureResult() => new(
        Success: false,
        Summary: "seeded-fake: authentication required",
        Stdout: null,
        Stderr: "seeded-fake: ERROR 401 authentication required: run `codeybox login` to refresh credentials\n");

    private static AgentResult NormalFailureResult() => new(
        Success: false,
        Summary: "seeded-fake: simulated work failure",
        Stdout: "[seeded-fake] working...\n",
        Stderr: "seeded-fake: simulated work failure: unable to complete the requested change\n");

    private static AgentResult EmptyDiffResult(SeededFakeAgentOptions options) => new(
        Success: true,
        Summary: $"seeded-fake: success with no changes (seed {options.Seed})",
        Stdout: "[seeded-fake] nothing to change\n",
        Stderr: null);

    internal static string BuildArtifactContent(string prompt, int seed)
    {
        var digest = SeededFakeBehaviorSelector.Fnv1a32($"{seed}:{prompt}");
        return $"# Seeded fake-agent change (seed {seed}, prompt-hash {digest:x8})\n";
    }

    internal static string ValidateArtifactFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Artifact file name is required.", nameof(fileName));
        var trimmed = fileName.Trim();
        if (trimmed.Contains('\0')
            || trimmed.Contains('/') || trimmed.Contains('\\')
            || trimmed is "." or ".."
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.StartsWith('-')
            || Path.IsPathRooted(trimmed))
        {
            throw new ArgumentException(
                $"Artifact file name '{trimmed}' must be a bare file name.",
                nameof(fileName));
        }
        return trimmed;
    }

    private static void Emit(Action<string>? callback, string chunk)
    {
        try
        {
            callback?.Invoke(chunk);
        }
        catch
        {
            // Observer-side failures must not fail the run; the callback is
            // best-effort progress reporting, not part of the result contract.
        }
    }
}
