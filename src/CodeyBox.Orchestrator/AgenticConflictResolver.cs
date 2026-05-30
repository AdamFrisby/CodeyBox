using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hot-reloadable knobs for the agentic conflict resolver. Operator config
/// flips these by swapping the snapshot held by the orchestrator; the resolver
/// reads <see cref="AgenticConflictResolverOptionsSnapshot.Current"/> on every
/// invocation so a mid-run reload is observed by the next conflict iteration.
/// </summary>
public sealed record AgenticConflictResolverOptions
{
    /// <summary>
    /// Maximum number of attempts at a single conflict state. If the first
    /// agent invocation leaves markers or unmerged paths, the resolver
    /// re-invokes the agent with a refreshed prompt up to this many times
    /// before giving up. Default 3 — one fresh attempt plus two retries.
    /// </summary>
    public int MaxIterations { get; init; } = 3;

    /// <summary>
    /// When true, after marker/unmerged-path verification passes the resolver
    /// runs <see cref="BuildVerifyArgv"/> inside the sandbox and treats a
    /// non-zero exit as a resolution failure. Defaults to false — the merge
    /// pipeline already has dedicated build / audit stages downstream; this
    /// flag exists for operators who want a fast-fail signal inline.
    /// </summary>
    public bool BuildVerify { get; init; }

    /// <summary>
    /// Command to run for <see cref="BuildVerify"/>. Empty list disables build
    /// verification regardless of <see cref="BuildVerify"/>. The command runs
    /// inside the sandbox via <see cref="ISandbox.ExecAsync"/>.
    /// </summary>
    public IReadOnlyList<string> BuildVerifyArgv { get; init; } = [];
}

/// <summary>
/// Mutable hot-reload holder for <see cref="AgenticConflictResolverOptions"/>.
/// Construction-time DI binds a single instance; operators swap the underlying
/// options via <see cref="Apply"/>. Mirrors the
/// <c>AgentConcurrencySnapshot</c> pattern so the orchestrator's hot-reload
/// coordinator can observe option churn without recreating the resolver.
/// </summary>
public sealed class AgenticConflictResolverOptionsSnapshot
{
    private AgenticConflictResolverOptions _current;

    public AgenticConflictResolverOptionsSnapshot()
        : this(new AgenticConflictResolverOptions()) { }

    public AgenticConflictResolverOptionsSnapshot(AgenticConflictResolverOptions initial)
    {
        _current = initial ?? new AgenticConflictResolverOptions();
    }

    public AgenticConflictResolverOptions Current => Volatile.Read(ref _current);

    public void Apply(AgenticConflictResolverOptions next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));
        Volatile.Write(ref _current, next);
    }
}

/// <summary>
/// Describes the conflict situation the resolver is being called for. Used in
/// the prompt and in audit-log messages so operators can tell rebase-step
/// failures from merge-step failures.
/// </summary>
public sealed record AgenticConflictResolverContext(
    string BaseBranch,
    string WorkBranch,
    AgenticConflictResolverOperation Operation);

public enum AgenticConflictResolverOperation
{
    Rebase,
    Merge,
}

/// <summary>
/// Outcome of a single <see cref="AgenticConflictResolver.ResolveAsync"/>
/// call. <see cref="Success"/> is true only when conflict-marker, unmerged-path,
/// and (when enabled) build-verify checks all pass after one of the candidate
/// agents finished. <see cref="ChosenRunner"/> / <see cref="ChosenCredential"/>
/// carry the agent that actually succeeded so callers can attribute the work
/// for audit-log and usage accounting.
/// </summary>
public sealed record AgenticConflictResolverResult(
    bool Success,
    string Summary,
    IAgentRunner? ChosenRunner,
    AgentCredential? ChosenCredential,
    IReadOnlyList<string> ConflictFiles,
    int IterationsUsed);

/// <summary>
/// A single agent candidate the resolver may invoke. The orchestrator builds
/// these from the work item's primary runner plus its class-fallback chain,
/// honouring <see cref="ProjectAudit.AuditAgent"/> and class membership. The
/// resolver itself is agnostic to how the order was chosen — it just walks
/// the list until one candidate produces a clean tree (or all fail).
/// </summary>
public sealed record AgenticConflictResolverCandidate(
    IAgentRunner Runner,
    AgentCredential? Credential,
    string? ModelId = null,
    string? ReasoningMode = null);

/// <summary>
/// Resolves an in-sandbox mid-rebase/merge conflict by invoking the
/// project's configured coding agent CLI <em>inside</em> the same sandbox via
/// <see cref="IAgentRunner.RunAsync"/> — NOT via a text-only/raw-HTTP path.
/// The agent sees the conflicted working tree, can read arbitrary files for
/// context, edit multiple files, and stage them. The resolver verifies the
/// result deterministically (no conflict markers, no unmerged paths, optional
/// build) and iterates per agent attempt up to a configurable cap.
///
/// <para>
/// This supersedes the old text-only resolver path (
/// <c>PipelineRunner.RunConstrainedConflictResolverAsync</c> +
/// <c>InvokeTextOnlyAsync</c> + <c>ClaudeAgentRunner.RunTextOnlyAsync</c>),
/// which had three structural defects: a 128 KB per-file byte cap, no
/// multi-file iterative resolution, and a raw <c>api.anthropic.com</c> call
/// that risked subscription-account termination. None of those apply here:
/// the agent runs in-VM through its normal CLI shape (ToS-compliant) and reads
/// files directly without orchestrator-side base64 transport.
/// </para>
/// </summary>
public sealed class AgenticConflictResolver
{
    private readonly AgenticConflictResolverOptionsSnapshot _options;
    private readonly ILogger _log;

    public AgenticConflictResolver(
        AgenticConflictResolverOptionsSnapshot? options = null,
        ILogger<AgenticConflictResolver>? log = null)
    {
        _options = options ?? new AgenticConflictResolverOptionsSnapshot();
        _log = log ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger<AgenticConflictResolver>.Instance;
    }

    /// <summary>
    /// Resolves the current conflict state inside <paramref name="sandbox"/>.
    /// Iterates through <paramref name="candidates"/> in order; each candidate
    /// is given up to <see cref="AgenticConflictResolverOptions.MaxIterations"/>
    /// attempts. Returns success on the first attempt whose post-run
    /// verification passes, failure with a concrete reason otherwise.
    /// </summary>
    public async Task<AgenticConflictResolverResult> ResolveAsync(
        ISandbox sandbox,
        string workingDirectory,
        WorkItemId workItemId,
        AgenticConflictResolverContext context,
        IReadOnlyList<AgenticConflictResolverCandidate> candidates,
        CancellationToken ct = default)
    {
        if (sandbox is null) throw new ArgumentNullException(nameof(sandbox));
        if (string.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentException("workingDirectory must be non-empty", nameof(workingDirectory));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (candidates is null || candidates.Count == 0)
            throw new ArgumentException("at least one agent candidate is required", nameof(candidates));

        var conflictFiles = await ListUnmergedPathsAsync(sandbox, workingDirectory, ct);
        if (conflictFiles.Count == 0)
        {
            return new AgenticConflictResolverResult(
                Success: true,
                Summary: "no conflicts to resolve",
                ChosenRunner: null,
                ChosenCredential: null,
                ConflictFiles: [],
                IterationsUsed: 0);
        }

        foreach (var file in conflictFiles)
            ValidateRelativeWorkPath(file);

        var options = _options.Current;
        var maxIterations = Math.Max(1, options.MaxIterations);
        var attemptTrail = new List<string>();
        int totalIterations = 0;
        AgentResult? lastAgentResult = null;
        string? lastVerificationError = null;

        foreach (var candidate in candidates)
        {
            var runner = candidate.Runner;
            for (var attempt = 1; attempt <= maxIterations; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                totalIterations++;

                var prompt = BuildAgenticConflictResolverPrompt(
                    context,
                    conflictFiles,
                    attempt,
                    maxIterations,
                    lastVerificationError);

                AgentResult agentResult;
                try
                {
                    agentResult = await runner.RunAsync(
                        sandbox,
                        workingDirectory,
                        prompt,
                        candidate.Credential,
                        candidate.ModelId,
                        candidate.ReasoningMode,
                        ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Agentic conflict resolver: agent '{Agent}' threw on attempt {Attempt}/{Max} for {WorkItemId}",
                        runner.Kind.Value, attempt, maxIterations, workItemId);
                    attemptTrail.Add($"{runner.Kind.Value}#{attempt}(threw: {ex.Message})");
                    break;
                }

                lastAgentResult = agentResult;
                if (!agentResult.Success)
                {
                    _log.LogInformation(
                        "Agentic conflict resolver: agent '{Agent}' reported failure on attempt {Attempt}/{Max} for {WorkItemId}: {Summary}",
                        runner.Kind.Value, attempt, maxIterations, workItemId, agentResult.Summary);
                    attemptTrail.Add($"{runner.Kind.Value}#{attempt}(agent failed: {Truncate(agentResult.Summary, 120)})");
                    break;
                }

                var verification = await VerifyResolutionAsync(
                    sandbox, workingDirectory, conflictFiles, options, ct);
                if (verification.Success)
                {
                    return new AgenticConflictResolverResult(
                        Success: true,
                        Summary: $"resolved by '{runner.Kind.Value}' on attempt {attempt}/{maxIterations}",
                        ChosenRunner: runner,
                        ChosenCredential: candidate.Credential,
                        ConflictFiles: conflictFiles,
                        IterationsUsed: totalIterations);
                }

                lastVerificationError = verification.Reason;
                attemptTrail.Add($"{runner.Kind.Value}#{attempt}({Truncate(verification.Reason, 200)})");
                _log.LogInformation(
                    "Agentic conflict resolver: verification failed for agent '{Agent}' attempt {Attempt}/{Max} on {WorkItemId}: {Reason}",
                    runner.Kind.Value, attempt, maxIterations, workItemId, verification.Reason);
            }
        }

        var summary = lastVerificationError
            ?? lastAgentResult?.Summary
            ?? "no candidate produced a clean resolution";
        var trail = attemptTrail.Count == 0 ? "(none)" : string.Join("; ", attemptTrail);
        return new AgenticConflictResolverResult(
            Success: false,
            Summary: $"agentic conflict resolution failed: {summary} (attempts: {trail})",
            ChosenRunner: null,
            ChosenCredential: null,
            ConflictFiles: conflictFiles,
            IterationsUsed: totalIterations);
    }

    internal static async Task<IReadOnlyList<string>> ListUnmergedPathsAsync(
        ISandbox sandbox, string workingDirectory, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--name-only", "--diff-filter=U"],
        }, ct);
        if (!result.Success)
            throw new MergeConflictResolutionFailedException(
                $"failed to inspect unmerged paths: {result.Stderr.Trim()}");

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reject path patterns that escape the working directory (absolute paths,
    /// backslashes, traversal segments). The agent runs inside the sandbox so
    /// the VM boundary is the real defence; this is belt-and-braces against a
    /// malformed git output line that, if interpolated into a downstream argv,
    /// could reach outside <paramref name="workingDirectory"/>.
    /// </summary>
    internal static void ValidateRelativeWorkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/', StringSplitOptions.None).Any(static part => part is "" or "." or ".."))
        {
            throw new MergeConflictResolutionFailedException($"unsafe conflict file path '{path}'");
        }
    }

    internal sealed record VerificationOutcome(bool Success, string Reason);

    internal async Task<VerificationOutcome> VerifyResolutionAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> originalConflictFiles,
        AgenticConflictResolverOptions options,
        CancellationToken ct)
    {
        var unmerged = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--name-only", "--diff-filter=U"],
        }, ct);
        if (!unmerged.Success)
            return new VerificationOutcome(false, $"git diff failed: {unmerged.Stderr.Trim()}");
        if (!string.IsNullOrWhiteSpace(unmerged.Stdout))
            return new VerificationOutcome(
                false,
                "unmerged paths remain after agent: " + unmerged.Stdout.Trim().Replace('\n', ' '));

        if (originalConflictFiles.Count > 0)
        {
            // Mirror PipelineRunner.FinalizeRebaseConflictResolutionAsync's grep
            // pattern so the agentic and legacy paths agree on what counts as a
            // marker line.
            var argv = new List<string>
            {
                "git", "-C", workingDirectory, "grep", "-l", "-E",
                "^(<<<<<<<|=======|>>>>>>>)", "--",
            };
            argv.AddRange(originalConflictFiles);
            var markers = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
            if (markers.ExitCode == 0)
                return new VerificationOutcome(
                    false,
                    "conflict markers remain in: " + markers.Stdout.Trim().Replace('\n', ' '));
            if (markers.ExitCode != 1)
                return new VerificationOutcome(false, $"failed to scan for conflict markers: {markers.Stderr.Trim()}");
        }

        if (options.BuildVerify && options.BuildVerifyArgv.Count > 0)
        {
            var build = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = options.BuildVerifyArgv,
                WorkingDirectory = workingDirectory,
            }, ct);
            if (!build.Success)
            {
                var detail = string.IsNullOrWhiteSpace(build.Stderr) ? build.Stdout : build.Stderr;
                return new VerificationOutcome(
                    false,
                    $"build-verify failed (exit {build.ExitCode}): {Truncate(detail, 400)}");
            }
        }

        return new VerificationOutcome(true, "ok");
    }

    internal static string BuildAgenticConflictResolverPrompt(
        AgenticConflictResolverContext context,
        IReadOnlyList<string> conflictFiles,
        int attempt,
        int maxAttempts,
        string? priorVerificationError)
    {
        var op = context.Operation == AgenticConflictResolverOperation.Rebase ? "rebase" : "merge";
        var sb = new StringBuilder();
        sb.Append("# Conflict-resolution mode (in-sandbox agentic resolver)\n\n");
        sb.Append($"You are inside a sandbox at `{SandboxConventions.WorkDir}` which contains\n");
        sb.Append($"a git repository in a conflicted state mid-{op} of `{context.WorkBranch}`\n");
        sb.Append($"into `{context.BaseBranch}`. Your job is to resolve every conflict so the\n");
        sb.Append("working tree is clean and ready for the orchestrator to continue the operation.\n\n");

        sb.Append("Conflicted files (relative to the working tree):\n");
        foreach (var file in conflictFiles)
            sb.Append("  - `").Append(file).Append("`\n");

        sb.Append("\nSuccess criteria (verified deterministically after you exit):\n");
        sb.Append("  - `git diff --name-only --diff-filter=U` is empty (no unmerged paths)\n");
        sb.Append("  - None of the listed files contain `<<<<<<< `, `=======` (alone on a line),\n");
        sb.Append("    or `>>>>>>> ` conflict markers\n");
        sb.Append("  - Every resolved file is `git add`'d so the index reflects your resolution\n\n");

        sb.Append("How to resolve each file:\n");
        sb.Append("  1. Read the full file. Locate every `<<<<<<<` / `=======` / `>>>>>>>` block.\n");
        sb.Append("  2. Read both sides carefully. If a diff3 base section is present\n");
        sb.Append("     (`|||||||` marker), use it as a tie-breaker.\n");
        sb.Append("  3. Preserve the intent of BOTH sides — do not take one side blindly.\n");
        sb.Append("  4. Write the merged content back to the same file. Remove every conflict\n");
        sb.Append("     marker line. The file must contain neither `<<<<<<< `, `======= `, nor\n");
        sb.Append("     `>>>>>>> ` once you are done.\n");
        sb.Append("  5. `git add <file>` once it is marker-free.\n\n");

        sb.Append("Constraints (the orchestrator rejects resolutions that violate these):\n");
        sb.Append($"  - DO NOT run `git {op} --continue` or `git {op} --abort` — the orchestrator does.\n");
        sb.Append("  - DO NOT push, pull, fetch, or change remotes.\n");
        sb.Append("  - DO NOT amend, reset, or rewrite existing history.\n");
        sb.Append("  - DO NOT add, delete, or rename files outside the conflict list.\n");
        sb.Append("  - DO NOT resolve by stripping code: every functional change from EITHER\n");
        sb.Append("    side must survive in the merged form, unless one side genuinely replaces\n");
        sb.Append("    the other's intent.\n");
        sb.Append("  - DO NOT commit. Just `git add` the resolved files and exit.\n\n");

        if (attempt > 1 && !string.IsNullOrWhiteSpace(priorVerificationError))
        {
            sb.Append("This is a retry. Your previous attempt did not satisfy the success criteria:\n");
            sb.Append("  ").Append(priorVerificationError).Append('\n');
            sb.Append("Fix the remaining issues and re-stage the resolved files. ");
            sb.Append($"({attempt}/{maxAttempts})\n\n");
        }

        sb.Append("There are no commit-trailer requirements for this step: the orchestrator\n");
        sb.Append("creates the rebase/merge commit itself after verifying your work.\n");

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxChars ? value : value[..maxChars] + "…";
    }
}
