using System.Diagnostics;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Cohesive build-gate service that owns the entire required-build workflow:
/// applicability probing, phase-specific enforcement (work / audit / merge),
/// timeout wrapping around the verifier, audit-finding construction, and
/// adaptation of the verifier result into the canonical audit-report shape
/// for persistence.
///
/// <para>Existence rationale: the <see cref="IRequiredBuildVerifier"/>
/// abstraction only covers execution (probe + run). Without this gate, the
/// pipeline runner ended up owning every other build-gate concern — the
/// god-object footprint the architecture review flagged. Keeping the policy
/// here lets <see cref="PipelineRunner"/> stay at phase-orchestration level
/// and call a single entry point per call site.</para>
/// </summary>
internal sealed class RequiredBuildGate
{
    /// <summary>
    /// Callback used to persist the canonical <see cref="AuditReport"/> for a
    /// completed build verification. Wired by the orchestrator so the gate
    /// reuses the same redaction / finding-id wiring as every other auditor
    /// instead of duplicating it here.
    /// </summary>
    public delegate Task PersistAuditReport(
        AuditContext ctx,
        IAuditor auditor,
        AuditResult result,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        CancellationToken ct);

    private readonly IRequiredBuildVerifier _verifier;
    private readonly TimeSpan _verificationTimeout;
    private readonly PersistAuditReport? _persistReport;

    public RequiredBuildGate(
        IRequiredBuildVerifier verifier,
        TimeSpan verificationTimeout,
        PersistAuditReport? persistReport)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _verificationTimeout = verificationTimeout;
        _persistReport = persistReport;
    }

    /// <summary>
    /// Returns true when the branch carries .NET build markers and the gate
    /// must run for the audit phase. Propagates probe-side infrastructure
    /// failures as <see cref="RequiredBuildVerificationUnavailableException"/>
    /// so callers route them to the same Unavailable handling that the
    /// verification stage uses.
    /// </summary>
    public async Task<bool> AppliesAsync(
        WorkItemId workItemId,
        ProjectId projectId,
        string repositoryId,
        string? baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        var probe = await _verifier.ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = workItemId,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
        }, ct);

        return probe.Status switch
        {
            RequiredBuildProbeStatus.Applies => true,
            RequiredBuildProbeStatus.NotApplicable => false,
            RequiredBuildProbeStatus.Unavailable => throw new RequiredBuildVerificationUnavailableException(
                $"could not verify required build: {probe.Reason ?? "build marker inspection failed"}"),
            _ => throw new RequiredBuildVerificationUnavailableException(
                $"could not verify required build: unknown probe status {probe.Status}"),
        };
    }

    /// <summary>
    /// Enforces the build gate for the work / rework phase.
    ///
    /// <para><b>Work (initial) phase:</b> on failure throws
    /// <see cref="RequiredBuildFailedException"/> so the outer pipeline catch
    /// transitions the item to Failed with <c>failureKind=build</c>. The
    /// initial work phase has no audit/rework loop behind it that could
    /// converge on a fix, so terminal failure is the only sensible outcome.
    /// </para>
    ///
    /// <para><b>Rework phase:</b> on failure does NOT throw. The next audit
    /// iteration's <see cref="RunForAuditAsync"/> call detects the same
    /// non-compile state and surfaces it as a blocking finding, giving the
    /// audit/rework loop another iteration to recover within the existing
    /// <see cref="ProjectAudit.MaxIterations"/> budget. Only when that budget
    /// is exhausted does the audit ceiling (park-if-progress / fail-if-not)
    /// take over. This mirrors the unified "unsuccessful-rework =&gt;
    /// loop-back-not-terminal" policy documented alongside the audit-loop
    /// docs and avoids the asymmetry where a build failure DISCOVERED in
    /// audit self-corrects but the SAME failure PRODUCED by a rework
    /// terminal-failed.
    /// </para>
    /// </summary>
    public async Task EnforceForWorkPhaseAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        string agentPhase,
        CancellationToken ct)
    {
        var result = await VerifyAsync(
            item, project, repoId, baseBranch, workBranch, phase: agentPhase, iteration: null, ct);
        if (result.Status != RequiredBuildVerificationStatus.Failed)
            return;

        if (agentPhase.Equals("rework", StringComparison.OrdinalIgnoreCase))
            return;

        throw new RequiredBuildFailedException(
            $"work left the branch non-compiling: {BuildFailureSummary(result)}");
    }

    /// <summary>
    /// Runs the gate as an audit-phase auditor. Returns an
    /// <see cref="AuditFinding"/> when the build failed (the caller folds it
    /// into the iteration's blocking findings) or null on pass/skip.
    /// Unavailable status propagates as
    /// <see cref="RequiredBuildVerificationUnavailableException"/> so the
    /// audit phase distinguishes infra degradation from real breakage.
    /// </summary>
    public async Task<AuditFinding?> RunForAuditAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        int iteration,
        CancellationToken ct)
    {
        var result = await VerifyAsync(
            item, project, repoId, baseBranch, workBranch, phase: "audit", iteration: iteration, ct);
        if (result.Status != RequiredBuildVerificationStatus.Failed)
            return null;

        return new AuditFinding(
            AuditorName: RequiredBuildGateIdentity.AuditorName,
            Severity: AuditSeverity.Error,
            Title: $"required build failed: {RequiredBuildGateIdentity.DisplayCommand}",
            Description: BuildFailureSummary(result));
    }

    /// <summary>
    /// Verifies the existing work-branch state before a Queued (from=work)
    /// pickup resets it to the base tip. If the branch is non-compiling, the
    /// gate fails loud with a <see cref="RequiredBuildFailedException"/> so
    /// the work item transitions to Failed (failureKind=build) instead of
    /// silently dropping the broken commits and proceeding from pristine
    /// base — which would neither fix the intrinsic compile error nor
    /// surface it. Skips entirely when the gate does not apply (no .NET
    /// markers), when the work branch has not been pushed to origin yet
    /// (fresh work item: nothing pre-existing to verify), or when the
    /// branch's tip already equals the base tip (nothing distinct on the
    /// branch).
    /// </summary>
    public async Task EnforceBeforePickupResetAsync(
        WorkItem item,
        Project project,
        IGitHost gitHost,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gitHost);

        // Fresh work item: no pre-existing branch on origin → no broken
        // state to inspect. The reset that follows is effectively a
        // create-from-base, no different from a brand-new WI.
        if (!await gitHost.BranchExistsAsync(repoId, workBranch, ct))
            return;

        var applies = await AppliesAsync(item.Id, project.Id, repoId, baseBranch, workBranch, ct);
        if (!applies) return;

        var result = await VerifyAsync(
            item, project, repoId, baseBranch, workBranch, phase: "pickup", iteration: null, ct);
        if (result.Status == RequiredBuildVerificationStatus.Failed)
        {
            throw new RequiredBuildFailedException(
                $"retry-from-work received a non-compiling branch: {BuildFailureSummary(result)}");
        }
    }

    /// <summary>
    /// Enforces the build gate when an item resumes at AuditPassed (skips the
    /// normal audit loop). Throws <see cref="AuditFailedException"/> on
    /// failure so the outer catch rolls the item back to AuditFailed.
    /// </summary>
    public async Task EnforceOnAuditPassedResumeAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        var applies = await AppliesAsync(item.Id, project.Id, repoId, baseBranch, workBranch, ct);
        if (!applies) return;

        var result = await VerifyAsync(
            item, project, repoId, baseBranch, workBranch, phase: "audit", iteration: null, ct);
        if (result.Status == RequiredBuildVerificationStatus.Failed)
        {
            throw new AuditFailedException(
                $"required build failed on AuditPassed resume: {BuildFailureSummary(result)}");
        }
    }

    private async Task<RequiredBuildVerificationResult> VerifyAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        string workBranch,
        string phase,
        int? iteration,
        CancellationToken ct)
    {
        var auditTarget = SandboxTargetResolver.ResolveAudit(
            project.NetworkProfiles.AuditTool,
            AuditCapabilities.None);
        var baselineRef = SandboxTargetResolver.BaselineRefForTarget(
            project, auditTarget, item.BaselineImageRef);
        var request = new RequiredBuildVerificationRequest
        {
            WorkItemId = item.Id,
            ProjectId = project.Id,
            RepositoryId = repoId,
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
            Phase = phase,
            Iteration = iteration,
            SandboxPolicy = new RequiredBuildSandboxPolicy
            {
                NetworkProfile = auditTarget.NetworkProfile,
                BaselineImageRef = baselineRef,
            },
        };

        // Branch-controlled MSBuild targets can sleep or loop forever.
        // Bound every verification call (work, audit, AuditPassed-resume)
        // so the gate cannot hold the pipeline worker indefinitely.
        using var timeoutCts = new CancellationTokenSource(_verificationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        RequiredBuildVerificationResult result;
        try
        {
            result = await _verifier.VerifyAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new RequiredBuildVerificationUnavailableException(
                $"could not verify required build: build exceeded the required-build verification timeout of {_verificationTimeout.TotalMinutes:0.##} minutes");
        }
        finally
        {
            sw.Stop();
        }

        if (iteration is int iter)
        {
            await PersistReportAsync(item.Id, iter, startedAt, sw.Elapsed, result, ct);
        }

        if (result.Status == RequiredBuildVerificationStatus.Unavailable)
        {
            throw new RequiredBuildVerificationUnavailableException(
                result.Reason ?? "could not verify required build: verifier unavailable");
        }

        return result;
    }

    private async Task PersistReportAsync(
        WorkItemId workItemId,
        int iteration,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        RequiredBuildVerificationResult result,
        CancellationToken ct)
    {
        if (_persistReport is null) return;
        // Skipped: the gate did not apply on this branch, so there is no
        // auditor invocation to record. Unavailable with no captured output
        // also has nothing useful to persist (the build script never ran:
        // marker inspection, isolated clone, or sandbox setup failed before
        // the build emitted anything). Failed/Passed always persist; an
        // Unavailable that DOES carry build output (dotnet-not-found,
        // no-target) is treated like a script-ran-but-couldn't-finish
        // outcome and persisted with its output so operators can inspect it.
        if (result.Status == RequiredBuildVerificationStatus.Skipped) return;
        if (result.Status == RequiredBuildVerificationStatus.Unavailable
            && string.IsNullOrEmpty(result.Output))
        {
            return;
        }

        var auditResult = ToAuditResult(result);
        var ctxForReport = new AuditContext(
            WorkItemId: workItemId,
            WorkBranch: string.Empty,
            BaseBranch: string.Empty,
            Iteration: iteration,
            OriginalPrompt: string.Empty);
        await _persistReport(
            ctxForReport,
            RequiredBuildAuditorAdapter.Instance,
            auditResult,
            startedAt,
            elapsed,
            ct);
    }

    private static AuditResult ToAuditResult(RequiredBuildVerificationResult result)
    {
        // Failed: real build breakage on the branch — Error finding, fails the report.
        // Unavailable: the auditor could not execute the build (dotnet missing,
        // no target after marker detection, etc.). The work item separately
        // fails with failureKind=infrastructure, but the persisted report must
        // not look clean — otherwise operators reading audit-report views
        // cannot tell "auditor could not run" from a real pass. Emit a clear
        // Error finding with a distinct title so the two cases are
        // distinguishable in the report.
        var findings = result.Status switch
        {
            RequiredBuildVerificationStatus.Failed => new AuditFinding[]
            {
                new(
                    AuditorName: RequiredBuildGateIdentity.AuditorName,
                    Severity: AuditSeverity.Error,
                    Title: $"required build failed: {RequiredBuildGateIdentity.DisplayCommand}",
                    Description: $"Required build exited with code {result.ExitCode}."),
            },
            RequiredBuildVerificationStatus.Unavailable => new AuditFinding[]
            {
                new(
                    AuditorName: RequiredBuildGateIdentity.AuditorName,
                    Severity: AuditSeverity.Error,
                    Title: $"required build unavailable: {RequiredBuildGateIdentity.DisplayCommand}",
                    Description: result.Reason
                        ?? $"Required build auditor could not execute (exit {result.ExitCode})."),
            },
            _ => Array.Empty<AuditFinding>(),
        };
        var passed = result.Status == RequiredBuildVerificationStatus.Passed;
        return new AuditResult(
            Passed: passed,
            Findings: findings,
            RawOutput: string.IsNullOrEmpty(result.Output) ? null : result.Output);
    }

    private static string BuildFailureSummary(RequiredBuildVerificationResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Output)
            ? "(no build output captured)"
            : result.Output.Trim();
        return $"required build failed (exit {result.ExitCode}): {detail}";
    }

    /// <summary>
    /// Persistence-only auditor identity for the build gate. The verifier
    /// owns execution; this adapter exists so persisted audit reports carry
    /// the same auditor name/kind every other auditor uses and flow through
    /// the canonical redaction / finding-id wiring.
    /// </summary>
    private sealed class RequiredBuildAuditorAdapter : IAuditor
    {
        public static readonly RequiredBuildAuditorAdapter Instance = new();
        private RequiredBuildAuditorAdapter() { }

        public string Name => RequiredBuildGateIdentity.AuditorName;
        public string Kind => "shell";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => throw new NotSupportedException(
                "RequiredBuildAuditorAdapter is a persistence-only adapter; the build verifier owns execution.");
    }
}
