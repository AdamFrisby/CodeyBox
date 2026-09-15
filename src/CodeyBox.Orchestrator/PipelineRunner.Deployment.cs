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

// PipelineRunner.Deployment.cs — Deployment stage: human-review gating, expiry, teardown, and auditor short-circuit ordering.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Runs the deployment stage of the cost-ordered audit ladder for one
    /// iteration. The caller guarantees the code stage is clean
    /// (<paramref name="codeStageClean"/>); this method re-checks the full
    /// provisioning policy (toggle, recipe, auditor presence) so a skipped
    /// phase provisions zero deployments.
    ///
    /// <para>On provision: exactly one deployment is stood up from the
    /// project's recipe, deployment-targeted auditors run against its live
    /// endpoint (cheap smoke/health probes before quota-spending exploration
    /// via <see cref="AuditPhaseLadder.OrderDeploymentStage"/>), and the
    /// deployment is torn down when the auditors complete — including on
    /// abort, cancel, iteration timeout, and recipe-max-lifetime expiry.
    /// Findings flow into the normal rework loop with full blocking
    /// authority (deployment probes are objective gates, never demoted).</para>
    ///
    /// <para>Returns null when the phase does not apply. Throws
    /// <see cref="AuditUnavailableException"/> when the phase applies but
    /// cannot run (missing wiring, provision failure) — a loudly visible
    /// incomplete iteration, never a fake pass. A lost handle across an
    /// orchestrator restart is swept by the deployment leak reaper.</para>
    /// </summary>
    private async Task<(DeploymentStageOutcome? Outcome, bool Parked)> RunDeploymentStageAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        int iteration,
        int? promptRevisionAtDispatch,
        IReadOnlyList<AuditFinding>? priorBlockingFindings,
        bool auditShortCircuitEnabled,
        Func<AuditProgressUpdate, CancellationToken, Task> progressUpdate,
        List<string> scheduledAuditorNames,
        IReadOnlyList<AuditFinding> codeFindings,
        IReadOnlyList<string> codeCompletedAuditors,
        bool codeStageClean,
        CancellationToken auditToken)
    {
        var deploymentAuditors = _auditorComposer.ComposeForTarget(project, runner, AuditTarget.Deployment);
        var decision = DeploymentAuditPolicy.ShouldProvision(
            project.Audit.DeploymentAuditEnabled,
            project.Deployment,
            deploymentAuditors.Count > 0,
            codeStageClean);
        if (!decision.Provision)
        {
            _log.LogInformation(
                "Audit iteration {Iteration} for work item {WorkItemId}: skipping deployment stage ({Reason})",
                iteration,
                item.Id,
                decision.Reason);
            return (null, false);
        }

        var recipe = project.Deployment!;
        if (_deploymentManager is null || _deploymentSubstrates is null)
        {
            throw new AuditUnavailableException(
                $"audit iteration {iteration} requires the deployment stage (enabled with " +
                $"{deploymentAuditors.Count} deployment auditor(s) and a '{recipe.Kind}' recipe), but no " +
                "IDeploymentManager/IDeploymentSubstrateProvider is wired into the pipeline. " +
                "Wire deployment provisioning or disable Project.Audit.DeploymentAuditEnabled.");
        }

        var ordered = auditShortCircuitEnabled
            ? AuditPhaseLadder.OrderDeploymentStage(deploymentAuditors)
            : deploymentAuditors;
        var humanAuditors = ordered.Where(IsHumanKindAuditor).ToList();
        if (humanAuditors.Count > 0)
        {
            // Async human review: provision, park, and resume. Automated
            // deployment auditors still run inline (before parking and never
            // after), but human-kind auditors never run inline — the verdict
            // arrives via the operator park/resume path.
            return await RunHumanDeploymentStageAsync(
                item,
                project,
                runner,
                repoId,
                baseBranch,
                workBranch,
                iteration,
                promptRevisionAtDispatch,
                priorBlockingFindings,
                auditShortCircuitEnabled,
                progressUpdate,
                scheduledAuditorNames,
                codeFindings,
                codeCompletedAuditors,
                humanAuditors,
                ordered.Where(a => !IsHumanKindAuditor(a)).ToList(),
                auditToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Audit iteration {Iteration} for work item {WorkItemId}: provisioning one '{Kind}' deployment for {Count} deployment auditor(s)",
            iteration,
            item.Id,
            recipe.Kind,
            ordered.Count);

        await using var deployment = await DeploymentAuditScope.ProvisionAsync(
            _deploymentManager,
            _deploymentSubstrates,
            project,
            recipe,
            () => _opts.TimeProvider.GetUtcNow(),
            _log,
            auditToken).ConfigureAwait(false);
        // Bound the deployment's life by the recipe's max lifetime on top of
        // the iteration budget: whichever fires first cancels the auditors,
        // and the scope disposal above still tears the deployment down.
        using var lifetimeCts = deployment.LinkLifetime(auditToken, () => _opts.TimeProvider.GetUtcNow());

        // Extend the scheduled-auditor list before running so partial and
        // final progress snapshots account for the deployment auditors.
        scheduledAuditorNames.AddRange(ordered.Select(a => a.Name));
        var deploymentCtx = new AuditContext(
            item.Id,
            workBranch,
            baseBranch,
            iteration,
            item.Prompt,
            ModelId: item.ModelId,
            ReasoningMode: item.ReasoningMode,
            PromptRevisionAtDispatch: promptRevisionAtDispatch,
            BuildScriptRequired: project.Audit.BuildScriptRequired,
            ProjectId: project.Id.Value,
            Target: AuditTarget.Deployment,
            PlanArtifact: item.PlanArtifact,
            PriorBlockingFindings: priorBlockingFindings,
            DeploymentEndpoint: deployment.Endpoint);

        var collection = await CollectFindingsAsync(
            item,
            project,
            runner,
            ordered,
            repoId,
            deploymentCtx,
            auditShortCircuitEnabled,
            BuildTestGateEvidence.None,
            progressUpdate,
            lifetimeCts.Token).ConfigureAwait(false);

        // Deployment probes are objective gates over live behaviour: they keep
        // full blocking authority and are never demoted to advisory.
        var blocking = collection.Findings
            .Where(f => f.Severity >= project.Audit.FailingSeverity)
            .ToList();
        return (new DeploymentStageOutcome(
            collection.Findings,
            blocking,
            collection.CompletedAuditors ?? [],
            collection.IncompleteAuditors ?? [],
            collection.ActiveAuditAgentKind,
            collection.DeclaredShortCircuitBlocking,
            collection.IncompleteVerdict,
            deployment.Endpoint), false);
    }

    /// <summary>
    /// True when the auditor is a human reviewer (declared
    /// <c>Kind = "human"</c>). Human reviewers never run inline; the
    /// deployment stage parks for their verdict instead.
    /// </summary>
    private static bool IsHumanKindAuditor(IAuditor auditor)
        => string.Equals(auditor.Kind, WellKnownAuditorKinds.Human, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the work item's active (non-consumed) human review, if any.
    /// Null when the store is unwired or no review is outstanding.
    /// </summary>
    private Task<HumanDeploymentReview?> LoadActiveHumanReviewAsync(WorkItemId id, CancellationToken ct)
        => _humanReviews is null
            ? Task.FromResult<HumanDeploymentReview?>(null)
            : _humanReviews.GetActiveForWorkItemAsync(id.ToString(), ct);

    /// <summary>
    /// Deployment stage with at least one human-kind auditor. Automated
    /// deployment auditors run inline against a freshly provisioned
    /// deployment; when they are clean the iteration parks for the human
    /// verdict (releasing the worker slot and audit sandbox while keeping
    /// only the deployment alive), and a resumed iteration consumes the
    /// recorded verdict or expiry instead of provisioning. Returns
    /// <c>Parked: true</c> when the caller must unwind the iteration
    /// (worker slot released); the deployment stays live in the manager's
    /// active set in that case and is torn down on verdict or expiry.
    /// </summary>
    private async Task<(DeploymentStageOutcome? Outcome, bool Parked)> RunHumanDeploymentStageAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        int iteration,
        int? promptRevisionAtDispatch,
        IReadOnlyList<AuditFinding>? priorBlockingFindings,
        bool auditShortCircuitEnabled,
        Func<AuditProgressUpdate, CancellationToken, Task> progressUpdate,
        List<string> scheduledAuditorNames,
        IReadOnlyList<AuditFinding> codeFindings,
        IReadOnlyList<string> codeCompletedAuditors,
        IReadOnlyList<IAuditor> humanAuditors,
        IReadOnlyList<IAuditor> automatedAuditors,
        CancellationToken auditToken)
    {
        if (_humanReviews is null)
        {
            throw new AuditUnavailableException(
                $"audit iteration {iteration} requires the human deployment-review stage " +
                $"({humanAuditors.Count} human auditor(s)) but no IHumanDeploymentReviewStore is wired " +
                "into the pipeline. Wire the review store or remove the human auditor.");
        }

        if (_questionStore is null)
        {
            throw new AuditUnavailableException(
                $"audit iteration {iteration} requires the human deployment-review stage but no " +
                "IWorkItemQuestionStore is wired into the pipeline — the operator verdict travels " +
                "through the question/answer plumbing. Wire the question store or remove the human auditor.");
        }

        var humanNames = humanAuditors.Select(a => a.Name).ToList();
        var now = _opts.TimeProvider.GetUtcNow();
        var existing = await _humanReviews.TryGetAsync(item.Id.ToString(), iteration, auditToken)
            .ConfigureAwait(false);
        if (existing is { ConsumedAt: not null })
            existing = null;
        existing ??= await _humanReviews.GetActiveForWorkItemAsync(item.Id.ToString(), auditToken)
            .ConfigureAwait(false) is { ConsumedAt: null } stale
            ? stale
            : null;
        if (existing is not null)
        {
            if (existing.Iteration != iteration)
            {
                // Unreachable without manual state surgery (the loop only
                // advances past a consumed or re-parked iteration): never
                // apply a verdict to an iteration whose code it did not
                // review. Expire it fail-closed and provision fresh.
                _log.LogWarning(
                    "Work item {Id}: human deployment review for iteration {ReviewIteration} does not match loop iteration {Iteration}; expiring it and auditing fresh",
                    item.Id, existing.Iteration, iteration);
                await ExpireStaleHumanReviewAsync(item, existing, auditToken).ConfigureAwait(false);
                existing = null;
            }
        }

        if (existing is not null)
        {
            // Resume path: consume the verdict/expiry, or re-park while
            // undecided — never provision a second deployment for the parked
            // iteration.
            return await ConsumeOrReparkHumanReviewAsync(
                item, project, existing, humanNames, now, auditToken).ConfigureAwait(false);
        }

        var recipe = project.Deployment!;
        var requestedAt = _opts.TimeProvider.GetUtcNow();
        _log.LogInformation(
            "Audit iteration {Iteration} for work item {WorkItemId}: provisioning one '{Kind}' deployment for {Auto} automated + {Human} human deployment auditor(s)",
            iteration,
            item.Id,
            recipe.Kind,
            automatedAuditors.Count,
            humanAuditors.Count);

        await using var deployment = await DeploymentAuditScope.ProvisionAsync(
            _deploymentManager!,
            _deploymentSubstrates!,
            project,
            recipe,
            () => _opts.TimeProvider.GetUtcNow(),
            _log,
            auditToken).ConfigureAwait(false);
        using var lifetimeCts = deployment.LinkLifetime(auditToken, () => _opts.TimeProvider.GetUtcNow());

        scheduledAuditorNames.AddRange(automatedAuditors.Select(a => a.Name));
        scheduledAuditorNames.AddRange(humanNames);
        var deploymentCtx = new AuditContext(
            item.Id,
            workBranch,
            baseBranch,
            iteration,
            item.Prompt,
            ModelId: item.ModelId,
            ReasoningMode: item.ReasoningMode,
            PromptRevisionAtDispatch: promptRevisionAtDispatch,
            BuildScriptRequired: project.Audit.BuildScriptRequired,
            ProjectId: project.Id.Value,
            Target: AuditTarget.Deployment,
            PlanArtifact: item.PlanArtifact,
            PriorBlockingFindings: priorBlockingFindings,
            DeploymentEndpoint: deployment.Endpoint);

        var collection = await CollectFindingsAsync(
            item,
            project,
            runner,
            automatedAuditors,
            repoId,
            deploymentCtx,
            auditShortCircuitEnabled,
            BuildTestGateEvidence.None,
            progressUpdate,
            lifetimeCts.Token).ConfigureAwait(false);

        var blocking = collection.Findings
            .Where(f => f.Severity >= project.Audit.FailingSeverity)
            .ToList();
        if (blocking.Count > 0 || collection.IncompleteVerdict)
        {
            // Automated stage dirty or incomplete: ordinary outcome, no human
            // park — the operator is only asked to review deployments the
            // automated probes accept. The scope tears the deployment down.
            return (new DeploymentStageOutcome(
                collection.Findings,
                blocking,
                collection.CompletedAuditors ?? [],
                collection.IncompleteAuditors ?? [],
                collection.ActiveAuditAgentKind,
                collection.DeclaredShortCircuitBlocking,
                collection.IncompleteVerdict,
                deployment.Endpoint), false);
        }

        var deadline = DeploymentAuditPolicy.DeadlineFor(recipe, requestedAt);
        var brief = await BuildHumanReviewBriefAsync(item, deployment.Endpoint, deadline, iteration, deployment.Handle.Id, auditToken)
            .ConfigureAwait(false);
        var pending = new HumanDeploymentReview
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            DeploymentId = deployment.Handle.Id,
            EndpointJson = JsonSerializer.Serialize(deployment.Endpoint),
            Deadline = deadline,
            RequestedAt = requestedAt,
            Brief = brief,
            QuestionId = HumanDeploymentReviewPolicy.QuestionIdFor(iteration),
            HumanAuditorsJson = HumanDeploymentReviewPolicy.SerializeStrings(humanNames),
            CodeFindingsJson = HumanDeploymentReviewPolicy.SerializeFindings(codeFindings),
            CodeCompletedJson = HumanDeploymentReviewPolicy.SerializeStrings(codeCompletedAuditors),
            AutomatedFindingsJson = HumanDeploymentReviewPolicy.SerializeFindings(collection.Findings),
            AutomatedCompletedJson = HumanDeploymentReviewPolicy.SerializeStrings(
                collection.CompletedAuditors ?? []),
            AutomatedIncompleteJson = HumanDeploymentReviewPolicy.SerializeStrings(
                collection.IncompleteAuditors ?? []),
            ActiveAuditAgentKind = collection.ActiveAuditAgentKind?.Value,
            DeclaredShortCircuitBlocking = collection.DeclaredShortCircuitBlocking,
            IncompleteVerdict = collection.IncompleteVerdict,
        };
        var effective = await _humanReviews.GetOrCreatePendingAsync(pending, auditToken).ConfigureAwait(false);

        var parked = await ParkForHumanReviewAsync(item, project, effective, iteration, auditToken)
            .ConfigureAwait(false);
        if (!parked)
        {
            // Lost a concurrent state transition: the scope tears the
            // deployment down on unwind, and the loud incomplete verdict
            // below keeps the human gate from being skipped silently.
            throw new AuditUnavailableException(
                $"audit iteration {iteration} could not park for human deployment review: " +
                "the work item state changed concurrently. The deployment was torn down; " +
                "re-queue the item to restart the review.");
        }

        // Parked: keep ONLY the deployment alive. Detaching transfers
        // teardown ownership to the review record — the resume path or the
        // expiry sweeper tears it down by re-attaching through the manager.
        deployment.Detach();
        return (null, true);
    }

    /// <summary>
    /// Resume path for a parked iteration: consumes a decided/expired review
    /// into a deployment-stage outcome (tearing the held deployment down
    /// immediately), or re-parks while the review is still undecided without
    /// provisioning again.
    /// </summary>
    private async Task<(DeploymentStageOutcome? Outcome, bool Parked)> ConsumeOrReparkHumanReviewAsync(
        WorkItem item,
        Project project,
        HumanDeploymentReview review,
        IReadOnlyList<string> humanNames,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (review.Status == HumanDeploymentReviewStatus.Pending && now >= review.Deadline)
        {
            if (await _humanReviews!.MarkExpiredAsync(
                    review.WorkItemId, review.Iteration, now, ct).ConfigureAwait(false))
            {
                review = review with
                {
                    Status = HumanDeploymentReviewStatus.Expired,
                    DecidedAt = now,
                };
                _log.LogWarning(
                    "Work item {Id}: human deployment review for iteration {Iteration} expired unreviewed at {Deadline}; failing closed",
                    item.Id, review.Iteration, review.Deadline);
            }
            else
            {
                // A verdict raced the expiry: honour the recorded verdict.
                review = await _humanReviews.TryGetAsync(
                        review.WorkItemId, review.Iteration, ct).ConfigureAwait(false)
                    ?? review;
            }
        }

        if (review.Status == HumanDeploymentReviewStatus.Pending)
        {
            // Still undecided and within deadline: re-park without
            // provisioning. The held deployment must still be alive — after
            // an orchestrator restart the handle is gone and the review
            // fails closed instead of verifying a dead endpoint.
            if (_deploymentManager is null
                || !_deploymentManager.TryGetActive(review.DeploymentId, out _))
            {
                _log.LogWarning(
                    "Work item {Id}: held deployment {DeploymentId} for human review is gone; expiring the review",
                    item.Id, review.DeploymentId);
                await _humanReviews!.MarkExpiredAsync(
                    review.WorkItemId, review.Iteration, now, ct).ConfigureAwait(false);
                review = review with
                {
                    Status = HumanDeploymentReviewStatus.Expired,
                    DecidedAt = now,
                };
            }
            else
            {
                var reparked = await ParkForHumanReviewAsync(item, project, review, review.Iteration, ct)
                    .ConfigureAwait(false);
                return (null, reparked);
            }
        }

        var outcome = BuildConsumedHumanOutcome(project, review, humanNames);
        await TearDownHeldDeploymentAsync(item, review, ct).ConfigureAwait(false);
        await _humanReviews!.MarkConsumedAsync(review.WorkItemId, review.Iteration, now, ct)
            .ConfigureAwait(false);
        return (outcome, false);
    }

    /// <summary>
    /// Merges the parked code + automated findings with the human verdict
    /// into the iteration's deployment-stage outcome. Corrupt stored payloads
    /// fail closed with a blocking finding rather than passing.
    /// </summary>
    private static DeploymentStageOutcome BuildConsumedHumanOutcome(
        Project project,
        HumanDeploymentReview review,
        IReadOnlyList<string> humanNames)
    {
        var storedHumanNames = TryDeserializeStrings(review.HumanAuditorsJson);
        var auditorName = storedHumanNames.Count > 0
            ? storedHumanNames[0]
            : humanNames.Count > 0
                ? humanNames[0]
                : WellKnownAuditorNames.HumanDeploymentReview;
        var completedHumans = storedHumanNames.Count > 0 ? storedHumanNames : humanNames;

        IReadOnlyList<AuditFinding> codeFindings = [];
        IReadOnlyList<AuditFinding> automatedFindings = [];
        var corrupt = new List<AuditFinding>();
        try
        {
            codeFindings = HumanDeploymentReviewPolicy.DeserializeFindings(review.CodeFindingsJson);
        }
        catch (InvalidOperationException ex)
        {
            corrupt.Add(new AuditFinding(
                auditorName, AuditSeverity.Error,
                "Stored human-review code findings are unreadable",
                $"The parked code-stage findings for iteration {review.Iteration} could not be decoded ({ex.Message}). Failing closed: the deployment was torn down."));
        }

        try
        {
            automatedFindings = HumanDeploymentReviewPolicy.DeserializeFindings(review.AutomatedFindingsJson);
        }
        catch (InvalidOperationException ex)
        {
            corrupt.Add(new AuditFinding(
                auditorName, AuditSeverity.Error,
                "Stored human-review deployment findings are unreadable",
                $"The parked automated deployment-stage findings for iteration {review.Iteration} could not be decoded ({ex.Message}). Failing closed: the deployment was torn down."));
        }

        var verdictFindings = HumanDeploymentReviewPolicy.BuildVerdictFindings(
            auditorName, review.Status, review.Notes, review.Deadline);
        IReadOnlyList<AuditFinding> findings =
            [.. codeFindings, .. automatedFindings, .. verdictFindings, .. corrupt];
        var blocking = findings
            .Where(f => f.Severity >= project.Audit.FailingSeverity)
            .ToList();

        DeploymentEndpoint? endpoint;
        try
        {
            endpoint = JsonSerializer.Deserialize<DeploymentEndpoint>(review.EndpointJson);
        }
        catch (JsonException)
        {
            endpoint = null;
        }

        IReadOnlyList<string> automatedCompleted;
        IReadOnlyList<string> automatedIncomplete;
        IReadOnlyList<string> codeCompleted;
        try
        {
            automatedCompleted = HumanDeploymentReviewPolicy.DeserializeStrings(review.AutomatedCompletedJson);
            automatedIncomplete = HumanDeploymentReviewPolicy.DeserializeStrings(review.AutomatedIncompleteJson);
            codeCompleted = HumanDeploymentReviewPolicy.DeserializeStrings(review.CodeCompletedJson);
        }
        catch (InvalidOperationException)
        {
            automatedCompleted = [];
            automatedIncomplete = [];
            codeCompleted = [];
            blocking.Add(new AuditFinding(
                auditorName, AuditSeverity.Error,
                "Stored human-review auditor lists are unreadable",
                $"The parked auditor lists for iteration {review.Iteration} could not be decoded. Failing closed: the deployment was torn down."));
        }

        return new DeploymentStageOutcome(
            findings,
            blocking,
            [.. codeCompleted, .. automatedCompleted, .. completedHumans],
            automatedIncomplete,
            string.IsNullOrWhiteSpace(review.ActiveAuditAgentKind)
                ? null
                : new AgentKind(review.ActiveAuditAgentKind),
            review.DeclaredShortCircuitBlocking,
            review.IncompleteVerdict,
            endpoint ?? new DeploymentEndpoint { Kind = DeploymentEndpointKind.Http });
    }

    private static IReadOnlyList<string> TryDeserializeStrings(string json)
    {
        try
        {
            return HumanDeploymentReviewPolicy.DeserializeStrings(json);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Retires a review that must never be consumed: stale across work
    /// attempts, or addressing an iteration the loop has left. Tears the
    /// held deployment down (bounded), marks the review expired, consumes it
    /// out of the active set, and dismisses the backing question — without
    /// applying its verdict anywhere. Conservative by construction: a
    /// discarded approval only ever causes a re-review, never a pass.
    /// </summary>
    private async Task ExpireStaleHumanReviewAsync(
        WorkItem item, HumanDeploymentReview review, CancellationToken ct)
    {
        var now = _opts.TimeProvider.GetUtcNow();
        await TearDownHeldDeploymentAsync(item, review, ct).ConfigureAwait(false);
        if (_humanReviews is null)
            return;
        await _humanReviews.MarkExpiredAsync(review.WorkItemId, review.Iteration, now, ct)
            .ConfigureAwait(false);
        await _humanReviews.MarkConsumedAsync(review.WorkItemId, review.Iteration, now, ct)
            .ConfigureAwait(false);
        if (_questionStore is not null)
        {
            try
            {
                await _questionStore.DismissAsync(
                    review.WorkItemId, review.QuestionId, "superseded", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(
                    ex, "Work item {Id}: failed to dismiss superseded human-review question {QuestionId}",
                    item.Id, review.QuestionId);
            }
        }
    }

    /// <summary>
    /// Best-effort teardown of the deployment held for a consumed review.
    /// Missing handles (restart, sweeper) are fine — teardown is idempotent
    /// and the verdict stands regardless.
    /// </summary>
    private async Task TearDownHeldDeploymentAsync(        WorkItem item, HumanDeploymentReview review, CancellationToken ct)
    {
        if (_deploymentManager is null
            || !_deploymentManager.TryGetActive(review.DeploymentId, out var handle)
            || handle is null)
        {
            _log.LogInformation(
                "Work item {Id}: held deployment {DeploymentId} already gone; consume continues without teardown",
                item.Id, review.DeploymentId);
            return;
        }

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation(
                "Work item {Id}: tore down held deployment {DeploymentId} after consuming human review",
                item.Id, review.DeploymentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Teardown failure must not flip the recorded verdict: the loop
            // continues with the verdict outcome and the leak reaper remains
            // the safety net for the orphaned substrate.
            _log.LogWarning(
                ex,
                "Work item {Id}: failed to tear down held deployment {DeploymentId}; verdict stands, leak reaper owns the substrate",
                item.Id, review.DeploymentId);
        }
    }

    /// <summary>
    /// Parks the iteration for human review: creates the backing operator
    /// question (endpoint + expiry + acceptance criteria), notifies via the
    /// existing question/webhook plumbing, and transitions the item to
    /// <c>NeedsOperatorInput</c> — releasing the worker slot and audit
    /// sandbox. <c>NeedsOperatorInput</c> is outside both watchdogs'
    /// watched states, so a parked-on-human item is never flagged stalled;
    /// the transition message annotates the park. Returns false when a
    /// concurrent state change made the transition impossible.
    /// </summary>
    private async Task<bool> ParkForHumanReviewAsync(
        WorkItem item,
        Project project,
        HumanDeploymentReview review,
        int iteration,
        CancellationToken ct)
    {
        var utcNow = _opts.TimeProvider.GetUtcNow();
        var created = await _questionStore!.CreateIfNotExistsAsync(new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            QuestionId = review.QuestionId,
            QuestionText = review.Brief,
            AskedAt = utcNow,
        }, ct).ConfigureAwait(false);

        var fresh = await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
        if (created)
        {
            AuditLog.WorkItemTransitioned(item.Id, $"question_asked:{review.QuestionId}");
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.question_asked",
                WorkItem = fresh,
                Project = project,
                Details = new QuestionAskedDetails(
                    item.Id.ToString(), project.Id.Value, review.QuestionId, review.Brief),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        var endpointDescription = HumanDeploymentReviewPolicy.DescribeEndpoint(TryParseEndpoint(review.EndpointJson));
        var message =
            $"Parked for human deployment review (iteration {iteration}): deployment {review.DeploymentId} " +
            $"at {endpointDescription} awaiting operator verdict; expires {review.Deadline:O}.";
        var parked = false;
        await RunBoundedPostAgentAsync(item.Id, "audit-human-review-park", ct, async transitionCt =>
        {
            var current = await _store.GetAsync(item.Id, transitionCt).ConfigureAwait(false) ?? item;
            if (current.State != WorkItemState.Auditing)
            {
                _log.LogInformation(
                    "Work item {Id} left Auditing concurrently ({State}); skipping human-review park",
                    item.Id, current.State);
                return;
            }

            var updated = await _store.TryUpdateIfStateAsync(
                current.With(WorkItemState.NeedsOperatorInput, message),
                WorkItemState.Auditing,
                transitionCt).ConfigureAwait(false);
            if (!updated)
            {
                _log.LogInformation(
                    "Work item {Id} state changed concurrently; skipping human-review park",
                    item.Id);
                return;
            }

            parked = true;
            _log.LogWarning(
                "Work item {Id} parked at iteration {Iteration} for human deployment review: {DeploymentId} expires {Deadline}",
                item.Id, iteration, review.DeploymentId, review.Deadline);
            AuditLog.WorkItemTransitioned(item.Id, $"NeedsOperatorInput (human deployment review; {review.DeploymentId})");
            CodeyBoxMeters.PipelineTransitions.Add(1,
                new KeyValuePair<string, object?>("to_state", WorkItemState.NeedsOperatorInput.ToString()));

            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.needs_operator_input",
                WorkItem = current.With(WorkItemState.NeedsOperatorInput, message),
                Project = project,
                Details = new HumanReviewParkedDetails(
                    item.Id.ToString(),
                    project.Id.Value,
                    iteration,
                    review.DeploymentId,
                    endpointDescription,
                    review.Deadline,
                    review.QuestionId),
            }, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return parked;
    }

    private static DeploymentEndpoint? TryParseEndpoint(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DeploymentEndpoint>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the operator brief from the live endpoint, the deadline, and
    /// the item's acceptance criteria (linked test cases, bounded). Pure
    /// assembly — see <see cref="HumanDeploymentReviewPolicy.BuildBrief"/>
    /// for the truncation contract.
    /// </summary>
    private async Task<string> BuildHumanReviewBriefAsync(
        WorkItem item,
        DeploymentEndpoint endpoint,
        DateTimeOffset deadline,
        int iteration,
        string deploymentId,
        CancellationToken ct)
    {
        var fresh = await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
        var criteria = new List<(string Name, string Description)>();
        if (_testCaseStore is not null)
        {
            try
            {
                await foreach (var testCase in _testCaseStore
                    .ListByWorkItemAsync(item.Id.ToString(), ct).ConfigureAwait(false))
                {
                    if (testCase.IsArchived) continue;
                    criteria.Add((testCase.Name, testCase.Description));
                    if (criteria.Count >= HumanDeploymentReviewPolicy.MaxCriteriaEntries) break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Criteria are advisory context for the operator, not the
                // verdict: a store failure degrades the brief instead of the
                // review.
                _log.LogWarning(ex, "Work item {Id}: failed to load test cases for human-review brief", item.Id);
            }
        }

        return HumanDeploymentReviewPolicy.BuildBrief(
            fresh.Title,
            fresh.Prompt,
            HumanDeploymentReviewPolicy.DescribeEndpoint(endpoint),
            deadline,
            criteria,
            iteration,
            deploymentId);
    }

}
