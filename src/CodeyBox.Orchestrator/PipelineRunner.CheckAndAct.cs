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

// PipelineRunner.CheckAndAct.cs — Check-and-act completion flow: post-work verification context, agent re-checks, and completion review.
public sealed partial class PipelineRunner
{
    /// <summary>
    /// Executes a <see cref="JobType.CheckAndAct"/> work item end-to-end: spins
    /// up a sandbox with a read-only clone of the project repo at the base
    /// branch, runs a SINGLE agent invocation with the verdict-protocol prompt,
    /// parses the structured JSON verdict from agent stdout, persists it on
    /// the work item, and — when the verdict matches
    /// <see cref="CheckAndActSpec.ActionableAnswer"/> — enqueues a Normal
    /// follow-up item built from <see cref="CheckAndActSpec.OnYes"/>. Finishes
    /// the work item Done on a parsable verdict (regardless of yes/no);
    /// transitions to Failed with <c>failureKind=other</c> on a missing /
    /// malformed verdict so the operator can surface the misbehaviour. Never
    /// commits, pushes, opens a PR, or otherwise mutates the project repo.
    /// </summary>
    private async Task RunCheckAndActAsync(
        WorkItem item, Project project, IAgentRunner agentRunner, CancellationToken ct)
    {
        if (item.Check is null || item.Check.OnYes is null)
        {
            await TransitionFailed(item,
                "check-and-act item is missing a check spec (or its on-yes action) — refusing to dispatch",
                CancellationToken.None, project, failureKind: "other");
            return;
        }
        var checkSpec = item.Check;

        try
        {
            var configuredBaseBranch = item.BaseBranch ?? project.DefaultBaseBranch;
            var (repoId, baseBranch) = await EnsurePipelineRepositoryAsync(item, project, configuredBaseBranch, ct);

            await Transition(item, WorkItemState.Working, ct, project);

            string? stdout = null;
            if (string.Equals(checkSpec.Mode, CheckAndActModes.Completion, StringComparison.OrdinalIgnoreCase))
            {
                stdout = await TryRunCheckAndActCompletionAsync(
                    item,
                    project,
                    checkSpec,
                    repoId,
                    baseBranch,
                    targetBranch: baseBranch,
                    phase: "check",
                    iteration: null,
                    ct);
            }

            if (stdout is null)
            {
                if (string.Equals(checkSpec.Mode, CheckAndActModes.Completion, StringComparison.OrdinalIgnoreCase)
                    && !await EnsureCheckAgenticFallbackAvailableAsync(item, project, agentRunner.Kind, ct))
                {
                    return;
                }

                var prompt = CheckAndActPipeline.BuildPrompt(checkSpec);
                stdout = await RunCheckAndActAgentAsync(item, project, agentRunner, repoId, baseBranch, prompt, ct);
            }

            if (!CheckAndActPipeline.TryParseVerdict(stdout, out var verdict, out var parseError))
            {
                AuditLog.WorkItemFailed(item.Id, $"check-and-act: {parseError}");
                await TransitionFailed(item,
                    $"check-and-act verdict parse failure: {parseError}",
                    CancellationToken.None, project, failureKind: "other");
                return;
            }

            // Persist the verdict. We re-read the item to avoid clobbering any
            // concurrent partial-update (priority / prompt) that may have
            // landed mid-flight.
            var current = await _store.GetAsync(item.Id, ct) ?? item;
            var withVerdict = current with { Verdict = verdict };
            await _store.UpdateAsync(withVerdict, ct);
            item = withVerdict;

            _log.LogInformation(
                "Work item {Id} check verdict: answer={Answer} confidence={Confidence}",
                item.Id, verdict!.Answer, verdict.Confidence ?? "(unspecified)");

            // Only enqueue the on-yes follow-up when the verdict matches the
            // actionable condition. A non-matching verdict still completes Done
            // — the recorded verdict is the deliverable.
            if (verdict.Answer == checkSpec.ActionableAnswer)
            {
                await EnqueueOnYesFollowupAsync(item, project, checkSpec.OnYes, ct);
            }

            await Transition(item, WorkItemState.Done, ct, project);
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
        catch (TerminalTransientNetworkError ex)
        {
            _log.LogWarning("Work item {Id} check-and-act hit transient transport failure: {Error}", item.Id, ex.Message);
            await TransitionWaitingForTransientRetryAsync(item, ex, project);
        }
        catch (AgentAuthRequiredException authEx)
        {
            // TerminalFailureClassifier treats AuthRequired as Deterministic
            // (no auto-retry), so this is a terminal failure, not a pause —
            // word the log accordingly so operators grepping for "paused"
            // don't think the item is parked awaiting auth.
            _log.LogWarning(
                "Work item {Id} check-and-act failed because agent {Agent} requires re-authentication in phase {Phase}: {Reason}",
                item.Id, authEx.Agent.Value, authEx.Phase, authEx.Message);
            await TransitionFailed(
                item,
                authEx.Message,
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.AuthRequired,
                agent: authEx.Agent,
                authFailureScope: authEx.Scope);
        }
        catch (AgentInfrastructureFailureException infraEx)
        {
            _log.LogWarning(
                "Work item {Id} check-and-act failed because agent {Agent} hit infrastructure failure in phase {Phase}: {Reason}",
                item.Id, infraEx.Agent.Value, infraEx.Phase, infraEx.Message);
            await TransitionFailed(
                item,
                infraEx.Message,
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.Infrastructure,
                agent: infraEx.Agent);
        }
        catch (GitRepositorySeedingException seedEx)
        {
            // Repository seeding runs on the orchestrator host against the git
            // remote — no agent, no provider quota. Always infrastructure,
            // never quota evidence.
            _log.LogWarning(
                seedEx,
                "Work item {Id} check-and-act failed during repository {Operation}: {Error}",
                item.Id, seedEx.Operation, seedEx.Message);
            await TransitionFailed(
                item,
                seedEx.Message,
                CancellationToken.None,
                project,
                failureKind: WorkItemFailureKinds.Infrastructure);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} check-and-act failed", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None, project, failureKind: "other");
        }
    }

    private async Task<string?> TryRunCheckAndActCompletionAsync(
        WorkItem item,
        Project project,
        CheckAndActSpec checkSpec,
        string repoId,
        string baseBranch,
        string targetBranch,
        string phase,
        int? iteration,
        CancellationToken ct)
    {
        if (_checkCompletionRunner is null)
            return null;

        var startedAt = DateTimeOffset.UtcNow;
        var reviewContext = await BuildCompletionReviewContextAsync(repoId, baseBranch, targetBranch, ct);
        var blocks = CheckAndActPipeline.BuildCompletionPromptBlocks(checkSpec, reviewContext);
        var credentials = new CheckAndActCompletionCredentials(
            Gemini: await ResolveAgentCredentialAsync(AgentKind.Gemini, project, item, ct),
            Codex: await ResolveAgentCredentialAsync(AgentKind.Codex, project, item, ct),
            Claude: await ResolveAgentCredentialAsync(AgentKind.Claude, project, item, ct));

        var result = await _checkCompletionRunner.TryCompleteAsync(
            new CheckAndActCompletionRequest(
                item.Id,
                phase,
                iteration,
                blocks,
                credentials,
                ModelId: item.ModelId),
            ct);
        if (result is null)
        {
            _log.LogInformation(
                "Work item {Id} requested check-and-act completion mode for phase {Phase}, but no account-safe completion provider is configured; falling back to agentic mode",
                item.Id,
                phase);
            return null;
        }

        var endedAt = DateTimeOffset.UtcNow;
        _stdoutBroadcaster?.BroadcastChunk(item.Id, phase, result.Output);
        await TryRecordCompletionCostAsync(result, item, phase, iteration, startedAt, endedAt);
        _log.LogInformation(
            "Work item {Id} check-and-act completion used {Provider} cacheHit={CacheHit} input={Input} cached={Cached} output={Output}",
            item.Id,
            result.Provider,
            result.Usage.CacheHit,
            result.Usage.InputTokens,
            result.Usage.CachedInputTokens,
            result.Usage.OutputTokens);
        return result.Output;
    }

    private async Task<string> BuildCompletionReviewContextAsync(
        string repoId,
        string baseBranch,
        string targetBranch,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Base branch: ").AppendLine(baseBranch);
        sb.Append("Target branch: ").AppendLine(targetBranch);

        string? targetCommit = null;
        try
        {
            targetCommit = await _gitHost.ResolveCommitAsync(repoId, targetBranch, ct);
            sb.Append("Target commit: ").AppendLine(targetCommit);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not resolve target branch {TargetBranch} for completion check context", targetBranch);
        }

        var includeDiff = !string.Equals(baseBranch, targetBranch, StringComparison.Ordinal);
        if (includeDiff)
        {
            var (diffStat, fullDiff) = await _gitHost.GetDiffAsync(repoId, baseBranch, targetBranch, ct);
            AppendSection(sb, "Diff stat", string.IsNullOrWhiteSpace(diffStat) ? "(empty)" : diffStat);
            AppendSectionCapped(sb, "Unified diff", string.IsNullOrWhiteSpace(fullDiff) ? "(empty)" : fullDiff);
        }
        else
        {
            AppendSection(sb, "Diff", "(initial check against the target branch; no work-branch diff exists)");
        }

        IReadOnlyList<string> files;
        try
        {
            files = await _gitHost.ListFilesAsync(repoId, targetBranch, pathPrefix: null, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not list files for completion check context");
            AppendSection(sb, "File listing", "(unavailable)");
            return Truncate(sb.ToString(), CompletionReviewContextMaxChars);
        }

        var ordered = files.OrderBy(static f => f, StringComparer.Ordinal).ToList();
        AppendSection(
            sb,
            $"File listing ({ordered.Count} total)",
            string.Join('\n', ordered.Take(CompletionReviewMaxFiles)));
        if (ordered.Count > CompletionReviewMaxFiles)
            sb.AppendLine($"(listing truncated after {CompletionReviewMaxFiles} files)");

        var selectedFiles = SelectCompletionContextFiles(ordered, includeDiff, repoId, baseBranch, targetBranch, ct);
        await foreach (var (path, content) in selectedFiles)
        {
            if (sb.Length >= CompletionReviewContextMaxChars)
                break;
            AppendSectionCapped(sb, $"File: {path}", content);
        }

        return Truncate(sb.ToString(), CompletionReviewContextMaxChars);
    }

    private async IAsyncEnumerable<(string Path, string Content)> SelectCompletionContextFiles(
        IReadOnlyList<string> orderedFiles,
        bool includeDiff,
        string repoId,
        string baseBranch,
        string targetBranch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IEnumerable<string> candidates = orderedFiles;
        if (includeDiff)
        {
            try
            {
                var changed = await _gitHost.GetChangedPathsAsync(repoId, baseBranch, targetBranch, ct);
                var changedPaths = changed
                    .Select(static c => c.Path)
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .OrderBy(static p => p, StringComparer.Ordinal)
                    .ToList();
                if (changedPaths.Count > 0)
                    candidates = changedPaths;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not list changed files for completion check context; falling back to tree order");
            }
        }

        var selected = 0;
        foreach (var file in candidates)
        {
            if (selected >= CompletionReviewMaxFiles)
                yield break;
            if (!LooksLikeUsefulTextFile(file))
                continue;
            string content;
            try
            {
                content = await _gitHost.ReadTextFileAsync(repoId, targetBranch, file, ct);
            }
            catch
            {
                continue;
            }
            if (content.IndexOf('\0', StringComparison.Ordinal) >= 0)
                continue;
            selected++;
            yield return (file, Truncate(content, CompletionReviewFileMaxChars));
        }
    }

    private async Task<bool> EnsureCheckAgenticFallbackAvailableAsync(
        WorkItem item,
        Project project,
        AgentKind agentKind,
        CancellationToken ct)
    {
        var smokeTarget = ResolvePhaseSmokeTarget(project, "check", item.BaselineImageRef);
        var availability = await EnsureAgentSmokeAvailableAsync(agentKind, smokeTarget, ct);
        if (availability.Available)
            return true;

        var reason = availability.Reason ?? "in-VM smoke gate excluded agent";
        if (IsOperatorPaused(availability))
        {
            await TransitionWaitingForAgentResumeAsync(item, reason, project, agentKind);
            return false;
        }

        AuditLog.AgentSmokeFailed(agentKind, reason, TimeSpan.Zero, SmokeFailureCategory.Unknown);
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = "agent.smoke_failed",
            WorkItem = item,
            Project = project,
            Details = new AgentSmokeFailedDetails
            {
                AgentKind = agentKind.Value,
                Reason = reason,
            },
        }, CancellationToken.None);
        await TransitionFailed(
            item,
            $"in-VM smoke gate: {reason}",
            CancellationToken.None,
            project,
            failureKind: WorkItemFailureKinds.AgentUnavailable,
            agent: agentKind);
        return false;
    }

    private static void AppendSection(StringBuilder sb, string title, string body)
    {
        sb.AppendLine();
        sb.Append("### ").AppendLine(title);
        sb.AppendLine();
        sb.AppendLine(body.TrimEnd());
    }

    private static void AppendSectionCapped(StringBuilder sb, string title, string body)
    {
        var remaining = CompletionReviewContextMaxChars - sb.Length;
        if (remaining <= 0)
            return;
        var content = body.Length > remaining ? body[..Math.Max(0, remaining - 64)] + "\n...(truncated)" : body;
        AppendSection(sb, title, content);
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..Math.Max(0, maxChars - 15)] + "\n...(truncated)";

    private static bool LooksLikeUsefulTextFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "package-lock.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "yarn.lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext is ".cs" or ".fs" or ".vb" or ".js" or ".jsx" or ".ts" or ".tsx"
            or ".py" or ".go" or ".rs" or ".java" or ".kt" or ".kts"
            or ".rb" or ".php" or ".c" or ".h" or ".cc" or ".cpp" or ".hpp"
            or ".sql" or ".html" or ".css" or ".scss" or ".json" or ".yaml"
            or ".yml" or ".xml" or ".md" or ".sh" or ".ps1" or ".toml"
            or ".gradle" or ".tf"
            || string.Equals(name, "Dockerfile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Makefile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the agent inside a project sandbox for the check phase. Mirrors
    /// the work-phase sandbox / clone scaffolding but never commits, never
    /// pushes, never opens a PR — the agent's only deliverable is the
    /// structured verdict on stdout. Returns the aggregated stdout chunks
    /// (via callback) and final <see cref="AgentResult.Stdout"/> concatenated
    /// so the verdict parser sees both streamed deltas and any one-shot final
    /// payload. Throws on agent failure so the outer catch in
    /// <see cref="RunCheckAndActAsync"/> records it as Failed.
    /// </summary>
    private async Task<string> RunCheckAndActAgentAsync(
        WorkItem item, Project project, IAgentRunner agentRunner,
        string repoId, string baseBranch, string prompt, CancellationToken ct)
    {
        agentRunner = BindMemberRunner(agentRunner, TryResolveSelectedMember(agentRunner.Kind, project, item));
        var credential = await ResolveAgentCredentialAsync(agentRunner.Kind, project, item, ct);
        var access = _gitHost.GetSandboxAccess(repoId);

        var spec = BuildSandboxSpec(
            access,
            includeAgentCredential: credential,
            allowAgentNetwork: true,
            hostNetworkProfile: project.NetworkProfiles.Work,
            timingWorkItemId: item.Id,
            timingPhase: "check",
            flavor: SandboxProfileFlavor.Headless,
            extraEnvironment: null,
            baselineImageRef: SandboxTargetResolver.BaselineRefForTarget(
                project,
                new SandboxTarget(project.NetworkProfiles.Work, SandboxProfileFlavor.Headless),
                item.BaselineImageRef),
            credentialRunner: agentRunner);

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
        if (credential is not null && credential.Files.Count > 0)
            await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", baseBranch);

        var aggregator = new System.Text.StringBuilder();
        Action<string>? chunkCallback = chunk =>
        {
            aggregator.Append(chunk);
            _stdoutBroadcaster?.BroadcastChunk(item.Id, "check", chunk);
        };

        AuditLog.AgentStarted(agentRunner.Kind, sandbox.Id, "check");
        prompt = await ProcessAgentPromptAsync(
            item.Id,
            agentRunner.Kind,
            AgentPromptPhase.CheckAndAct,
            1,
            project,
            sandbox,
            prompt,
            ct);
        await using var supervision = await StartAgentSupervisionSessionAsync(
            item.Id,
            project,
            "check",
            1,
            agentRunner,
            item.AgentInstanceId,
            item.ModelId,
            item.ReasoningMode,
            sandbox,
            SandboxConventions.WorkDir,
            source: "check-and-act",
            ct);
        var involvementId = await RecordInvolvementStartAsync(
            item.Id,
            agentRunner.Kind,
            item.AgentInstanceId,
            item.ModelId,
            "check",
            iteration: null);
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var result = supervision is null
                ? await agentRunner.RunAsync(
                    sandbox, SandboxConventions.WorkDir, prompt, credential,
                    item.ModelId, item.ReasoningMode, ct,
                    stdoutChunkCallback: chunkCallback,
                    captureStructuredStream: false)
                : await AgentSupervisionTurnRunner.RunAutonomousAndQueuedInjectionsAsync(
                    agentRunner,
                    sandbox,
                    SandboxConventions.WorkDir,
                    prompt,
                    credential,
                    item.ModelId,
                    item.ReasoningMode,
                    supervision,
                    chunkCallback,
                    captureStructuredStream: false,
                    promptPreprocessor: (raw, pct) => ProcessAgentPromptAsync(
                        item.Id, agentRunner.Kind, AgentPromptPhase.CheckAndAct,
                        1, project, sandbox, raw, pct),
                    ct);
            var endedAt = DateTimeOffset.UtcNow;

            var aggregatedStdout = aggregator.ToString();
            if (!string.IsNullOrEmpty(result.Stdout) && !aggregatedStdout.EndsWith(result.Stdout, StringComparison.Ordinal))
            {
                aggregator.Append(result.Stdout);
                aggregatedStdout = aggregator.ToString();
            }

            await TryRecordCostAsync(aggregatedStdout, result.Stderr,
                agentRunner.Kind, item.AgentInstanceId, item.Id, "check", iteration: null,
                startedAt, endedAt, ResolveObservedModelId(agentRunner, item.ModelId));

            // Check-and-act stdout is parsed model output. Detect auth evidence so
            // the item fails as infrastructure instead of verdict-parse noise, but
            // force an in-VM corroboration attempt before publishing the fleet-wide
            // auth bench reason. A missing/inconclusive probe must not suppress the
            // fail-fast auth exclusion because smoke can be disabled during the exact
            // outage this detector is meant to catch.
            await ThrowIfAuthRequiredOutputAsync(
                item, project, agentRunner.Kind, "check", aggregatedStdout, result.Stderr,
                requireStdoutOnlyCorroboration: true,
                ct: ct);

            if (!result.Success)
            {
                await ThrowIfAuthErrorAgentFailureAsync(
                    item,
                    project,
                    agentRunner,
                    result,
                    "check",
                    classification: null,
                    ct);
                ThrowIfTransientAgentFailure(agentRunner, result, "check");
                ThrowIfInfrastructureAgentFailure(
                    agentRunner,
                    result,
                    "check",
                    $"Check-and-act agent {agentRunner.Kind} reported failure");
                var detail = BuildAgentFailureDetail("check-and-act agent failed", result, _opts.MaxFailureDetailBytes);
                throw new InvalidOperationException(detail);
            }

            await FinalizeInvolvementAsync(involvementId, AgentInvolvementOutcomes.Success);
            return aggregatedStdout;
        }
        catch (Exception ex)
        {
            await FinalizeInvolvementAsync(involvementId, OutcomeForFailure(ex));
            throw;
        }
    }

}
