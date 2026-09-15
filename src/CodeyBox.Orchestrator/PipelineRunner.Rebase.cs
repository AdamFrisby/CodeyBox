using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// PipelineRunner.Rebase.cs — Pickup/incremental rebase plus conflict-resolution helpers: sandbox rebase core, scope-fenced resolver wiring, and branch-ownership guards.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Pre-merge canonical-base refresh. Asks the upstream remote to update the
    /// host bare repo's local base ref to the upstream tip, so the merge phase
    /// agent composes the merge against canonical main rather than the snapshot
    /// taken when the bare repo was first created. Best-effort: a fetch that
    /// throws or returns null is logged at Warning but does not park the work
    /// item — the residual race window (between this refresh and the upstream
    /// merge call) is already covered by the 405 auto-merge race recovery and
    /// the non-fast-forward push reconcile.
    /// </summary>
    private async Task TryRefreshCanonicalBaseBeforeMergeAsync(
        WorkItem item,
        Project project,
        IUpstreamRemote upstream,
        string repoId,
        string baseBranch,
        CancellationToken ct)
    {
        Validation.ValidateBranchName(baseBranch, nameof(baseBranch));

        string? preRefreshSha = null;
        try
        {
            preRefreshSha = await _gitHost.ResolveCommitAsync(repoId, baseBranch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Couldn't read pre-refresh tip — log and proceed. The refresh
            // itself may still succeed and overwrite whatever the local ref
            // points at; we just lose the "did it actually move?" diagnostic.
            _log.LogDebug(ex,
                "Pre-merge base refresh: could not read local '{Branch}' tip before fetch",
                baseBranch);
        }

        string? postRefreshSha;
        try
        {
            postRefreshSha = await upstream.FetchBaseBranchAsync(repoId, baseBranch, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Pre-merge base refresh: fetch of canonical '{Branch}' for work item {WorkItemId} failed via '{UpstreamKind}'; merge phase will proceed against the bare repo's existing base (possibly stale)",
                baseBranch, item.Id, project.Upstream.Kind);
            return;
        }

        if (postRefreshSha is null)
        {
            _log.LogWarning(
                "Pre-merge base refresh: upstream '{UpstreamKind}' did not advertise '{Branch}' for work item {WorkItemId}; merge phase will proceed against the bare repo's existing base (possibly stale)",
                project.Upstream.Kind, baseBranch, item.Id);
            return;
        }

        if (preRefreshSha is not null && !string.Equals(preRefreshSha, postRefreshSha, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "Pre-merge base refresh: '{Branch}' advanced {Old} → {New} for work item {WorkItemId}",
                baseBranch, preRefreshSha, postRefreshSha, item.Id);
        }
    }

    /// <summary>
    /// Context-based entry point for the rebase core. Packs the long
    /// <c>(item, runner, repoId, baseBranch, workBranch, project)</c> prefix
    /// into <see cref="PipelineItemContext"/> so future collaborators take
    /// one stable parameter; behavior is identical to the canonical overload
    /// below (this method delegates verbatim).
    /// </summary>
    private Task RebaseWorkBranchInSandboxCoreAsync(
        PipelineItemContext context,
        string timingPhase,
        string? baselineImageRef,
        bool swallowReviewFailures,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RepoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.BaseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkBranch);
        ArgumentNullException.ThrowIfNull(context.Runner);
        return RebaseWorkBranchInSandboxCoreAsync(
            context.Item,
            context.Runner,
            context.RepoId,
            context.BaseBranch,
            context.WorkBranch,
            context.Project,
            timingPhase,
            baselineImageRef,
            swallowReviewFailures,
            ct);
    }

    private async Task RebaseWorkBranchInSandboxCoreAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        Project project,
        string timingPhase,
        string? baselineImageRef,
        bool swallowReviewFailures,
        CancellationToken ct)
    {
        var lockKey = $"{repoId}:{workBranch}";
        var gate = RetainPickupRebaseLock(lockKey);
        var lockEntered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct);
            lockEntered = true;

            var access = _gitHost.GetSandboxAccess(repoId);
            // Pre-resolve the exact resolver candidate set before sandbox
            // creation. Candidate environment credentials must not enter the
            // shared sandbox environment: the conflict resolver scopes only the
            // current candidate's direct variables to that candidate's execs,
            // while file-backed values are materialised through stdin. Non-secret
            // credential adjunct mounts must still be present at creation time,
            // so the typed plan below unions only compatible mounts and requests
            // an ephemeral credential tmpfs whenever the resolver has a
            // credential scope.
            //
            // Candidate/routing or mount-plan failures are captured and raised
            // only if a conflict actually needs the resolver. A clean rebase
            // never materialises candidate secrets and ignores pre-resolution
            // failures, though a successful plan can still attach validated
            // non-secret adjunct mounts and the credential tmpfs.
            //
            // Network profile prefers the agent profile (Work) so AllowedHosts
            // includes the agent's API endpoints. We fall back through the
            // audit profiles for the baseline-clone fast path when Work is
            // unconfigured.
            var (precomputedCandidates, precomputedCandidateFailure) =
                await TryBuildPickupRebaseResolverCandidatesAsync(item, project, runner, ct);
            var credentialPlan = ResolverCredentialPlan.Empty;
            if (precomputedCandidates is not null)
            {
                try
                {
                    credentialPlan = BuildResolverCredentialPlan(precomputedCandidates, access);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    precomputedCandidateFailure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            var rebaseProfile = project.NetworkProfiles.Work
                ?? project.NetworkProfiles.AuditAgent
                ?? project.NetworkProfiles.AuditTool;
            var rebaseTarget = new SandboxTarget(rebaseProfile, SandboxProfileFlavor.Headless);
            var spec = BuildSandboxSpec(
                access,
                includeAgentCredential: null,
                allowAgentNetwork: true,
                hostNetworkProfile: rebaseProfile,
                timingWorkItemId: item.Id,
                timingPhase: timingPhase,
                additionalCredentialMounts: credentialPlan.Mounts,
                includeCredentialsTmpfs: credentialPlan.RequiresCredentialsTmpfs,
                agentCredentialScope: true,
                baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, rebaseTarget, baselineImageRef));

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            await using (var cloneScope = await TimingScope.BeginAsync(
                _timings, item.Id, timingPhase, "git.clone_into_sandbox",
                activitySource: CodeyBoxActivities.Sandbox, log: _log))
            {
                await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            }

            await FetchOriginBranchAsync(sandbox, baseBranch, required: true, ct);
            var hasWorkBranch = await FetchOriginBranchAsync(sandbox, workBranch, required: false, ct)
                && await OriginBranchExistsAsync(sandbox, workBranch, ct);
            if (!hasWorkBranch)
                return;

            var oldTip = await RevParseSandboxAsync(sandbox, $"origin/{workBranch}", ct);
            var baseTip = await RevParseSandboxAsync(sandbox, $"origin/{baseBranch}", ct);
            var baseAlreadyAncestor = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "merge-base", "--is-ancestor", baseTip, oldTip],
            }, ct);
            if (baseAlreadyAncestor.Success)
                return;

            ValidatePickupRebaseWorkBranch(item, baseBranch, workBranch);

            var (gitName, gitEmail) = ResolveGitIdentity(project, _opts.HostGitIdentity, item.Initiator);
            await RunMasked(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", gitEmail);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", gitName);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", workBranch, $"origin/{workBranch}");

            IReadOnlyList<string> rebaseConflictFiles;
            IAgentRunner? rebaseReviewRunner = null;
            AgentCredential? rebaseReviewCredential = null;
            var rebaseMergeScope = _mergeScopeResolver.Resolve(item.Knobs, project.Knobs);
            await using (var rebaseScope = await TimingScope.BeginAsync(
                _timings, item.Id, timingPhase, "git.rebase_work_branch_onto_base",
                metadata: new Dictionary<string, object>
                {
                    ["change_scope"] = rebaseMergeScope.Value,
                },
                activitySource: CodeyBoxActivities.Sandbox, log: _log))
            {
                var rebaseResult = await RebaseCheckedOutBranchWithScopeFenceAsync(
                    item,
                    runner,
                    sandbox,
                    repoId,
                    baseBranch,
                    workBranch,
                    $"origin/{baseBranch}",
                    oldTip,
                    rebaseMergeScope,
                    project,
                    precomputedCandidates,
                    precomputedCandidateFailure,
                    ct);
                rebaseConflictFiles = rebaseResult.ConflictFiles;
                rebaseReviewRunner = rebaseResult.ChosenResolver;
                rebaseReviewCredential = rebaseResult.ChosenCredential;
            }

            var newTip = await RevParseSandboxAsync(sandbox, "HEAD", ct);
            if (string.Equals(newTip, oldTip, StringComparison.Ordinal))
                return;

            await using (var pushScope = await TimingScope.BeginAsync(
                _timings, item.Id, timingPhase, "git.force_push_rebased_work_branch",
                activitySource: CodeyBoxActivities.Sandbox, log: _log))
            {
                await Run(
                    sandbox,
                    "git", "-C", SandboxConventions.WorkDir,
                    "push",
                    $"--force-with-lease=refs/heads/{workBranch}:{oldTip}",
                    "origin",
                    $"HEAD:refs/heads/{workBranch}");
            }
            await sandbox.SyncStateToHostAsync(ct);

            if (rebaseConflictFiles.Count > 0 && rebaseReviewRunner is not null)
            {
                // Reuse the exact runner/credential the resolver already chose
                // for conflict resolution. A non-empty conflict-file set means a
                // conflict was resolved, so the pair is materialised; reusing it
                // keeps the advisory review on the same agent and avoids
                // emitting a second rebase_resolver.agent_selected line for one
                // work item even if cap/quota state shifts between resolution
                // and review.
                if (swallowReviewFailures)
                {
                    try
                    {
                        await RecordMergeSecurityReviewAsync(
                            item.Id,
                            repoId,
                            oldTip,
                            newTip,
                            rebaseConflictFiles,
                            project,
                            rebaseReviewRunner,
                            rebaseReviewCredential,
                            sandbox,
                            ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(ex,
                            "{Phase} rebase security review failed for work item {WorkItemId}; continuing",
                            timingPhase,
                            item.Id);
                    }
                }
                else
                {
                    await RecordMergeSecurityReviewAsync(
                        item.Id,
                        repoId,
                        oldTip,
                        newTip,
                        rebaseConflictFiles,
                        project,
                        rebaseReviewRunner,
                        rebaseReviewCredential,
                        sandbox,
                        ct);
                }
            }

            _log.LogInformation(
                "Rebased work branch {WorkBranch} for work item {WorkItemId} from {OldTip} onto base {BaseBranch} at {BaseTip}; new tip {NewTip}",
                workBranch,
                item.Id,
                oldTip,
                baseBranch,
                baseTip,
                newTip);
        }
        finally
        {
            ReleasePickupRebaseLock(lockKey, gate, lockEntered);
        }
    }

    private async Task RebaseExistingWorkBranchOntoFreshBaseAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        Project project,
        CancellationToken ct)
    {
        Validation.ValidateBranchName(baseBranch, nameof(baseBranch));
        Validation.ValidateBranchName(workBranch, nameof(workBranch));

        if (!IsPickupRebaseOwnedWorkBranch(item.Id, workBranch))
        {
            _log.LogInformation(
                "Skipping pickup-time rebase for work item {WorkItemId} branch {WorkBranch}; only {OwnedWorkBranch} is eligible for sandbox force-push",
                item.Id,
                workBranch,
                DefaultWorkBranchFor(item.Id));
            return;
        }

        await RebaseWorkBranchInSandboxCoreAsync(
            item, runner, repoId, baseBranch, workBranch, project,
            timingPhase: "pickup",
            baselineImageRef: item.BaselineImageRef,
            swallowReviewFailures: false,
            ct);
    }

    /// <summary>
    /// Best-effort incremental rebase invoked between audit iterations to
    /// keep the work branch close to <paramref name="baseBranch"/> so the
    /// pickup-time rebase at merge has less to consolidate (smaller and
    /// rarer conflicts).
    ///
    /// <para>
    /// Reuses the pickup-time rebase end-to-end — including the per-repo
    /// lock, the in-VM agentic conflict resolver, the scope-fence
    /// verification, and the merge-security-review routing through the
    /// resolver that actually resolved any conflicts. The single rebase core
    /// (<see cref="RebaseCheckedOutBranchWithScopeFenceAsync"/>) stays
    /// authoritative; this entry point is a gate + try/catch around the
    /// existing flow, not a parallel implementation. The timing-phase tag is
    /// passed through as <c>"incremental-rebase"</c> so the between-iteration
    /// flow is distinguishable from the merge-time pickup flow in
    /// observability (without it both flows would appear as <c>"pickup"</c>).
    /// </para>
    ///
    /// <para>
    /// Gates: a) hot-reloadable config flag — when
    /// <see cref="IncrementalRebaseOptions.Enabled"/> is <c>false</c> or
    /// the snapshot is unwired, returns immediately; b) only the
    /// pickup-rebase-owned (server-owned) work branch is eligible for
    /// sandbox force-push — non-owned branches return immediately.
    /// </para>
    ///
    /// <para>
    /// Failure mode: any non-cancellation failure (clone error, resolver
    /// unavailable, conflict that the cascade could not resolve, security
    /// review failure, push failure) logs a warning and returns normally.
    /// The work item proceeds with the un-rebased branch; the merge-time
    /// rebase at pickup is the authoritative retry. Cancellation
    /// propagates so the surrounding audit/rework loop tears down cleanly
    /// on shutdown or operator cancel.
    /// </para>
    /// </summary>
    private async Task MaybeIncrementalRebaseAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        Project project,
        CancellationToken ct)
    {
        var snapshot = _incrementalRebase?.Current;
        if (snapshot is null || !snapshot.Enabled)
            return;

        if (!IsPickupRebaseOwnedWorkBranch(item.Id, workBranch))
        {
            _log.LogDebug(
                "Skipping incremental rebase for work item {WorkItemId} branch {WorkBranch}; only {OwnedWorkBranch} is eligible",
                item.Id,
                workBranch,
                DefaultWorkBranchFor(item.Id));
            return;
        }

        try
        {
            await RebaseWorkBranchInSandboxCoreAsync(
                item, runner, repoId, baseBranch, workBranch, project,
                timingPhase: "incremental-rebase",
                baselineImageRef: item.BaselineImageRef,
                swallowReviewFailures: true,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SandboxDiskDeferredException)
        {
            throw;
        }
        catch (SandboxProvisioningDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Incremental rebase between audit iterations failed for work item {WorkItemId} branch {WorkBranch}; continuing with un-rebased branch (the pickup-time rebase will retry at merge)",
                item.Id,
                workBranch);
        }
    }

    private PickupRebaseLockRegistry.PickupRebaseGate RetainPickupRebaseLock(string key) =>
        _rebaseLocks.Retain(key);

    private void ReleasePickupRebaseLock(string key, PickupRebaseLockRegistry.PickupRebaseGate gate, bool releaseSemaphore) =>
        _rebaseLocks.Release(key, gate, releaseSemaphore);

    private static string DefaultWorkBranchFor(WorkItemId id) => $"codeybox/{id.ToString()[..8]}";

    private ProjectAudit ResolveAuditProfileForWorkItem(Project project, WorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AuditorProfile))
        {
            var requested = item.AuditorProfile.Trim();
            if (requested.Equals(ProjectAudit.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                || project.Audit.Profiles.ContainsKey(requested))
            {
                return project.Audit.ResolveProfile(requested);
            }

            _log.LogWarning(
                "Work item {WorkItemId} requested audit profile '{AuditProfile}', but project {ProjectId} no longer defines it; using project default audit profile",
                item.Id,
                requested,
                project.Id);
        }

        return project.Audit.ResolveProfile();
    }

    private static bool IsPickupRebaseOwnedWorkBranch(WorkItemId id, string workBranch)
        => string.Equals(workBranch, DefaultWorkBranchFor(id), StringComparison.Ordinal);

    private static bool ShouldPreserveQueuedWorkBranch(
        WorkItem item,
        string workBranch,
        bool hadRecordedWorkBranchAtEntry)
        => item.PreserveWorkBranchOnQueuedPickup
            || (hadRecordedWorkBranchAtEntry
                && item.RecoveryAttempts == 0
                && !IsPickupRebaseOwnedWorkBranch(item.Id, workBranch));

    private static void ValidatePickupRebaseWorkBranch(WorkItem item, string baseBranch, string workBranch)
    {
        var owned = DefaultWorkBranchFor(item.Id);
        if (!IsPickupRebaseOwnedWorkBranch(item.Id, workBranch)
            || string.Equals(workBranch, baseBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"pickup-time rebase may force-push only work item {item.Id}'s server-owned work branch '{owned}', not '{workBranch}'");
        }
    }

    private sealed record PickupRebaseResolutionResult(
        IReadOnlyList<string> ConflictFiles,
        IAgentRunner? ChosenResolver,
        AgentCredential? ChosenCredential);

    private async Task<(AgenticConflictCandidatesResult? Candidates, ExceptionDispatchInfo? Failure)>
        TryBuildPickupRebaseResolverCandidatesAsync(
            WorkItem item,
            Project project,
            IAgentRunner runner,
            CancellationToken ct)
    {
        try
        {
            return (await BuildAgenticConflictCandidatesAsync(item, project, runner, ct), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Pickup-time rebase resolver candidate pre-resolution failed for work item {WorkItemId}; deferring failure unless the rebase conflicts",
                item.Id);
            return (null, ExceptionDispatchInfo.Capture(ex));
        }
    }

    private sealed record ResolverCredentialPlan(
        IReadOnlyList<SandboxMount> Mounts,
        bool RequiresCredentialsTmpfs)
    {
        public static ResolverCredentialPlan Empty { get; } =
            new([], false);
    }

    private static ResolverCredentialPlan BuildResolverCredentialPlan(
        AgenticConflictCandidatesResult candidates,
        SandboxRepositoryAccess repositoryAccess)
    {
        var mountsByDestination = new Dictionary<string, SandboxMount>(StringComparer.Ordinal);
        var reservedDestinations = repositoryAccess.Mounts
            .Select(static mount => mount.SandboxPath)
            .Append(SandboxConventions.WorkDir)
            .Append(SandboxConventions.CredentialsDir)
            .ToHashSet(StringComparer.Ordinal);
        var requiresCredentialsTmpfs = false;
        foreach (var candidate in candidates.Candidates)
        {
            if (candidate.Credential is not { } credential)
                continue;
            if (credential.Agent != candidate.Runner.Kind)
            {
                throw new AgentCredentialScopeException(
                    candidate.Runner.Kind,
                    $"credential belongs to agent '{credential.Agent.Value}'");
            }
            _ = SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                credential,
                candidate.Runner,
                nameof(credential.EnvironmentVariables));

            requiresCredentialsTmpfs = true;
            foreach (var mount in credential.Mounts)
            {
                if (!mount.SandboxPath.StartsWith("/", StringComparison.Ordinal)
                    || mount.SandboxPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Any(static segment => segment is "." or ".."))
                {
                    throw new ArgumentException(
                        "Resolver candidate credential mount path must be an absolute canonical sandbox path.");
                }
                if (reservedDestinations.Contains(mount.SandboxPath))
                {
                    throw new InvalidOperationException(
                        "Resolver candidate credential mount conflicts with a reserved sandbox path.");
                }
                if (mountsByDestination.TryGetValue(mount.SandboxPath, out var existing))
                {
                    if (existing != mount)
                    {
                        throw new InvalidOperationException(
                            "Resolver candidate credential mounts conflict at the same sandbox path.");
                    }
                    continue;
                }
                mountsByDestination.Add(mount.SandboxPath, mount);
            }
        }

        return new ResolverCredentialPlan(
            mountsByDestination.Values.ToArray(),
            requiresCredentialsTmpfs);
    }

    private async Task<PickupRebaseResolutionResult> RebaseCheckedOutBranchWithScopeFenceAsync(
        WorkItem item,
        IAgentRunner runner,
        ISandbox sandbox,
        string repoId,
        string baseBranch,
        string workBranch,
        string upstreamRef,
        string oldTip,
        MergeScopeHint mergeScope,
        Project project,
        AgenticConflictCandidatesResult? precomputedCandidateResult,
        ExceptionDispatchInfo? precomputedCandidateFailure,
        CancellationToken ct)
    {
        var conflictFiles = new SortedSet<string>(StringComparer.Ordinal);
        var resolvedAnyConflict = false;
        var selectedResolverLogged = false;
        IAgentRunner? chosenResolver = null;
        AgentCredential? chosenCredential = null;
        // The candidate list is pre-resolved before sandbox creation so routing,
        // quota, and credential-provider failures are captured while the known
        // candidate set is still available. The wrapped list is still built
        // lazily on first conflict so clean rebases avoid prompt-preprocessor
        // work and any pre-resolution failure is ignored unless a resolver is
        // actually needed.
        IReadOnlyList<AgenticConflictResolverCandidate>? candidates = null;
        AgenticConflictCandidatesResult? candidateResult = precomputedCandidateResult;

        var rebase = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir,
                "rebase",
                "--keep-empty",
                "--reapply-cherry-picks",
                "--empty=keep",
                upstreamRef,
            ],
        }, ct);

        while (!rebase.Success)
        {
            try
            {
                if (candidates is null)
                {
                    precomputedCandidateFailure?.Throw();
                    candidateResult ??= await BuildAgenticConflictCandidatesAsync(item, project, runner, ct);
                    candidates = WrapPromptPreprocessedCandidates(
                        candidateResult.Candidates,
                        item.Id,
                        AgentPromptPhase.Merge,
                        iteration: 1,
                        project);
                }

                // The post-resolver HandleAgenticResolverAuthRequiredOutputAsync
                // call below is the single, deduplicated side-effect path for
                // auth-required evidence — it iterates the resolver's
                // AuthFailures with full result.Success context (so a fallback
                // success doesn't bench a candidate that produced a benign
                // login-prompt string in its diagnostics).
                var resolveResult = await _agenticConflictResolver.ResolveAsync(
                    sandbox,
                    SandboxConventions.WorkDir,
                    item.Id,
                    new AgenticConflictResolverContext(baseBranch, workBranch, AgenticConflictResolverOperation.Rebase)
                    {
                        ProjectId = project.Id,
                        MergeScope = mergeScope,
                    },
                    candidates,
                    ct);

                foreach (var path in resolveResult.ConflictFiles)
                    conflictFiles.Add(path);

                await HandleAgenticResolverAuthRequiredOutputAsync(
                    item, project, "rebase-resolver", resolveResult, ct);

                if (!resolveResult.Success || resolveResult.ChosenRunner is null)
                {
                    // Inspect the captured agent output for a login prompt
                    // BEFORE raising MergeConflictResolutionFailedException —
                    // an exit-0 login prompt that left unmerged paths would
                    // otherwise park as a generic conflict failure with no
                    // bench, no alert, and the unauthenticated agent stays
                    // routable. Use LastAttemptedRunner (populated by the
                    // resolver even on the failure path) so the correct
                    // candidate gets benched when a fallback emitted the
                    // prompt rather than the primary.
                    var emittingAgent = resolveResult.LastAttemptedRunner?.Kind ?? runner.Kind;
                    await ThrowIfAuthRequiredOutputAsync(
                        item, project, emittingAgent, "rebase-resolver",
                        resolveResult.Stdout, resolveResult.Stderr,
                        requireStdoutOnlyCorroboration: true,
                        ct: ct);

                    if (candidateResult is { HasTransientlyUnavailableStrongerAgent: true })
                    {
                        throw new AgentClassExhaustedException(
                            item.AgentClassId ?? project.DefaultAgentClass ?? "default",
                            "rebase",
                            candidateResult.Candidates.Count,
                            candidateResult.EarliestResetAt,
                            candidateResult.DeferReason ?? "stronger agent(s) transiently unavailable");
                    }
                    if (resolveResult.FailureRunner is not null
                        && resolveResult.FailureClassificationResult is not null)
                    {
                        ThrowIfTransientAgentFailure(
                            resolveResult.FailureRunner,
                            resolveResult.FailureClassificationResult,
                            "rebase");
                        var classification = _authFailureClassifier.ClassifyFailure(
                            resolveResult.FailureRunner,
                            resolveResult.FailureClassificationResult);
                        if (classification.Kind == AgentFailureKind.Infrastructure)
                        {
                            throw new MergeConflictResolutionFailedException(
                                $"pickup-time rebase resolver failed for work branch '{workBranch}'; work branch left at original tip {oldTip}: {resolveResult.Summary}",
                                failureKind: WorkItemFailureKinds.Infrastructure,
                                agent: resolveResult.FailureRunner.Kind,
                                phase: "rebase-resolver");
                        }
                    }
                    throw new MergeConflictResolutionFailedException(
                        $"pickup-time rebase resolver failed for work branch '{workBranch}'; work branch left at original tip {oldTip}: {resolveResult.Summary}");
                }

                chosenResolver = resolveResult.ChosenRunner;
                chosenCredential = resolveResult.ChosenCredential;
                if (!selectedResolverLogged)
                {
                    AuditLog.RebaseResolverAgentSelected(item.Id, chosenResolver.Kind);
                    selectedResolverLogged = true;
                }
                resolvedAnyConflict = true;

                rebase = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "--continue"],
                    ExtraEnvironment = new Dictionary<string, string>
                    {
                        ["GIT_EDITOR"] = "true",
                        ["GIT_SEQUENCE_EDITOR"] = "true",
                    },
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "--abort"],
                }, CancellationToken.None);
                // Routing, auth, pause, quota, and transient failures are not
                // merge conflict failures — let them propagate so RunAsync
                // preserves the classified work-item outcome instead of
                // overwriting them as MergeConflictResolutionFailed.
                if (ex is MergeConflictResolutionFailedException
                    or AgentUnavailableException
                    or AgentPausedException
                    or AgentAuthRequiredException
                    or AgentInfrastructureFailureException
                    or AgentClassExhaustedException
                    or TerminalTransientNetworkError)
                    throw;
                throw new MergeConflictResolutionFailedException(
                    $"pickup-time rebase of work branch '{workBranch}' onto '{baseBranch}' failed with conflicts; work branch left at original tip {oldTip}: {ex.Message}",
                    ex);
            }
        }

        _ = repoId;
        _ = oldTip;
        // The chosen runner/credential are non-null whenever a conflict was
        // actually resolved. Returning them lets the caller reuse the chosen
        // pair for the advisory merge security review instead of re-running
        // resolver selection.
        return new PickupRebaseResolutionResult(
            resolvedAnyConflict ? conflictFiles.ToArray() : [],
            resolvedAnyConflict ? chosenResolver : null,
            resolvedAnyConflict ? chosenCredential : null);
    }

    /// <summary>
    /// Builds the ordered candidate list the agentic conflict resolver walks
    /// for a single rebase or merge. The configured
    /// <see cref="ProjectAudit.AuditAgent"/> is the primary when it is set and
    /// registered, falling back to the work runner otherwise. Candidates are
    /// quota-gated with the same audit quota router path, then at-cap agents are
    /// pushed to the back while retaining them as last-resort candidates.
    /// Gate rejection reasons are preserved so reroute and unavailable events
    /// report the real cause, such as quota exhaustion, rather than a generic
    /// credential failure.
    /// </summary>
    internal async Task<AgenticConflictCandidatesResult> BuildAgenticConflictCandidatesAsync(
        WorkItem item,
        Project project,
        IAgentRunner primaryRunner,
        CancellationToken ct,
        AgenticConflictResolverOperation operation = AgenticConflictResolverOperation.Rebase)
    {
        var classId = item.AgentClassId ?? project.DefaultAgentClass;
        var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipReasons = new List<string>();
        var collected = new List<AgenticConflictResolverCandidate>();
        var transientUnavailableList = new List<(int QualityScore, string Reason, DateTimeOffset? ResetAt)>();
        (AgentKind Agent, string Reason)? pausedCandidate = null;
        var quotaRejectedCount = 0;
        var resolverSmokePhase = operation == AgenticConflictResolverOperation.Merge ? "merge" : "rebase";
        var resolverSmokeTarget = ResolvePhaseSmokeTarget(project, resolverSmokePhase, item.BaselineImageRef);

        var resolverPrimary = primaryRunner;
        var resolverPrimaryModelId = item.ModelId;
        var resolverPrimaryReasoningMode = item.ReasoningMode;
        var resolverPrimaryMember = FindCandidateMember(primaryRunner.Kind, item.ModelId, item.AgentInstanceId);

        if (project.Audit.AuditAgent is { } auditKind && auditKind != primaryRunner.Kind)
        {
            if (_agents.TryGet(auditKind, out var auditRunner))
            {
                resolverPrimary = auditRunner;
                resolverPrimaryModelId = null;
                resolverPrimaryReasoningMode = null;
                resolverPrimaryMember = FindCandidateMember(auditKind, modelId: null);
            }
            else
            {
                _log.LogWarning(
                    "Pickup-time rebase resolver: configured audit agent '{AuditKind}' is not registered; using work agent '{WorkKind}'",
                    auditKind.Value, primaryRunner.Kind.Value);
            }
        }

        var resolverPrimaryRejectedReason = await TryAddAsync(
            resolverPrimary,
            resolverPrimaryModelId,
            resolverPrimaryReasoningMode,
            resolverPrimaryMember,
            ct);

        if (_classRouter is not null && classId is not null)
        {
            foreach (var member in await _classRouter.OrderedFallbackCandidatesAsync(
                item, project, ct, resolverSmokeTarget, requireQuota: false))
            {
                if (seenMembers.Contains(member.RouteKey))
                    continue;
                if (!_agents.TryGet(member.Agent, out var memberRunner))
                {
                    seenMembers.Add(member.RouteKey);
                    skipReasons.Add($"{member.RouteKey}: no runner registered");
                    continue;
                }

                // Cross-kind candidates clear ModelId / ReasoningMode; those
                // strings are agent-specific. The class member's quota metadata
                // still gates the candidate, but the runner dispatch uses its
                // own default model unless it is the primary work runner above.
                await TryAddAsync(memberRunner, modelId: null, reasoningMode: null, member, ct);
            }
        }

        if (collected.Count == 0)
        {
            if (pausedCandidate is { } paused)
            {
                var pauseReason = quotaRejectedCount == 0
                    ? paused.Reason
                    : string.Join("; ", skipReasons);
                throw new AgentPausedException(resolverSmokePhase, paused.Agent, pauseReason);
            }

            var reasons = skipReasons.Count == 0
                ? "no candidate runner registered"
                : string.Join("; ", skipReasons);
            AuditLog.RebaseResolverAgentUnavailable(item.Id, reasons);
            throw new AgentUnavailableException(
                $"pickup-time rebase resolver could not run: no agent has viable credentials or quota ({reasons})",
                reasons);
        }

        // Deprioritize at-cap candidates while preserving primary > class-chain
        // ordering within each cap bucket. A cap of 0 (unconfigured) is treated
        // as "not at cap" so wiring agentRunningCounters without an explicit
        // cap config keeps the previous "always prefer primary" behaviour.
        const int capSortPreferred = 0;
        const int capSortDeprioritized = 1;
        var ordered = collected
            .Select((c, idx) => (Candidate: c, Index: idx, AtCap: IsCandidateAtCap(c)))
            .OrderBy(t => t.AtCap ? capSortDeprioritized : capSortPreferred)
            .ThenBy(t => t.Index)
            .Select(t => t.Candidate)
            .ToList();

        var first = ordered[0];
        var auditRebaseRouting = operation == AgenticConflictResolverOperation.Rebase;
        if (auditRebaseRouting && first.Runner.Kind != resolverPrimary.Kind)
        {
            var resolverPrimaryAtCap = collected.Any(c => c.Runner.Kind == resolverPrimary.Kind)
                && resolverPrimaryMember is { } primaryMember
                && IsAtAgentCap(primaryMember);
            if (resolverPrimaryAtCap && resolverPrimaryMember is { } reroutedMember && !IsCandidateAtCap(first))
            {
                AuditLog.RebaseResolverAgentCapReroute(
                    resolverPrimary.Kind, first.Runner.Kind,
                    GetRunningSafe(reroutedMember), GetCapSafe(reroutedMember));
            }
            else if (resolverPrimaryRejectedReason is not null)
            {
                AuditLog.RebaseResolverAgentRerouted(
                    resolverPrimary.Kind, first.Runner.Kind, $"{resolverPrimaryRejectedReason}; using class member");
            }
        }
        if (auditRebaseRouting && ordered.All(IsCandidateAtCap))
        {
            var firstMemberForCap = CandidateMembershipForCap(first);
            AuditLog.RebaseResolverAllAtCap(
                first.Runner.Kind,
                firstMemberForCap is not null
                    ? GetRunningSafe(firstMemberForCap)
                    : GetRunningSafe(first.Runner.Kind),
                firstMemberForCap is not null
                    ? GetCapSafe(firstMemberForCap)
                    : GetCapSafe(first.Runner.Kind));
        }

        var maxCollectedScore = collected.Count > 0 ? collected.Max(c => c.QualityScore) : -1;
        var strongerTransientAgents = transientUnavailableList
            .Where(t => t.QualityScore > maxCollectedScore)
            .ToList();

        string? deferReason = null;
        DateTimeOffset? earliestResetAt = null;
        if (strongerTransientAgents.Count > 0)
        {
            var bestStronger = strongerTransientAgents.OrderByDescending(t => t.QualityScore).First();
            deferReason = bestStronger.Reason;

            var resetTimes = strongerTransientAgents
                .Select(t => t.ResetAt)
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .ToList();
            if (resetTimes.Count > 0)
            {
                earliestResetAt = resetTimes.Min();
            }
        }

        return new AgenticConflictCandidatesResult(
            ordered,
            HasTransientlyUnavailableStrongerAgent: strongerTransientAgents.Count > 0,
            DeferReason: deferReason,
            EarliestResetAt: earliestResetAt);

        async Task<string?> TryAddAsync(
            IAgentRunner candidate,
            string? modelId,
            string? reasoningMode,
            AgentMembership? configuredMember,
            CancellationToken token)
        {
            var quotaMember = BuildQuotaMember(candidate, configuredMember, modelId, reasoningMode);
            var routeKey = quotaMember.RouteKey;
            if (!seenMembers.Add(routeKey))
                return null;

            var smokeAvailability = await EnsureAgentSmokeAvailableAsync(candidate.Kind, resolverSmokeTarget, token);
            if (!smokeAvailability.Available)
            {
                var availabilityReason = smokeAvailability.Reason ?? "unavailable";
                if (IsOperatorPaused(smokeAvailability))
                {
                    pausedCandidate ??= (candidate.Kind, availabilityReason);
                    var pausedSkipReason = $"{candidate.Kind.Value}: {availabilityReason}";
                    skipReasons.Add(pausedSkipReason);
                    return pausedSkipReason;
                }

                var reason = $"{candidate.Kind.Value}: smoke gate: {availabilityReason}";
                skipReasons.Add(reason);
                transientUnavailableList.Add((quotaMember.QualityScore, reason, null));
                return reason;
            }

            var (quotaOk, quotaReason) = await EvaluateAuditCandidateQuotaAsync(item.Id, candidate.Kind, quotaMember, token);
            if (!quotaOk)
            {
                var reason = $"{candidate.Kind.Value}: {quotaReason}";
                skipReasons.Add(reason);
                quotaRejectedCount++;

                DateTimeOffset? resetAt = null;
                if (ResolveQuotaProbe(quotaMember).Probe is { } probe)
                {
                    try
                    {
                        var snapshot = await probe.GetAvailabilityAsync(quotaMember, token);
                        var probeQuota = QuotaGatePolicy.ResolveMemberQuota(snapshot, quotaMember);
                        resetAt = probeQuota.ResetAt;
                    }
                    catch { }
                }

                transientUnavailableList.Add((quotaMember.QualityScore, reason, resetAt));
                return reason;
            }

            var credential = await ResolveAgentCredentialAsync(quotaMember, project, token);
            collected.Add(new AgenticConflictResolverCandidate(
                BindMemberRunner(candidate, quotaMember),
                credential,
                modelId,
                reasoningMode,
                quotaMember.RouteKey,
                quotaMember.QualityScore));
            return null;
        }

        AgentMembership? FindCandidateMember(AgentKind kind, string? modelId, string? instanceId = null)
        {
            if (_classRouter is null || classId is null)
                return null;
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                return _classRouter.FindMember(classId, kind, modelId, instanceId)
                    ?? _classRouter.FindMember(classId, kind, modelId: null, instanceId);
            }
            return _classRouter.FindMember(classId, kind, modelId: null, instanceId);
        }

        bool IsCandidateAtCap(AgenticConflictResolverCandidate candidate)
        {
            return CandidateMembershipForCap(candidate) is { } member
                ? IsAtAgentCap(member)
                : IsAtAgentCap(candidate.Runner.Kind);
        }

        static AgentMembership? CandidateMembershipForCap(AgenticConflictResolverCandidate candidate)
        {
            return candidate.AgentInstanceId is null
                ? null
                : new AgentMembership
                {
                    Agent = candidate.Runner.Kind,
                    InstanceId = candidate.AgentInstanceId,
                    Billing = AgentBilling.Subscription,
                    ModelId = candidate.ModelId,
                    QualityScore = 100,
                };
        }

        AgentMembership BuildQuotaMember(
            IAgentRunner candidate,
            AgentMembership? configuredMember,
            string? modelId,
            string? reasoningMode)
        {
            var observedModelId = ResolveObservedModelId(candidate, modelId);
            if (configuredMember is not null)
            {
                return modelId is null
                    ? configuredMember
                    : configuredMember with
                    {
                        ModelId = observedModelId,
                        ReasoningMode = reasoningMode ?? configuredMember.ReasoningMode,
                    };
            }

            return new AgentMembership
            {
                Agent = candidate.Kind,
                Billing = AgentBilling.Subscription,
                ModelId = observedModelId,
                ReasoningMode = reasoningMode,
                QualityScore = 100,
            };
        }
    }

    /// <summary>
    /// Returns true when <paramref name="agent"/> has an operator-configured
    /// per-agent cap and the live in-flight count is at or above that cap.
    /// Always false when either the cap config or the running counters are
    /// not wired — keeping the resolver's behaviour stable for tests /
    /// embeddings that don't register concurrency.
    /// </summary>
    private bool IsAtAgentCap(AgentKind agent) => _concurrencyGate.IsAtAgentCap(agent);

    private bool IsAtAgentCap(AgentMembership member) => _concurrencyGate.IsAtAgentCap(member);

    private int GetCapSafe(AgentKind agent) => _concurrencyGate.GetCapSafe(agent);

    private int GetCapSafe(AgentMembership member) => _concurrencyGate.GetCapSafe(member);

    private int GetRunningSafe(AgentKind agent) => _concurrencyGate.GetRunningSafe(agent);

    private int GetRunningSafe(AgentMembership member) => _concurrencyGate.GetRunningSafe(member);

    private static async Task<bool> FetchOriginBranchAsync(ISandbox sandbox, string branch, bool required, CancellationToken ct)
    {
        var fetch = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", SandboxConventions.WorkDir,
                "fetch", "--no-tags", "origin",
                $"+refs/heads/{branch}:refs/remotes/origin/{branch}",
            ],
        }, ct);
        if (fetch.Success)
            return true;
        if (!required)
            return false;
        throw new InvalidOperationException($"failed to fetch branch '{branch}' from origin: {fetch.Stderr}");
    }

    private static async Task<bool> OriginBranchExistsAsync(ISandbox sandbox, string branch, CancellationToken ct)
    {
        var showRef = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{branch}"],
        }, ct);
        return showRef.Success;
    }

    private static async Task<string> RevParseSandboxAsync(ISandbox sandbox, string rev, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "--verify", $"{rev}^{{commit}}"],
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"failed to resolve sandbox revision '{rev}': {result.Stderr}");
        return result.Stdout.Trim();
    }

    // Cold-tier extraction forwarder: implementation lives on PromptComposer.
    internal static string BuildInitialWorkPrompt(
        string userPrompt,
        bool allowAgentQuestions = false,
        IReadOnlyList<IAuditor>? auditors = null,
        bool selfReviewChecklistEnabled = false,
        string? approvedPlan = null) =>
        new PromptComposer().BuildInitialWorkPrompt(userPrompt, allowAgentQuestions, auditors, selfReviewChecklistEnabled, approvedPlan);

    /// <summary>
    /// Resolves the git author identity to use for sandbox commits.
    /// Precedence: linked initiator GitHub identity → project override → host
    /// global git identity → synthetic fallback.
    /// </summary>
    internal static (string Name, string Email) ResolveGitIdentity(
        Project project,
        HostGitIdentity? host,
        WorkInitiator? initiator = null)
    {
        var github = initiator?.FindProvider("github");
        if (github is not null && GitHubIdentity.TryNoreplyEmail(github, out var email))
            return (initiator!.DisplayName, email);
        if (!string.IsNullOrWhiteSpace(project.GitAuthorName) && !string.IsNullOrWhiteSpace(project.GitAuthorEmail))
            return (project.GitAuthorName, project.GitAuthorEmail);
        if (host is not null)
            return (host.Name, host.Email);
        return ("CodeyBox", "codeybox@local");
    }

    private enum ReworkNoDiffHandling
    {
        TerminalError,
        AuditEmptyRework,
    }

    private async Task<string> ResolveAgentTurnPreTurnCommitAsync(
        string repoId,
        string workBranch,
        string baseBranch,
        CancellationToken ct)
    {
        var baselineBranch = await _gitHost.BranchExistsAsync(repoId, workBranch, ct)
            ? workBranch
            : baseBranch;
        var commitSha = await _gitHost.ResolveCommitAsync(
            repoId,
            $"refs/heads/{baselineBranch}",
            ct);
        Validation.ValidateCommitSha(commitSha, nameof(commitSha));
        return commitSha.ToLowerInvariant();
    }

}
