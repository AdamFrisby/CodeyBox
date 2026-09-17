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

// PipelineRunner.AuditCollection.cs — Auditor collection: batch finding collection, build/test gate normalization, and per-auditor execution.
public sealed partial class PipelineRunner
{
    private async Task<AuditorBatchResult> CollectFindingsAsync(
        WorkItem item,
        Project project,
        IAgentRunner workRunner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        AuditContext ctx,
        bool auditShortCircuitEnabled,
        BuildTestGateEvidence initialPassedBuildTestGateEvidence,
        Func<AuditProgressUpdate, CancellationToken, Task>? progressUpdate,
        CancellationToken ct)
    {
        if (auditors.Count == 0)
            return EmptyAuditorBatchResult(initialPassedBuildTestGateEvidence);

        var enforceBuildTestGates = AuditTargetSemantics.IsCodeReview(ctx.EffectiveTarget);
        var buildTestGateAuditors = enforceBuildTestGates
            ? auditors
                .Where(a => a.Role == AuditorRole.BuildTestGate)
                .Select((auditor, index) => new { Auditor = auditor, Index = index })
                .OrderBy(x => BuildTestGateOrderingTier(x.Auditor))
                .ThenBy(x => x.Index)
                .Select(x => x.Auditor)
                .ToList()
            : new List<IAuditor>();
        var remainingAuditors = buildTestGateAuditors.Count == 0
            ? auditors
            : auditors.Where(a => a.Role != AuditorRole.BuildTestGate).ToList();

        var prefix = EmptyAuditorBatchResult(initialPassedBuildTestGateEvidence);
        if (buildTestGateAuditors.Count > 0)
        {
            var gate = await CollectFindingsBatchAsync(
                item,
                project,
                workRunner,
                buildTestGateAuditors,
                repoId,
                ctx,
                detectDeclaredShortCircuit: auditShortCircuitEnabled,
                progressUpdate,
                ct);

            prefix = MergeAuditorBatchResults(prefix, gate);
            if (gate.IncompleteVerdict)
                return prefix;
            if (gate.DeclaredShortCircuitBlocking)
                return prefix;
            if (project.Audit.StopOnFirstFailure
                && gate.Findings.Any(f => f.Severity >= project.Audit.FailingSeverity))
            {
                return prefix;
            }
        }

        if (remainingAuditors.Count == 0)
            return prefix;

        var gatedReviewAuditors = enforceBuildTestGates
            ? remainingAuditors
                .Where(RequiresPassedBuildTestGate)
                .ToList()
            : new List<IAuditor>();
        if (gatedReviewAuditors.Count > 0
            && (prefix.BuildTestGateFailed || !HasPassedBuildAndTestGateEvidence(prefix)))
        {
            _log.LogInformation(
                "Audit iteration {Iter}: skipping {Count} build/test-gated auditor(s) because verified deterministic build-and-test evidence is unavailable or a build/test gate failed",
                ctx.Iteration,
                gatedReviewAuditors.Count);
            AuditLog.LlmPanelSkippedBuildTestGate(item.Id, gatedReviewAuditors.Count);

            if (!prefix.BuildTestGateFailed && !HasPassedBuildAndTestGateEvidence(prefix))
            {
                var missingGate = MissingBuildTestGateFinding(gatedReviewAuditors);
                prefix = MergeAuditorBatchResults(
                    prefix,
                    new AuditorBatchResult([missingGate], null, false));
            }

            remainingAuditors = remainingAuditors
                .Where(a => !RequiresPassedBuildTestGate(a))
                .ToList();

            if (remainingAuditors.Count == 0)
            {
                if (progressUpdate is not null)
                {
                    await progressUpdate(
                        new AuditProgressUpdate(
                            prefix.Findings,
                            prefix.CompletedAuditors ?? []),
                        ct).ConfigureAwait(false);
                }
                return prefix;
            }
        }

        var remainingProgressUpdate = PrefixProgressUpdate(prefix, progressUpdate);
        var remaining = auditShortCircuitEnabled
            ? await CollectFindingsWithDeclaredShortCircuitAsync(
                item,
                project,
                workRunner,
                remainingAuditors,
                repoId,
                ctx,
                remainingProgressUpdate,
                ct)
            : (await CollectFindingsBatchAsync(
                item,
                project,
                workRunner,
                remainingAuditors,
                repoId,
                ctx,
                detectDeclaredShortCircuit: false,
                remainingProgressUpdate,
                ct)) with
            { DeclaredShortCircuitBlocking = false };

        return MergeAuditorBatchResults(prefix, remaining);
    }

    private async Task<AuditorBatchResult> CollectFindingsWithDeclaredShortCircuitAsync(
        WorkItem item,
        Project project,
        IAgentRunner workRunner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        AuditContext ctx,
        Func<AuditProgressUpdate, CancellationToken, Task>? progressUpdate,
        CancellationToken ct)
    {
        if (auditors.Count == 0)
            return EmptyAuditorBatchResult();

        var gateAuditors = auditors
            .Where(a => a.CanShortCircuitOnBlockingFinding)
            .ToList();
        if (gateAuditors.Count == 0)
        {
            var all = await CollectFindingsBatchAsync(
                item,
                project,
                workRunner,
                auditors,
                repoId,
                ctx,
                detectDeclaredShortCircuit: false,
                progressUpdate,
                ct);
            return all with { DeclaredShortCircuitBlocking = false };
        }

        var gate = await CollectFindingsBatchAsync(
            item,
            project,
            workRunner,
            gateAuditors,
            repoId,
            ctx,
            detectDeclaredShortCircuit: true,
            progressUpdate,
            ct);
        if (gate.DeclaredShortCircuitBlocking)
            return gate with { DeclaredShortCircuitBlocking = true };
        if (gate.IncompleteVerdict)
            return gate;
        if (gate.Findings.Any(f => f.Severity >= project.Audit.FailingSeverity))
            return gate;

        var remainingAuditors = auditors
            .Where(a => !a.CanShortCircuitOnBlockingFinding)
            .ToList();
        if (remainingAuditors.Count == 0)
            return gate with { DeclaredShortCircuitBlocking = false };

        Func<AuditProgressUpdate, CancellationToken, Task>? remainingProgressUpdate = progressUpdate is null
            ? null
            : (remainingProgress, progressCt) =>
            {
                if (remainingProgress.Operation == AuditProgressUpdateOperation.Replace)
                    return progressUpdate(remainingProgress, progressCt);

                return progressUpdate(
                    remainingProgress with
                    {
                        Findings = [.. gate.Findings, .. remainingProgress.Findings],
                        CompletedAuditors = [.. (gate.CompletedAuditors ?? []), .. remainingProgress.CompletedAuditors],
                    },
                    progressCt);
            };

        var remaining = await CollectFindingsBatchAsync(
            item,
            project,
            workRunner,
            remainingAuditors,
            repoId,
            ctx,
            detectDeclaredShortCircuit: false,
            remainingProgressUpdate,
            ct);

        return MergeAuditorBatchResults(
            gate with { DeclaredShortCircuitBlocking = false },
            remaining);
    }

    private static IReadOnlyList<IAuditor> OrderAuditorsForShortCircuit(
        IReadOnlyList<IAuditor> auditors,
        bool auditShortCircuitEnabled)
    {
        if (!auditShortCircuitEnabled || auditors.Count <= 1)
            return auditors;

        return auditors
            .Select((auditor, index) => new { Auditor = auditor, Index = index })
            .OrderBy(x => AuditorOrdering.TierOf(x.Auditor))
            .ThenBy(x => x.Index)
            .Select(x => x.Auditor)
            .ToList();
    }

    private static bool HasAuditBlockingFinding(AuditResult result, Project project)
        => result.Findings.Any(f => f.Severity >= project.Audit.FailingSeverity);

    private static bool IsDeclaredShortCircuitBlockingResult(AuditResult result)
        => !result.Passed || result.Findings.Any(f => f.Severity == AuditSeverity.Error);

    private static bool RequiresPassedBuildTestGate(IAuditor auditor)
        => auditor is IRequiresPassedBuildTestGate
           || string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase);

    private static AuditorBatchResult EmptyAuditorBatchResult()
        => new([], null, false, CompletedAuditors: []);

    private static AuditorBatchResult EmptyAuditorBatchResult(
        BuildTestGateEvidence passedBuildTestGateEvidence)
        => new(
            [],
            null,
            false,
            CompletedAuditors: [],
            PassedBuildTestGateEvidence: passedBuildTestGateEvidence);

    private static int BuildTestGateOrderingTier(IAuditor auditor)
    {
        var evidence = auditor.BuildTestGateEvidence;
        if ((evidence & BuildTestGateEvidence.Build) == BuildTestGateEvidence.Build)
            return 0;
        if ((evidence & BuildTestGateEvidence.Test) == BuildTestGateEvidence.Test)
            return 1;
        return 2;
    }

    private static AuditorBatchResult MergeAuditorBatchResults(
        AuditorBatchResult first,
        AuditorBatchResult second)
        => new(
            [.. first.Findings, .. second.Findings],
            first.ActiveAuditAgentKind ?? second.ActiveAuditAgentKind,
            first.DeclaredShortCircuitBlocking || second.DeclaredShortCircuitBlocking,
            first.IncompleteVerdict || second.IncompleteVerdict,
            [.. (first.CompletedAuditors ?? []), .. (second.CompletedAuditors ?? [])],
            [.. (first.IncompleteAuditors ?? []), .. (second.IncompleteAuditors ?? [])],
            first.PassedBuildTestGateEvidence | second.PassedBuildTestGateEvidence,
            first.BuildTestGateFailed || second.BuildTestGateFailed,
            [.. (first.TestFailureAttributions ?? []), .. (second.TestFailureAttributions ?? [])]);

    private static Func<AuditProgressUpdate, CancellationToken, Task>? PrefixProgressUpdate(
        AuditorBatchResult prefix,
        Func<AuditProgressUpdate, CancellationToken, Task>? progressUpdate)
    {
        if (progressUpdate is null)
            return null;

        return (progress, progressCt) =>
        {
            if (progress.Operation == AuditProgressUpdateOperation.Replace)
                return progressUpdate(progress, progressCt);

            return progressUpdate(
                progress with
                {
                    Findings = [.. prefix.Findings, .. progress.Findings],
                    CompletedAuditors = [.. (prefix.CompletedAuditors ?? []), .. progress.CompletedAuditors],
                },
                progressCt);
        };
    }

    private static AuditFinding MissingBuildTestGateFinding(IReadOnlyList<IAuditor> gatedReviewAuditors)
    {
        var auditorList = string.Join(", ", gatedReviewAuditors.Select(a => a.Name));
        return new AuditFinding(
            AuditorName: "audit:build-test-gate",
            Severity: AuditSeverity.Error,
            Title: "build/test-gated auditor skipped because no verified build/test gate passed",
            Description: $"The configured build/test-gated auditor(s) require verified deterministic build and test evidence before they can run: {auditorList}. Configure build/test auditor(s) with role 'build-test-gate' and gateEvidence 'build-and-test', or separate 'build' and 'test' gates, that actually run and pass before the gated auditor(s).");
    }

    private static bool HasPassedBuildAndTestGateEvidence(AuditorBatchResult result)
        => (result.PassedBuildTestGateEvidence & BuildTestGateEvidence.BuildAndTest)
           == BuildTestGateEvidence.BuildAndTest;

    private static AuditorRunRecord NormalizeBuildTestGateRun(
        AuditorRunRecord run,
        Project project,
        out BuildTestGateEvidence passedGateEvidence,
        out bool failedGate)
    {
        passedGateEvidence = BuildTestGateEvidence.None;
        failedGate = false;

        if (run.Auditor.Role != AuditorRole.BuildTestGate)
            return run;

        var blocking = HasAuditBlockingFinding(run.Result, project);
        var unverified = run.Result.Passed
            && !blocking
            && run.Result.BuildTestGateEvidenceVerified == false
            && !IsOptionalSkippedBuildTestGate(run);
        failedGate = !run.Result.Passed || blocking || unverified;
        if (!failedGate)
        {
            passedGateEvidence = BuildTestGatePassEvidence(run);
            return run;
        }

        if (unverified)
        {
            var unverifiedFindings = run.Result.Findings
                .Append(new AuditFinding(
                    AuditorName: run.Auditor.Name,
                    Severity: AuditSeverity.Error,
                    Title: "build/test gate did not verify",
                    Description: $"Build/test gate '{run.Auditor.Name}' returned a passing result but explicitly reported that its evidence was not verified. Build/test-gated auditor(s) were skipped because the CI-passed prompt claim cannot be verified."))
                .ToList();
            return run with
            {
                Result = run.Result with
                {
                    Passed = false,
                    Findings = unverifiedFindings,
                },
            };
        }

        if (blocking)
            return run.Result.Passed
                ? run with { Result = run.Result with { Passed = false } }
                : run;

        var augmentedFindings = run.Result.Findings
            .Append(new AuditFinding(
                AuditorName: run.Auditor.Name,
                Severity: AuditSeverity.Error,
                Title: "build/test gate did not pass",
                Description: $"Build/test gate '{run.Auditor.Name}' returned a non-passing result without a blocking finding. Build/test-gated auditor(s) were skipped because the CI-passed prompt claim cannot be verified."))
            .ToList();
        return run with
        {
            Result = run.Result with
            {
                Passed = false,
                Findings = augmentedFindings,
            },
        };
    }

    private static BuildTestGateEvidence BuildTestGatePassEvidence(AuditorRunRecord run)
    {
        if (!run.Result.Passed)
            return BuildTestGateEvidence.None;
        if (run.Result.BuildTestGateEvidenceVerified == false)
            return BuildTestGateEvidence.None;

        return run.Auditor.BuildTestGateEvidence;
    }

    private static bool IsOptionalSkippedBuildTestGate(AuditorRunRecord run)
        => run.Auditor.Name.Equals(WellKnownAuditorNames.BuildScript, StringComparison.OrdinalIgnoreCase)
           && run.Result.Passed
           && run.Result.BuildTestGateEvidenceVerified == false
           && run.Result.Findings.Count == 0;

    private async Task<AuditorBatchResult> CollectFindingsBatchAsync(
        WorkItem item,
        Project project,
        IAgentRunner workRunner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        AuditContext ctx,
        bool detectDeclaredShortCircuit,
        Func<AuditProgressUpdate, CancellationToken, Task>? progressUpdate,
        CancellationToken ct)
    {
        var findings = new List<AuditFinding>();
        var completedAuditors = new List<string>();
        var testFailureAttributions = new List<TestFailureAttributionResult>();
        AgentKind? activeAuditAgentKind = null;
        var declaredShortCircuitBlocking = false;
        using var progressWriteLock = new SemaphoreSlim(1, 1);

        async Task PublishPartialProgressAsync(
            IReadOnlyList<AuditFinding> currentFindings,
            IReadOnlyList<string> currentCompletedAuditors,
            CancellationToken progressCt,
            AuditProgressUpdateOperation operation = AuditProgressUpdateOperation.Accumulate)
        {
            if (progressUpdate is null)
                return;

            await progressWriteLock.WaitAsync(progressCt).ConfigureAwait(false);
            try
            {
                await progressUpdate(
                    new AuditProgressUpdate(currentFindings, currentCompletedAuditors, operation),
                    progressCt).ConfigureAwait(false);
            }
            finally
            {
                progressWriteLock.Release();
            }
        }

        Task ClearPartialProgressAsync(CancellationToken progressCt) =>
            PublishPartialProgressAsync(
                [],
                [],
                progressCt,
                AuditProgressUpdateOperation.Replace);

        // Resolve the audit agent runner per LLM auditor (once, before grouping).
        // Tool auditors don't carry a runner — they stay with workRunner as a
        // harmless sentinel that only affects grouping.
        //
        // HARD INVARIANT: every configured auditor must produce a verdict, or
        // the audit phase must surface a transient-execution failure (park /
        // infra fail) rather than silently dropping the auditor. The resolver
        // never returns null — it returns a selection, throws
        // AgentClassExhaustedException for quota exhaustion (parks the item in
        // WaitingForQuotaReset; QuotaRetryScheduler resumes the same
        // iteration once quota returns), AgentPausedException for operator
        // pauses, or AuditUnavailableException for configuration-shaped
        // absence (no audit-capable members at all, all candidates missing
        // runners or credentials) — that last one surfaces via the existing
        // RunAsync catch as failureKind="infrastructure", not a code-quality
        // finding. A silently-skipped auditor would let a Pass verdict emerge
        // with an incomplete review set.
        var resolved = new List<(IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member)>(auditors.Count);
        foreach (var a in auditors)
        {
            if (a.Required.HasFlag(AuditCapabilities.AgentCredentials))
            {
                var selection = await ResolveAuditAgentRunnerAsync(item, project, a.Name, a.Required, workRunner, ct);
                resolved.Add((a, selection.Runner, selection.Member));
            }
            else
            {
                resolved.Add((a, workRunner, null));
            }
        }

        // BuildTestGate-role auditors are always forced to the front because
        // the LLM prompt frame claims build/tests already passed. Declared
        // short-circuit auditors only get priority when the operator switch
        // is enabled; with it disabled, non-gate auditors keep their normal
        // registration order as far as the capability grouping below allows.
        resolved = OrderResolvedAuditorsForBatch(resolved, detectDeclaredShortCircuit);

        // Group by (capabilities, resolved-runner-kind) so auditors that need
        // different agent credentials get separate sandboxes — each sandbox is
        // only ever loaded with the credentials of a single agent kind.
        // Tool-only auditors all share one group (kind = default).
        var byCaps = resolved
            .GroupBy(x => (
                Caps: x.Auditor.Required,
                RouteKey: x.Auditor.Required.HasFlag(AuditCapabilities.AgentCredentials)
                    ? x.Member?.RouteKey ?? x.Runner.Kind.Value
                    : string.Empty))
            .ToList();

        // Once any BuildTestGate auditor does not pass, the LLM panel's
        // prompt-frame claim ("CI built the project and ran the full test
        // suite with no failures") would be false, so we skip LLM auditors
        // entirely for this iteration. The build/test findings still flow to
        // rework as normal.
        var passedBuildTestGateEvidence = BuildTestGateEvidence.None;
        var buildTestGateFailed = false;

        foreach (var group in byCaps)
        {
            var needsCreds = group.Key.Caps.HasFlag(AuditCapabilities.AgentCredentials);
            var needsNetwork = group.Key.Caps.HasFlag(AuditCapabilities.Network);

            // All auditors in this group share the same runner kind; pick from first.
            var groupRunner = needsCreds ? group.First().Runner : workRunner;
            var groupMember = needsCreds ? group.First().Member : null;
            // Tool-only auditors get the project's "audit-tool" profile
            // (typically isolated/no-egress); LLM-driven auditors get the
            // "audit-agent" profile (typically same as the work profile).
            AgentCredential? credential = needsCreds
                ? groupMember is not null
                    ? await ResolveAgentCredentialAsync(groupMember, project, ct)
                    : await ResolveAgentCredentialAsync(groupRunner.Kind, project, item, ct)
                : null;
            var access = _gitHost.GetSandboxAccess(repoId);
            var sandboxTarget = SandboxTargetResolver.ResolveAudit(
                needsCreds ? project.NetworkProfiles.AuditAgent : project.NetworkProfiles.AuditTool,
                group.Key.Caps);
            SandboxSpec BuildAuditSandboxSpec(SandboxRepositoryAccess repositoryAccess)
            {
                var auditSecretScope = needsCreds
                    ? ProjectSandboxSecretScopes.AuditAgent
                    : ProjectSandboxSecretScopes.AuditTool;
                var built = BuildSandboxSpec(repositoryAccess, includeAgentCredential: credential, allowAgentNetwork: needsNetwork,
                    hostNetworkProfile: sandboxTarget.NetworkProfile, timingWorkItemId: ctx.WorkItemId, timingPhase: "audit",
                    flavor: sandboxTarget.Flavor,
                    baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, sandboxTarget, item.BaselineImageRef),
                    credentialRunner: credential is null ? null : groupRunner,
                    projectSecretEnvironment: ResolveProjectSecretEnvironment(project, auditSecretScope));
                return built with
                {
                    Mounts = [.. built.Mounts, new SandboxMount { SandboxPath = "/audit", Tmpfs = true, SizeBytes = 1024 * 1024 }],
                };
            }
            var spec = BuildAuditSandboxSpec(access);

            // Within each capability group, split by Kind so tool auditors stay
            // sequential in a shared sandbox while LLM auditors each get their
            // own isolated clone and run concurrently (wall-clock ≈ max individual,
            // not sum). Tool auditors that share filesystem state must stay sequential.
            var toolPairs = group.Where(x => x.Auditor.Kind != "llm").ToList();
            var llmPairs = group.Where(x => x.Auditor.Kind == "llm").ToList();

            // Tool auditors: one shared sandbox, sequential.
            if (toolPairs.Count > 0)
            {
                async Task<ISandbox> CreatePreparedToolSandboxAsync(
                    SandboxRepositoryAccess repositoryAccess,
                    SandboxSpec sandboxSpec,
                    string auditorName,
                    AgentKind agentKind)
                {
                    var prepared = await CreateAuditSandboxWithIdleTimeoutAsync(sandboxSpec, auditorName, agentKind, item, project, ctx.Iteration, ct);
                    try
                    {
                        await RunAuditSandboxSetupWithIdleTimeoutAsync(
                            prepared,
                            auditorName,
                            agentKind,
                            item,
                            project,
                            ctx.Iteration,
                            async (setupSandbox, setupCt) =>
                            {
                                if (credential is not null && credential.Files.Count > 0)
                                    await MaterialiseCredentialFilesAsync(setupSandbox, credential, setupCt);
                                await RunWithCancellation(
                                    setupSandbox,
                                    setupCt,
                                    "git",
                                    "clone",
                                    repositoryAccess.CloneUrlInsideSandbox,
                                    SandboxConventions.WorkDir);
                                await RunWithCancellation(
                                    setupSandbox,
                                    setupCt,
                                    "git",
                                    "-C",
                                    SandboxConventions.WorkDir,
                                    "checkout",
                                    ctx.WorkBranch);
                                // Heal an inherited root-owned $HOME/.nuget once, before
                                // this shared sandbox's dotnet build/test/format gates run.
                                await HealAuditNuGetHomeAsync(setupSandbox, setupCt);
                            },
                            ct);
                        return prepared;
                    }
                    catch
                    {
                        await prepared.DisposeAsync();
                        throw;
                    }
                }

                ISandbox? sharedToolSandbox = null;
                try
                {
                    foreach (var (auditor, runner, member) in toolPairs)
                    {
                        AuditorRunRecord run;
                        if (auditor is IAuditSandboxIsolation { RequiresFreshSandbox: true })
                        {
                            string? isolatedRepoPath = null;
                            try
                            {
                                isolatedRepoPath = await _gitHost.CreateIsolatedRepositoryCloneAsync(repoId, ctx.WorkItemId, ct);
                                var isolatedAccess = _gitHost.GetIsolatedRepoSandboxAccess(isolatedRepoPath);
                                var isolatedSpec = BuildAuditSandboxSpec(isolatedAccess);
                                var timeoutAgentKind = AuditorTimeoutAgentKind(auditor, runner);
                                await using var isolatedSandbox = await CreatePreparedToolSandboxAsync(
                                    isolatedAccess,
                                    isolatedSpec,
                                    auditor.Name,
                                    timeoutAgentKind);
                                run = await ExecAuditorAsync(
                                    isolatedSandbox,
                                    auditor,
                                    runner,
                                    workRunner,
                                    credential,
                                    member?.RouteKey,
                                    item,
                                    member?.ModelId,
                                    project,
                                    ctx,
                                    ct);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException and not AuditUnavailableException and not AuditorIdleTimeoutException && !SandboxDeferralGuard.IsDeferral(ex))
                            {
                                throw new AuditUnavailableException(
                                    $"could-not-verify: isolated audit repository setup failed for {auditor.Name}: {SingleLineSummary(ex.Message)}",
                                    ex);
                            }
                            finally
                            {
                                if (isolatedRepoPath is not null)
                                {
                                    await _gitHost.DisposeIsolatedRepositoryCloneAsync(
                                        repoId,
                                        isolatedRepoPath,
                                        CancellationToken.None);
                                }
                            }
                        }
                        else
                        {
                            var timeoutAgentKind = AuditorTimeoutAgentKind(auditor, runner);
                            sharedToolSandbox ??= await CreatePreparedToolSandboxAsync(access, spec, auditor.Name, timeoutAgentKind);
                            run = await ExecAuditorAsync(
                                sharedToolSandbox,
                                auditor,
                                runner,
                                workRunner,
                                credential,
                                member?.RouteKey,
                                item,
                                member?.ModelId,
                                project,
                                ctx,
                                ct);
                        }

                        run = NormalizeBuildTestGateRun(
                            run,
                            project,
                            out var passedGateEvidence,
                            out var failedGate);
                        passedBuildTestGateEvidence |= passedGateEvidence;
                        buildTestGateFailed |= failedGate;

                        await PostProcessAuditorRunAsync(run, workRunner, needsCreds, item, project, ctx, ct);
                        if (needsCreds && runner.Kind != workRunner.Kind)
                            activeAuditAgentKind ??= runner.Kind;
                        findings.AddRange(run.Result.Findings);
                        if (run.Result.TestFailureAttributions.Count > 0)
                            testFailureAttributions.AddRange(run.Result.TestFailureAttributions);
                        completedAuditors.Add(auditor.Name);
                        await PublishPartialProgressAsync(findings.ToList(), completedAuditors.ToList(), ct);
                        if (detectDeclaredShortCircuit
                            && auditor.CanShortCircuitOnBlockingFinding
                            && IsDeclaredShortCircuitBlockingResult(run.Result))
                        {
                            declaredShortCircuitBlocking = true;
                        }
                        var blockingForThisAuditor = HasAuditBlockingFinding(run.Result, project);
                        if (project.Audit.StopOnFirstFailure && blockingForThisAuditor)
                            return new AuditorBatchResult(
                                findings.ToList(),
                                activeAuditAgentKind,
                                declaredShortCircuitBlocking,
                                CompletedAuditors: completedAuditors.ToList(),
                                PassedBuildTestGateEvidence: passedBuildTestGateEvidence,
                                BuildTestGateFailed: buildTestGateFailed,
                                TestFailureAttributions: testFailureAttributions.ToList());
                    }
                }
                catch (AuditorIdleTimeoutException ex)
                {
                    _log.LogWarning(
                        ex,
                        "Auditor {Auditor} (agent: {Agent}) timed out during iteration {Iteration}; returning incomplete audit verdict with {FindingCount} completed finding(s)",
                        ex.AuditorName,
                        ex.AgentKind.Value,
                        ctx.Iteration,
                        findings.Count);
                    return new AuditorBatchResult(
                        findings.ToList(),
                        activeAuditAgentKind,
                        declaredShortCircuitBlocking,
                        IncompleteVerdict: true,
                        CompletedAuditors: completedAuditors.ToList(),
                        IncompleteAuditors: [AuditBudgetOrdering.FormatBudgetedAuditorLabel(ex.AuditorName, ex.AgentKind.Value, ex.BudgetPath, ex.Timeout)],
                        PassedBuildTestGateEvidence: passedBuildTestGateEvidence,
                        BuildTestGateFailed: buildTestGateFailed,
                        TestFailureAttributions: testFailureAttributions.ToList());
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           && (findings.Count > 0 || completedAuditors.Count > 0))
                {
                    await ClearPartialProgressAsync(ct).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    if (sharedToolSandbox is not null)
                        await sharedToolSandbox.DisposeAsync();
                }
            }

            // LLM auditors: one sandbox per auditor, run concurrently capped by
            // MaxLlmAuditorParallelism. Independent sandboxes prevent races on
            // /audit/result.json. Post-processing is sequential and stable-ordered.
            if (llmPairs.Count > 0)
            {
                if (buildTestGateFailed)
                {
                    _log.LogInformation(
                        "Audit iteration {Iter}: skipping {Count} LLM auditor(s) because a build/test gate produced a blocking finding — the LLM prompt frame asserts CI passed, so the panel must not run when that claim is false",
                        ctx.Iteration, llmPairs.Count);
                    AuditLog.LlmPanelSkippedBuildTestGate(item.Id, llmPairs.Count);
                    continue;
                }
                var maxPar = project.Audit.MaxLlmAuditorParallelism;
                var sem = new SemaphoreSlim(maxPar, maxPar);
                var disposeSemaphoreOnExit = true;

                (SandboxSpec Spec, AuditReviewDotnetShim DotnetShim) BuildLlmSandboxSpec(
                    AgentCredential? candidateCredential,
                    IAgentRunner candidateRunner)
                {
                    var candidateSpec = BuildSandboxSpec(access,
                        includeAgentCredential: candidateCredential,
                        allowAgentNetwork: needsNetwork,
                        hostNetworkProfile: sandboxTarget.NetworkProfile,
                        timingWorkItemId: ctx.WorkItemId,
                        timingPhase: "audit",
                        flavor: sandboxTarget.Flavor,
                        baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(project, sandboxTarget, item.BaselineImageRef),
                        credentialRunner: candidateCredential is null ? null : candidateRunner,
                        projectSecretEnvironment: ResolveProjectSecretEnvironment(
                            project,
                            needsCreds ? ProjectSandboxSecretScopes.AuditAgent : ProjectSandboxSecretScopes.AuditTool));
                    var dotnetShim = AuditReviewDotnetShim.From(_pipelineTuning.Current);
                    var specWithAuditMount = candidateSpec with
                    {
                        Mounts =
                        [
                            .. candidateSpec.Mounts,
                            new SandboxMount { SandboxPath = "/audit", Tmpfs = true, SizeBytes = 1024 * 1024 },
                        ],
                    };
                    return (dotnetShim.Apply(specWithAuditMount), dotnetShim);
                }

                async Task<AuditorRunRecord> RunLlmPairOnceAsync(
                    (IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member) pair,
                    IAgentRunner candidateRunner,
                    WorkItem trialItem,
                    CancellationToken attemptCt)
                {
                    var candidateCredential = needsCreds
                        ? await ResolveAgentCredentialAsync(candidateRunner.Kind, project, trialItem, attemptCt)
                        : null;
                    var (candidateSpec, dotnetShim) = BuildLlmSandboxSpec(candidateCredential, candidateRunner);
                    await using var sandbox = await CreateAuditSandboxWithIdleTimeoutAsync(
                        candidateSpec,
                        pair.Auditor.Name,
                        candidateRunner.Kind,
                        trialItem,
                        project,
                        ctx.Iteration,
                        attemptCt);
                    await RunAuditSandboxSetupWithIdleTimeoutAsync(
                        sandbox,
                        pair.Auditor.Name,
                        candidateRunner.Kind,
                        trialItem,
                        project,
                        ctx.Iteration,
                        async (setupSandbox, setupCt) =>
                        {
                            await dotnetShim.InstallAsync(setupSandbox, setupCt);
                            if (candidateCredential is not null && candidateCredential.Files.Count > 0)
                                await MaterialiseCredentialFilesAsync(setupSandbox, candidateCredential, setupCt);
                            await RunWithCancellation(
                                setupSandbox,
                                setupCt,
                                "git",
                                "clone",
                                access.CloneUrlInsideSandbox,
                                SandboxConventions.WorkDir);
                            await RunWithCancellation(
                                setupSandbox,
                                setupCt,
                                "git",
                                "-C",
                                SandboxConventions.WorkDir,
                                "checkout",
                                ctx.WorkBranch);
                        },
                        attemptCt);
                    var candidateCtx = ctx with
                    {
                        ModelId = trialItem.ModelId,
                        ReasoningMode = trialItem.ReasoningMode,
                    };
                    return await ExecAuditorAsync(
                        sandbox,
                        pair.Auditor,
                        candidateRunner,
                        workRunner,
                        candidateCredential,
                        trialItem.AgentInstanceId,
                        trialItem,
                        // The quota-fallback wrapper rewrites trialItem.ModelId to
                        // the effective audit member's configured ModelId (initial
                        // override or a spilled fallback member), so this is the
                        // auditor's own class-member model — robust to mid-audit
                        // spill, not just pair.Member's initial value.
                        trialItem.ModelId,
                        project,
                        candidateCtx,
                        attemptCt);
                }

                async Task<AuditorRunRecord> RunLlmPairAttemptAsync(
                    (IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member) pair,
                    IAgentRunner candidateRunner,
                    WorkItem trialItem,
                    CancellationToken attemptCt)
                {
                    AuditorRunRecord run;
                    try
                    {
                        run = await RunLlmPairOnceAsync(pair, candidateRunner, trialItem, attemptCt);
                    }
                    catch (AuditorIdleTimeoutException ex)
                    {
                        _log.LogWarning(
                            ex,
                            "LLM auditor {Auditor} (agent: {Agent}) timed out; retrying once in a fresh sandbox",
                            pair.Auditor.Name,
                            candidateRunner.Kind.Value);
                        run = await RunLlmPairOnceAsync(pair, candidateRunner, trialItem, attemptCt);
                    }

                    // A nonzero review-agent exit is audit infrastructure, not a
                    // source-code finding. Auth, quota, and transient transport
                    // shapes must leave this attempt immediately so the durable
                    // availability/quota/transient schedulers own the backoff.
                    // Unknown non-quota/non-transient execution failures still get
                    // one fresh-sandbox retry.
                    if (IsLlmAgentExecutionFailure(run.Result))
                    {
                        await ThrowIfAuditorRunAuthRequiredAsync(run, needsCreds, item, project, attemptCt);
                        await ThrowIfAuditorRunQuotaAsync(run, needsCreds, item, project, attemptCt);
                        ThrowIfTransientAgentFailure(
                            run.Runner,
                            ToAgentResultForAuditFailureClassification(run.Result),
                            "audit");
                        _log.LogWarning(
                            "LLM auditor {Auditor} agent execution failed; retrying once in a fresh sandbox",
                            run.Auditor.Name);
                        run = await RunLlmPairOnceAsync(pair, candidateRunner, trialItem, attemptCt);
                    }

                    await ThrowIfAuditorRunAuthRequiredAsync(run, needsCreds, item, project, attemptCt);
                    await ThrowIfAuditorRunQuotaAsync(run, needsCreds, item, project, attemptCt);

                    // HARD INVARIANT: an auditor that could not RUN must surface as
                    // a transient execution failure, never as a code-quality finding
                    // or a Pass with a skipped review. The retry above is the one
                    // chance to ride out a transient CLI/network/process flap; if
                    // quota / transient parking has already had first claim.
                    // If the retry's result still carries the
                    // "review agent failed to run" sentinel and ThrowIfAuditorRunQuotaAsync
                    // did NOT classify it as quota, this is non-quota infrastructure:
                    // throw AuditUnavailableException so the RunAsync catch routes it
                    // to failureKind="infrastructure" rather than letting the caller
                    // post-process the Error finding into the audit findings list
                    // (which would either (a) re-introduce the 1aa5a13f false-
                    // AuditFailed regression by burning a rework iteration on an
                    // infra-shaped failure, or (b) turn an unrunnable auditor into a
                    // blocking source-code finding the work agent cannot fix).
                    if (IsLlmAgentExecutionFailure(run.Result))
                    {
                        ThrowIfTransientAgentFailure(
                            run.Runner,
                            ToAgentResultForAuditFailureClassification(run.Result),
                            "audit");
                        var summary = run.Result.AgentSummary ?? run.Result.AgentStderr ?? "agent execution failed";
                        throw new AuditUnavailableException(
                            $"LLM auditor '{run.Auditor.Name}' could not run: agent execution failed after one retry ({SingleLineSummary(summary)})");
                    }

                    return run;
                }

                Task<AuditorRunRecord> RunLlmPairAsync((IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member) pair)
                {
                    return InvokeAgentWithQuotaFallbackAsync(
                        item,
                        project,
                        "audit",
                        iteration: ctx.Iteration,
                        (candidateRunner, trialItem, attemptCt) => RunLlmPairAttemptAsync(pair, candidateRunner, trialItem, attemptCt),
                        ct,
                        initialRunnerOverride: pair.Runner,
                        initialMemberOverride: pair.Member ?? _classRouter?.FindMember(
                            item.AgentClassId ?? project.DefaultAgentClass ?? string.Empty,
                            pair.Runner.Kind,
                            modelId: null,
                            instanceId: item.AgentInstanceId),
                        // ExecAuditorAsync records one involvement row per auditor
                        // sandbox run (incl. the transient retry), so the wrapper
                        // must not also record one per attempt — that would
                        // double-count and collapse the retry into a single row.
                        recordInvolvement: false,
                        smokeTarget: SandboxTargetResolver.ToInVmSmokeTarget(project, sandboxTarget, item.BaselineImageRef),
                        // Mid-iteration spill must stay inside the audit-capability
                        // pool when one is active — a Claude audit that quota-fails
                        // must spill to another audit-capable member (e.g. Codex),
                        // never to a non-audit-capable one like Gemini.
                        requireCapability: WellKnownCapabilities.Audit);
                }

                var baseFindingsBeforeLlm = findings.ToList();
                var baseCompletedBeforeLlm = completedAuditors.ToList();
                var llmProgressGate = new object();
                var completedLlmProgress = new List<(int Index, AuditorRunRecord Run)>();

                async Task PublishLlmPartialProgressAsync(
                    int index,
                    AuditorRunRecord run,
                    CancellationToken progressCt)
                {
                    List<(int Index, AuditorRunRecord Run)> completedSnapshot;
                    lock (llmProgressGate)
                    {
                        completedLlmProgress.Add((index, run));
                        completedSnapshot = completedLlmProgress.ToList();
                    }

                    var orderedCompleted = completedSnapshot
                        .OrderBy(e => e.Index)
                        .ToList();
                    var currentFindings = baseFindingsBeforeLlm
                        .Concat(orderedCompleted.SelectMany(e => e.Run.Result.Findings))
                        .ToList();
                    var currentCompleted = baseCompletedBeforeLlm
                        .Concat(orderedCompleted.Select(e => e.Run.Auditor.Name))
                        .ToList();
                    await PublishPartialProgressAsync(currentFindings, currentCompleted, progressCt)
                        .ConfigureAwait(false);
                }

                try
                {
                    var llmTasks = llmPairs.Select(async (pair, index) =>
                    {
                        await sem.WaitAsync(ct);
                        try
                        {
                            AuditorRunRecord run;
                            try
                            {
                                run = await RunLlmPairAsync(pair);
                            }
                            catch (AgentClassExhaustedException ex)
                            {
                                // Every class member exhausted mid-iteration while
                                // running THIS auditor. The whole spill-to-peer pool
                                // is gone: capture and re-raise as the task's
                                // exception so we can surface it after sibling
                                // tasks finish. The bug report's hard invariant —
                                // a Pass verdict requires every configured auditor
                                // to have produced a verdict — means we must park,
                                // not silently skip. Counting as a finding would
                                // re-introduce the 1aa5a13f false-AuditFailed
                                // regression; raising as a transient execution
                                // failure parks the item in WaitingForQuotaReset
                                // and the QuotaRetryScheduler resumes it without
                                // burning a rework iteration.
                                AuditLog.LlmAuditorParkedQuota(item.Id, pair.Auditor.Name, ex.MemberCount);
                                _log.LogWarning(
                                    "LLM auditor '{Auditor}' could not run mid-iteration: all {Members} class member(s) exhausted ({Reason}); parking work item",
                                    pair.Auditor.Name, ex.MemberCount, ex.Message);
                                throw;
                            }
                            await PublishLlmPartialProgressAsync(index, run, ct);
                            return (Run: run, Auditor: pair.Auditor, Index: index);
                        }
                        finally { sem.Release(); }
                    }).ToList();

                    // Wait for ALL tasks to settle (success OR failure) before
                    // inspecting outcomes. Task.WhenAll itself does wait for every
                    // supplied task to complete, but `await Task.WhenAll(tasks)`
                    // surfaces only ONE of the faulted exceptions (typically the
                    // first observed by the awaiter), which can mask a sibling
                    // task's AgentClassExhaustedException behind an unrelated
                    // failure and route the work item to the generic
                    // infrastructure-failure path even though a configured
                    // auditor was quota-blocked and should have parked the item
                    // in WaitingForQuotaReset. The continuation form below never
                    // throws — exceptions stay on each Task and we walk them in
                    // stable order so exhaustion wins over sibling faults.
                    var allLlmTasksSettled = Task.WhenAll(llmTasks).ContinueWith(
                        completed =>
                        {
                            _ = completed.Exception;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    var completedLlmWait = await Task.WhenAny(allLlmTasksSettled, WaitForCancellationAsync(ct))
                        .ConfigureAwait(false);
                    if (completedLlmWait != allLlmTasksSettled)
                    {
                        disposeSemaphoreOnExit = false;
                        _ = allLlmTasksSettled.ContinueWith(
                            static (_, state) => ((SemaphoreSlim)state!).Dispose(),
                            sem,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        ct.ThrowIfCancellationRequested();
                    }

                    await allLlmTasksSettled.ConfigureAwait(false);

                    // Cancellation MUST be honoured before exhaustion / generic
                    // failures are inspected: a cancelled audit phase has to
                    // transition the work item to Cancelled, not Failed. Without
                    // this explicit re-throw, the loop below would skip the
                    // (cancelled, task.Exception=null) entries silently and the
                    // pipeline would mis-route an Operator-initiated cancel.
                    ct.ThrowIfCancellationRequested();

                    // HARD INVARIANT: a Pass verdict must never emerge while an
                    // auditor was unable to run because the entire spill-to-peer
                    // pool was quota-exhausted. Surface exhaustion FIRST (in
                    // stable auditor order), before propagating any sibling
                    // execution exception, so the work item parks for quota
                    // reset instead of being routed to failureKind="other" or
                    // "infrastructure". QuotaRetryScheduler resumes the same
                    // iteration at the earliest reset.
                    AgentClassExhaustedException? firstExhaustion = null;
                    ExceptionDispatchInfo? firstOtherException = null;
                    var incompleteAuditors = new List<string>();
                    foreach (var task in llmTasks)
                    {
                        if (task.IsCompletedSuccessfully) continue;
                        if (task.IsCanceled)
                        {
                            // A per-task cancellation that wasn't covered by the
                            // outer ct check above (e.g. a phase timeout firing
                            // on a child token). Surface as cancellation rather
                            // than letting a downstream .Result re-wrap it as a
                            // generic failure.
                            throw new OperationCanceledException(ct);
                        }
                        var inner = task.Exception?.InnerException ?? task.Exception;
                        if (inner is null) continue;
                        if (firstExhaustion is null && inner is AgentClassExhaustedException exhaustion)
                            firstExhaustion = exhaustion;
                        else if (inner is AuditorIdleTimeoutException timeout)
                            incompleteAuditors.Add(AuditBudgetOrdering.FormatBudgetedAuditorLabel(timeout.AuditorName, timeout.AgentKind.Value, timeout.BudgetPath, timeout.Timeout));
                        else if (firstExhaustion is null && firstOtherException is null)
                            firstOtherException = ExceptionDispatchInfo.Capture(inner);
                    }
                    if (firstExhaustion is not null)
                    {
                        await PublishPartialProgressAsync(
                            [],
                            [],
                            ct,
                            AuditProgressUpdateOperation.Replace).ConfigureAwait(false);
                        throw firstExhaustion;
                    }

                    if (firstOtherException is not null)
                    {
                        await ClearPartialProgressAsync(ct).ConfigureAwait(false);
                        firstOtherException.Throw();
                    }

                    if (incompleteAuditors.Count > 0)
                    {
                        var completedSnapshot = completedLlmProgress
                            .OrderBy(e => e.Index)
                            .ToList();
                        foreach (var entry in completedSnapshot)
                        {
                            var run = entry.Run;
                            await PostProcessAuditorRunAsync(run, workRunner, needsCreds, item, project, ctx, ct);
                            if (needsCreds && run.Runner.Kind != workRunner.Kind)
                                activeAuditAgentKind ??= run.Runner.Kind;
                            if (detectDeclaredShortCircuit
                                && run.Auditor.CanShortCircuitOnBlockingFinding
                                && IsDeclaredShortCircuitBlockingResult(run.Result))
                            {
                                declaredShortCircuitBlocking = true;
                            }
                        }

                        var partialFindings = baseFindingsBeforeLlm
                            .Concat(completedSnapshot.SelectMany(e => e.Run.Result.Findings))
                            .ToList();
                        var partialCompleted = baseCompletedBeforeLlm
                            .Concat(completedSnapshot.Select(e => e.Run.Auditor.Name))
                            .ToList();
                        var partialAttributions = testFailureAttributions
                            .Concat(completedSnapshot.SelectMany(e => e.Run.Result.TestFailureAttributions))
                            .ToList();
                        _log.LogWarning(
                            "Audit iteration {Iteration} has incomplete LLM auditor verdict(s): {Auditors}; continuing with {FindingCount} completed finding(s)",
                            ctx.Iteration,
                            string.Join(", ", incompleteAuditors),
                            partialFindings.Count);
                        return new AuditorBatchResult(
                            partialFindings,
                            activeAuditAgentKind,
                            declaredShortCircuitBlocking,
                            IncompleteVerdict: true,
                            CompletedAuditors: partialCompleted,
                            IncompleteAuditors: incompleteAuditors,
                            PassedBuildTestGateEvidence: passedBuildTestGateEvidence,
                            BuildTestGateFailed: buildTestGateFailed,
                            TestFailureAttributions: partialAttributions);
                    }

                    // Every task succeeded — gather results in stable order.
                    var llmRuns = llmTasks.Select(t => t.Result).OrderBy(t => t.Index).ToList();

                    // Post-process in stable auditor order (same as llmPairs).
                    // entry.Run is non-nullable here: the only path that could
                    // produce a null record was the silent-skip variant the patch
                    // removed, and exhaustion is now thrown above before we
                    // reach this loop.
                    foreach (var entry in llmRuns)
                    {
                        var run = entry.Run;
                        await PostProcessAuditorRunAsync(run, workRunner, needsCreds, item, project, ctx, ct);
                        if (needsCreds && run.Runner.Kind != workRunner.Kind)
                            activeAuditAgentKind ??= run.Runner.Kind;
                        findings.AddRange(run.Result.Findings);
                        if (run.Result.TestFailureAttributions.Count > 0)
                            testFailureAttributions.AddRange(run.Result.TestFailureAttributions);
                        completedAuditors.Add(run.Auditor.Name);
                        if (detectDeclaredShortCircuit
                            && run.Auditor.CanShortCircuitOnBlockingFinding
                            && IsDeclaredShortCircuitBlockingResult(run.Result))
                        {
                            declaredShortCircuitBlocking = true;
                        }
                    }
                    if (project.Audit.StopOnFirstFailure && findings.Any(f => f.Severity >= project.Audit.FailingSeverity))
                        return new AuditorBatchResult(
                            findings.ToList(),
                            activeAuditAgentKind,
                            declaredShortCircuitBlocking,
                            CompletedAuditors: completedAuditors.ToList(),
                            PassedBuildTestGateEvidence: passedBuildTestGateEvidence,
                            BuildTestGateFailed: buildTestGateFailed,
                            TestFailureAttributions: testFailureAttributions.ToList());
                }
                finally
                {
                    if (disposeSemaphoreOnExit)
                        sem.Dispose();
                }
            }
        }

        return new AuditorBatchResult(
            findings.ToList(),
            activeAuditAgentKind,
            declaredShortCircuitBlocking,
            CompletedAuditors: completedAuditors.ToList(),
            PassedBuildTestGateEvidence: passedBuildTestGateEvidence,
            BuildTestGateFailed: buildTestGateFailed,
            TestFailureAttributions: testFailureAttributions.ToList());
    }

    private static List<(IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member)> OrderResolvedAuditorsForBatch(
        IReadOnlyList<(IAuditor Auditor, IAgentRunner Runner, AgentMembership? Member)> resolved,
        bool detectDeclaredShortCircuit)
    {
        if (resolved.Count <= 1)
            return resolved.ToList();

        return resolved
            .Select((entry, index) => new { Entry = entry, Index = index })
            .OrderBy(x => BatchOrderingTier(x.Entry.Auditor, detectDeclaredShortCircuit))
            .ThenBy(x => x.Index)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int BatchOrderingTier(IAuditor auditor, bool detectDeclaredShortCircuit)
    {
        if (auditor.Role == AuditorRole.BuildTestGate)
            return 0;
        if (detectDeclaredShortCircuit && auditor.CanShortCircuitOnBlockingFinding)
            return 1;
        return 2;
    }

    private static AgentKind AuditorTimeoutAgentKind(IAuditor auditor, IAgentRunner runner) =>
        auditor.Required.HasFlag(AuditCapabilities.AgentCredentials)
            ? runner.Kind
            : AuditToolAgentKind;

    /// <summary>
    /// Runs a single auditor inside <paramref name="sandbox"/>, wrapping it
    /// in a timing scope. Safe to call concurrently from parallel tasks — all
    /// state is local to this invocation.
    /// </summary>
    private async Task<AuditorRunRecord> ExecAuditorAsync(
        ISandbox sandbox,
        IAuditor auditor,
        IAgentRunner runner,
        IAgentRunner workRunner,
        AgentCredential? credential,
        string? agentInstanceId,
        WorkItem item,
        string? auditorMemberModelId,
        Project project,
        AuditContext ctx,
        CancellationToken ct)
    {
        _log.LogInformation(
            "Running auditor {Name} for target {Target} (iteration {Iter})",
            auditor.Name,
            ctx.EffectiveTarget.Value,
            ctx.Iteration);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var isPlanReview = AuditTargetSemantics.IsPlanReview(ctx.EffectiveTarget);
        var auditPhase = isPlanReview
            ? $"audit-plan-llm-{auditor.Name}"
            : $"audit-llm-{auditor.Name}";
        var canCaptureStructuredStream = auditor.Kind == "llm"
            && !isPlanReview
            && await CanCaptureAuditStructuredStreamAsync(runner, sandbox, auditPhase, auditor.Name, item, project, ctx.Iteration, ct);
        // Capture only for LLM-style auditors. Tool auditors don't run an
        // agent through this codepath (see IAuditor docs — tool auditors
        // ignore AuditContext.StdoutChunkCallback), so opening a capture
        // file would leave an empty .jsonl on disk plus an empty
        // agent_stream_summaries row.
        var streamCapture = (auditor.Kind == "llm" && _agentStreams is not null && _agentStreams.Options.Enabled)
            ? await BeginAgentStreamCaptureAsync(ctx.WorkItemId, auditPhase, ctx.Iteration, ct)
            : null;
        var stdoutCallback = auditor.Kind == "llm"
            ? BuildStdoutCallback(ctx.WorkItemId, auditPhase, streamCapture)
            : null;
        // Force id-bearing structured output for resumable LLM auditors only
        // when the runner's session-resume contract requires it (see work-phase
        // comment).
        var auditNeedsStreamForResume = auditor.Kind == "llm" && !isPlanReview && NeedsStructuredStreamForSessionResume(runner);
        // Resolve the model the auditor dispatches on. A same-kind auditor keeps
        // the work item's model (ctx.ModelId, itself the work member's configured
        // ModelId). A cross-kind auditor cannot use the work model (it is
        // vendor-specific to the work kind), so it resolves the AUDITOR's own
        // configured class-member model (auditorMemberModelId) — the single source
        // of truth for that agent's model — falling back to the runner's
        // config-driven DefaultModelId only when no member model is configured.
        // The prior `crossKind ? null` rule dropped the resolved auditor model and
        // let the CLI pick its stale built-in model (the gpt-5.5 mis-route
        // incident). ReasoningMode uses the universal low/medium/high vocabulary
        // and is safe to forward across kinds.
        var auditModelId = ResolveAuditModelId(runner, workRunner.Kind, ctx.ModelId, auditorMemberModelId);
        await using var supervision = auditor.Kind == "llm" && !isPlanReview
            ? await StartAgentSupervisionSessionAsync(
                ctx.WorkItemId,
                project,
                auditPhase,
                ctx.Iteration,
                runner,
                agentInstanceId,
                auditModelId,
                ctx.ReasoningMode,
                sandbox,
                SandboxConventions.WorkDir,
                source: "audit",
                ct)
            : null;
        // Thread the resolved runner into the context so LlmReviewAuditor
        // can use the cross-review agent instead of its baked-in default.
        IAgentRunner supervisedRunner = supervision is null
            ? runner
            : new SupervisedAgentRunner(runner, supervision);
        var promptPhase = isPlanReview
            ? AgentPromptPhase.PlanReview
            : AgentPromptPhase.Audit;
        IAgentRunner promptRunner = WrapPromptPreprocessedRunner(
            supervisedRunner,
            ctx.WorkItemId,
            promptPhase,
            ctx.Iteration,
            project,
            ctx.EffectiveTarget);
        var auditorCtx = ctx with
        {
            AuditRunner = promptRunner,
            AuditCredential = credential,
            StdoutChunkCallback = stdoutCallback,
            CaptureStructuredStream = canCaptureStructuredStream || auditNeedsStreamForResume,
            ModelId = auditModelId,
            ReasoningMode = ctx.ReasoningMode,
        };
        var timingScope = await TimingScope.BeginAsync(
            _timings,
            ctx.WorkItemId,
            "audit",
            isPlanReview ? $"auditor.plan.{auditor.Name}" : $"auditor.{auditor.Name}",
            iteration: ctx.Iteration,
            metadata: new Dictionary<string, object>
            {
                ["agent"] = runner.Kind.Value,
                ["agent.instance"] = agentInstanceId ?? runner.Kind.Value,
                ["audit.target"] = ctx.EffectiveTarget.Value,
            },
            log: _log,
            activitySource: CodeyBoxActivities.Audit);
        // Record one involvement row per auditor sandbox run. ExecAuditorAsync is
        // the single chokepoint for every auditor (tool + LLM, including the LLM
        // transient retry), so recording here gives a 1:1 mapping between the
        // "Running auditor" log line above and a history row — and an
        // auditor-identifying phase the plain "audit" label could not provide.
        var involvementId = await RecordInvolvementStartAsync(
            ctx.WorkItemId,
            runner.Kind,
            agentInstanceId,
            auditorCtx.ModelId,
            isPlanReview ? $"audit:plan:{auditor.Name}" : $"audit:{auditor.Name}",
            ctx.Iteration);
        AuditResult result;
        try
        {
            await using (timingScope)
            {
                result = await RunAuditorWithIdleTimeoutAsync(
                    auditor,
                    AuditorTimeoutAgentKind(auditor, runner),
                    sandbox,
                    SandboxConventions.WorkDir,
                    auditorCtx,
                    item,
                    project,
                    ct);
            }
        }
        catch (Exception ex)
        {
            await FinalizeInvolvementAsync(involvementId, OutcomeForFailure(ex));
            throw;
        }
        finally
        {
            if (streamCapture is not null)
                await streamCapture.DisposeAsync();
        }
        result = NormalizePlanReviewRunResult(auditor, ctx, result);
        sw.Stop();
        await FinalizeInvolvementAsync(involvementId, AuditorRunOutcome(runner, result));
        CodeyBoxMeters.AuditorDuration.Record(
            (long)sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("auditor.name", auditor.Name),
            new KeyValuePair<string, object?>("auditor.kind", auditor.Kind),
            new KeyValuePair<string, object?>("audit.target", ctx.EffectiveTarget.Value),
            new KeyValuePair<string, object?>("iteration", ctx.Iteration.ToString()));
        return new AuditorRunRecord(
            auditor,
            runner,
            agentInstanceId,
            auditModelId,
            result,
            startedAt,
            sw.Elapsed,
            timingScope.ElapsedMs,
            canCaptureStructuredStream);
    }

    private static AuditResult NormalizePlanReviewRunResult(
        IAuditor auditor,
        AuditContext ctx,
        AuditResult result)
    {
        if (!AuditTargetSemantics.IsPlanReview(ctx.EffectiveTarget)
            || result.Passed
            || result.Findings.Any(f => f.Severity == AuditSeverity.Error))
        {
            return result;
        }

        return result with
        {
            Findings =
            [
                .. result.Findings,
                new AuditFinding(
                    auditor.Name,
                    AuditSeverity.Error,
                    "plan rejected by reviewer",
                    "The plan reviewer returned an explicit reject verdict (passed=false) without an error-severity finding."),
            ],
        };
    }

}
