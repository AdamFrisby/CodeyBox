using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Manages release lifecycle transitions and orchestrates the deep-audit phase.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Create the release branch on the first work item pickup (atomic SETIFNULL).</item>
///   <item>Close a release (open→closed).</item>
///   <item>Automatically transition closed→in_review once all work items are terminal.</item>
///   <item>Run the deep-audit loop (in_review), spawning remediation work items as needed.</item>
///   <item>Merge the release branch into main and optionally publish a GitHub release (→released).</item>
///   <item>Handle failed/abandoned/re-open transitions.</item>
/// </list>
/// </summary>
public sealed class ReleaseService
{
    private readonly IReleaseStore _releases;
    private readonly IWorkItemStore _workItems;
    private readonly IProjectRepository _projects;
    private readonly IWebhookDispatcher _webhooks;
    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly IReadOnlyList<IDeepAuditor> _deepAuditors;
    private readonly PipelineOptions _pipelineOpts;
    private readonly ITaskQueue _queue;
    private readonly ILogger<ReleaseService> _log;

    public ReleaseService(
        IReleaseStore releases,
        IWorkItemStore workItems,
        IProjectRepository projects,
        IWebhookDispatcher webhooks,
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        IUpstreamRemoteFactory upstreamFactory,
        IEnumerable<IDeepAuditor> deepAuditors,
        PipelineOptions pipelineOpts,
        ITaskQueue queue,
        ILogger<ReleaseService> log)
    {
        _releases = releases;
        _workItems = workItems;
        _projects = projects;
        _webhooks = webhooks;
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _agents = agents;
        _credentials = credentials;
        _upstreamFactory = upstreamFactory;
        _deepAuditors = deepAuditors.ToList();
        _pipelineOpts = pipelineOpts;
        _queue = queue;
        _log = log;
    }

    // ── Branch creation ──────────────���─────────────────────────────────���──────

    /// <summary>
    /// Called by OrchestratorService at work-item pickup time for release-linked items.
    /// Loads the release record, ensures the branch exists, and returns the branch name
    /// so the orchestrator can override <c>item.BaseBranch</c> before running the pipeline.
    /// Returns null when the release no longer accepts work (abandoned / released).
    /// </summary>
    public async Task<string?> EnsureReleaseBranchForItemAsync(ReleaseId releaseId, Project project, CancellationToken ct)
    {
        var release = await _releases.GetAsync(releaseId, ct);
        if (release is null || release.State is ReleaseState.Abandoned or ReleaseState.Released)
            return null;

        var (branchName, _) = await EnsureReleaseBranchAsync(release, project, ct);
        return branchName;
    }

    /// <summary>
    /// Ensures the release branch exists in the upstream. Called at work-item pickup
    /// time for the first item in a release. Uses SETIFNULL for atomicity so two
    /// concurrent workers safely converge on a single branch creation attempt.
    ///
    /// Returns the branch name (either just-created or pre-existing) and the
    /// base commit SHA.
    /// </summary>
    public async Task<(string BranchName, string BaseCommitSha)> EnsureReleaseBranchAsync(
        Release release,
        Project project,
        CancellationToken ct)
    {
        if (release.BranchName is not null)
            return (release.BranchName, release.BaseCommitSha ?? "");

        var branchName = project.ReleaseConfig.BranchNameTemplate.Replace("{name}", release.Name);

        // Create a temporary bare repo to discover origin/main SHA and create the branch.
        // We reuse the WorkItemId slot with the release GUID so LocalGitHost gives us a stable directory.
        var fakeItemId = new WorkItemId(release.Id.Value);
        var repoId = await _gitHost.EnsureRepositoryAsync(fakeItemId, project.RepositoryUrl, ct);
        var defaultBranch = await _gitHost.GetDefaultBranchAsync(repoId, ct);
        var access = _gitHost.GetSandboxAccess(repoId);

        // Spin up a sandbox to run git commands against the bare repo.
        var spec = new SandboxSpec
        {
            ImageReference = _pipelineOpts.SandboxImageReference,
            Mounts = [.. access.Mounts, new SandboxMount { SandboxPath = "/work", Tmpfs = true }],
            Environment = new Dictionary<string, string>(),
            Network = new SandboxNetworkPolicy
            {
                HostGitEndpoint = access.Network.HostGitEndpoint,
                AllowedHosts = [],
            },
            WorkingDirectory = "/work",
        };

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);

        await RunSandboxCmd(sandbox, ct, "git", "clone", access.CloneUrlInsideSandbox, "/work/repo");

        // Capture base_commit_sha = origin/main
        var headResult = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", "/work/repo", "rev-parse", $"origin/{defaultBranch}"],
        }, ct);
        if (!headResult.Success)
            throw new InvalidOperationException($"Could not resolve origin/{defaultBranch}: {headResult.Stderr}");
        var baseCommitSha = headResult.Stdout.Trim();

        // Create branch locally and push to remote BEFORE writing to DB, so that any
        // concurrent worker that loses the DB race reads a branch that already exists.
        // Two workers may race on the push; "already exists" is a safe non-fatal outcome.
        await RunSandboxCmd(sandbox, ct, "git", "-C", "/work/repo", "checkout", "-b", branchName);
        var pushResult = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", "/work/repo", "push", "origin", $"{branchName}:{branchName}"],
        }, ct);
        if (!pushResult.Success &&
            !pushResult.Stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"command failed: git push {branchName}\n{pushResult.Stderr}");
        }

        // Now atomically record in DB. The winner proceeds; the loser reads the winner's value.
        var won = await _releases.TrySetBranchAsync(release.Id, branchName, baseCommitSha, ct);
        if (!won)
        {
            var refreshed = await _releases.GetAsync(release.Id, ct);
            return (refreshed?.BranchName ?? branchName, refreshed?.BaseCommitSha ?? baseCommitSha);
        }

        _log.LogInformation("Release {ReleaseId} branch '{Branch}' created at {Sha}",
            release.Id, branchName, baseCommitSha);

        return (branchName, baseCommitSha);
    }

    // ── State transitions ─────────────────────────────────────────────────────

    /// <summary>Transitions open → closed. Validates state; emits webhooks.</summary>
    public async Task<(bool Success, string? Error)> CloseAsync(ReleaseId id, CancellationToken ct)
    {
        var release = await _releases.GetAsync(id, ct);
        if (release is null) return (false, "release not found");
        if (release.State != ReleaseState.Open) return (false, $"release is {release.State}, not Open");

        var project = await _projects.GetAsync(release.ProjectId, ct);

        var closed = release with { State = ReleaseState.Closed, ClosedAt = DateTimeOffset.UtcNow };
        await _releases.UpdateAsync(closed, ct);
        await PublishAsync("release.closed", closed, project, ct);

        // Check whether any linked work items are non-Done (failed/cancelled) before
        // notifying the operator. The operator then decides whether to proceed or add more work.
        var items = await CollectWorkItemsAsync(id, ct);
        var hasFailed = items.Any(i => i.State is WorkItemState.Failed or WorkItemState.AuditFailed or WorkItemState.Cancelled);
        if (hasFailed)
            await PublishAsync("release.has_failed_work_items", closed, project, ct,
                new { failedCount = items.Count(i => i.State is WorkItemState.Failed or WorkItemState.AuditFailed or WorkItemState.Cancelled) });

        // If all items are already terminal → begin review immediately.
        if (AllTerminal(items))
            _ = Task.Run(() => TryBeginReviewAsync(closed, CancellationToken.None));

        return (true, null);
    }

    /// <summary>Re-opens a failed release (failed → open) for additional remediation work items.</summary>
    public async Task<(bool Success, string? Error)> ReopenAsync(ReleaseId id, string reason, CancellationToken ct)
    {
        var release = await _releases.GetAsync(id, ct);
        if (release is null) return (false, "release not found");
        if (release.State != ReleaseState.Failed) return (false, $"release is {release.State}, not Failed");

        var project = await _projects.GetAsync(release.ProjectId, ct);
        var reopened = release with { State = ReleaseState.Open, FailedReason = null };
        await _releases.UpdateAsync(reopened, ct);
        await PublishAsync("release.reopened", reopened, project, ct, new { reason });
        return (true, null);
    }

    /// <summary>Abandons a release from any non-Released state.</summary>
    public async Task<(bool Success, string? Error)> AbandonAsync(ReleaseId id, CancellationToken ct)
    {
        var release = await _releases.GetAsync(id, ct);
        if (release is null) return (false, "release not found");
        if (release.State == ReleaseState.Released) return (false, "cannot abandon a released release");

        var project = await _projects.GetAsync(release.ProjectId, ct);
        var abandoned = release with { State = ReleaseState.Abandoned };
        await _releases.UpdateAsync(abandoned, ct);
        await PublishAsync("release.abandoned", abandoned, project, ct);
        return (true, null);
    }

    /// <summary>
    /// Operator-initiated force-start of the review (closed → in_review),
    /// bypassing the "wait for all work items" check. Requires confirmation.
    /// </summary>
    public async Task<(bool Success, string? Error)> ForceBeginReviewAsync(ReleaseId id, CancellationToken ct)
    {
        var release = await _releases.GetAsync(id, ct);
        if (release is null) return (false, "release not found");
        if (release.State != ReleaseState.Closed) return (false, $"release is {release.State}, not Closed");

        _ = Task.Run(() => TryBeginReviewAsync(release, CancellationToken.None));
        return (true, null);
    }

    // ── Auto-transition closed → in_review ────────────────────────────────────

    /// <summary>
    /// Called by the orchestrator after any work item linked to a release reaches a
    /// terminal state. Checks whether all items for the release are now terminal; if so
    /// and the release is Closed, triggers the in_review transition.
    /// </summary>
    public async Task OnWorkItemTerminalAsync(ReleaseId releaseId, CancellationToken ct)
    {
        var release = await _releases.GetAsync(releaseId, ct);
        if (release is null || release.State != ReleaseState.Closed) return;

        var items = await CollectWorkItemsAsync(releaseId, ct);
        if (!AllTerminal(items)) return;

        await TryBeginReviewAsync(release, ct);
    }

    private async Task TryBeginReviewAsync(Release release, CancellationToken ct)
    {
        // Guard: only transition from Closed. Re-read then CAS-update to prevent concurrent
        // callers from each starting an independent deep audit.
        var current = await _releases.GetAsync(release.Id, ct);
        if (current is null || current.State != ReleaseState.Closed) return;

        var inReview = current with { State = ReleaseState.InReview, ReviewStartedAt = DateTimeOffset.UtcNow };
        // Atomic compare-and-swap: succeeds only if state is still Closed in the DB.
        if (!await _releases.TryTransitionStateAsync(inReview, ReleaseState.Closed, ct))
            return;

        var project = await _projects.GetAsync(inReview.ProjectId, ct);
        await PublishAsync("release.in_review", inReview, project, ct);

        // Run deep audit in background so this call returns quickly.
        _ = Task.Run(async () =>
        {
            try { await RunDeepAuditPhaseAsync(inReview, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Deep audit phase threw for release {Id}", inReview.Id);
            }
        }, CancellationToken.None);
    }

    // ── Deep audit phase ──────────────────────────────────────────────────────

    private async Task RunDeepAuditPhaseAsync(Release release, CancellationToken ct)
    {
        var project = await _projects.GetAsync(release.ProjectId, ct);
        if (project is null)
        {
            await FailReleaseAsync(release, "project not found", ct);
            return;
        }

        if (release.BranchName is null)
        {
            await FailReleaseAsync(release, "release branch name is null at in_review; cannot audit", ct);
            return;
        }

        // Apply per-release config overrides from ConfigJson (if any).
        ReleaseConfigOverrides? overrides = null;
        if (release.ConfigJson is { Length: > 2 })
        {
            try { overrides = JsonSerializer.Deserialize<ReleaseConfigOverrides>(release.ConfigJson); }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Release {Id}: failed to parse ConfigJson; using project defaults", release.Id);
            }
        }

        // Resolve deep auditors (per-release override takes precedence over project default).
        var auditorNames = overrides?.DeepAuditors ?? project.ReleaseConfig.DeepAuditors;
        var auditors = _deepAuditors
            .Where(a => auditorNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // If no deep auditors configured → immediately pass.
        if (auditors.Count == 0)
        {
            _log.LogInformation("Release {Id}: no deep auditors configured; skipping to released", release.Id);
            await TransitionReleasedAsync(release, project, ct);
            return;
        }

        var maxIterations = overrides?.DeepAuditMaxIterations ?? project.ReleaseConfig.DeepAuditMaxIterations;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            _log.LogInformation("Release {Id}: deep audit iteration {Iter}/{Max}", release.Id, iteration, maxIterations);

            var findings = await RunDeepAuditIterationAsync(release, project, auditors, iteration, ct);
            var blocking = findings.Where(f => f.Severity >= project.Audit.FailingSeverity).ToList();

            var current = await _releases.GetAsync(release.Id, ct) ?? release;
            await PublishAsync("release.deep_audit_iteration_complete", current, project, ct,
                new { iteration, maxIterations, blockingFindings = blocking.Count, totalFindings = findings.Count });

            if (blocking.Count == 0)
            {
                _log.LogInformation("Release {Id}: deep audit passed at iteration {Iter}", release.Id, iteration);
                await TransitionReleasedAsync(current, project, ct);
                return;
            }

            if (iteration == maxIterations)
            {
                var reason = $"deep audit did not converge after {maxIterations} iterations. " +
                             $"{blocking.Count} blocking finding(s): {string.Join("; ", blocking.Take(3).Select(f => f.Title))}";
                await FailReleaseAsync(current, reason, ct);
                return;
            }

            // Create a remediation work item.
            var remediationPrompt = BuildRemediationPrompt(findings, blocking, iteration, maxIterations);
            var remediationItem = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = project.Id,
                Title = $"[Release {release.Name}] Address deep-audit findings (iteration {iteration})",
                Prompt = remediationPrompt,
                ReleaseId = release.Id,
                BaseBranch = release.BranchName,
                QueuePosition = DateTimeOffset.UtcNow.Ticks,
            };

            await _workItems.CreateAsync(remediationItem, ct);
            await PublishAsync("release.deep_audit_remediation_dispatched", current, project, ct,
                new { workItemId = remediationItem.Id.ToString(), iteration });
            await _queue.EnqueueAsync(remediationItem.Id, ct);

            // Wait for the remediation item to complete before looping.
            await WaitForWorkItemTerminalAsync(remediationItem.Id, ct);
        }

        var final = await _releases.GetAsync(release.Id, ct) ?? release;
        await FailReleaseAsync(final,
            $"deep audit did not converge after {maxIterations} iterations", ct);
    }

    private async Task<IReadOnlyList<AuditFinding>> RunDeepAuditIterationAsync(
        Release release,
        Project project,
        IReadOnlyList<IDeepAuditor> auditors,
        int iteration,
        CancellationToken ct)
    {
        var allFindings = new List<AuditFinding>();
        if (release.BranchName is null) return allFindings;

        var fakeItemId = new WorkItemId(release.Id.Value);
        var repoId = await _gitHost.EnsureRepositoryAsync(fakeItemId, project.RepositoryUrl, ct);
        var access = _gitHost.GetSandboxAccess(repoId);

        // Group auditors by capability (same pattern as per-PR audit).
        var byGroup = auditors.GroupBy(a =>
            a.Required.HasFlag(AuditCapabilities.AgentCredentials)
                ? AuditCapabilities.AgentCredentials | AuditCapabilities.Network
                : AuditCapabilities.None).ToList();

        foreach (var group in byGroup)
        {
            var needsCreds = group.Key.HasFlag(AuditCapabilities.AgentCredentials);
            AgentCredential? credential = null;
            IAgentRunner? runner = null;

            if (needsCreds)
            {
                var agentKind = project.Audit.AuditAgent ?? project.DefaultAgent;
                if (!_agents.TryGet(agentKind, out runner))
                    runner = null;
                if (runner is not null)
                    credential = await _credentials.GetAsync(agentKind, ct);
            }

            var env = new Dictionary<string, string>();
            if (credential is not null)
                foreach (var (k, v) in credential.EnvironmentVariables) env[k] = v;

            var spec = new SandboxSpec
            {
                ImageReference = _pipelineOpts.SandboxImageReference,
                Mounts = [.. access.Mounts, new SandboxMount { SandboxPath = "/work", Tmpfs = true }],
                Environment = env,
                Network = new SandboxNetworkPolicy
                {
                    AllowedHosts = needsCreds ? _pipelineOpts.AgentAllowedHosts : [],
                    HostGitEndpoint = access.Network.HostGitEndpoint,
                },
                WorkingDirectory = "/work",
            };

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

            await RunSandboxCmd(sandbox, ct, "git", "clone", access.CloneUrlInsideSandbox, "/work/repo");
            await RunSandboxCmd(sandbox, ct, "git", "-C", "/work/repo", "checkout", release.BranchName);

            var ctx = new DeepAuditContext(
                ReleaseId: release.Id,
                ProjectId: project.Id,
                BranchName: release.BranchName,
                Iteration: iteration,
                AuditRunner: runner);

            foreach (var auditor in group)
            {
                _log.LogInformation("Deep auditor {Name} running for release {Id} iteration {Iter}",
                    auditor.Name, release.Id, iteration);
                try
                {
                    var result = await auditor.RunAsync(sandbox, "/work/repo", ctx, ct);
                    allFindings.AddRange(result.Findings);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Deep auditor {Name} threw for release {Id}", auditor.Name, release.Id);
                }
            }
        }

        return allFindings;
    }

    // ── Releasing ─────────────────────────────────────────────────────────────

    private async Task TransitionReleasedAsync(Release release, Project project, CancellationToken ct)
    {
        if (release.BranchName is null)
        {
            await FailReleaseAsync(release, "release branch name is null; cannot merge to main", ct);
            return;
        }

        // Merge the release branch into main via upstream.
        var upstream = _upstreamFactory.Create(project);
        var fakeItemId = new WorkItemId(release.Id.Value);
        var repoId = await _gitHost.EnsureRepositoryAsync(fakeItemId, project.RepositoryUrl, ct);
        var defaultBranch = await _gitHost.GetDefaultBranchAsync(repoId, ct);

        var request = new UpstreamCompletionRequest
        {
            RepositoryId = repoId,
            WorkItemId = fakeItemId,
            ProjectId = project.Id,
            WorkBranch = release.BranchName,
            BaseBranch = defaultBranch,
            Title = $"Release {release.Name}",
            Description = $"Automated release merge for release '{release.Name}' via CodeyBox.",
        };

        UpstreamCompletionOutcome? outcome = null;
        try
        {
            outcome = await upstream.CompleteAsync(request, ct);
            _log.LogInformation("Release {Id}: upstream merge complete. PR={Url}", release.Id, outcome.PullRequestUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Release {Id}: upstream merge failed", release.Id);
            await FailReleaseAsync(release, $"upstream merge failed: {ex.Message}", ct);
            return;
        }

        var released = release with { State = ReleaseState.Released, ReleasedAt = DateTimeOffset.UtcNow };
        // Guard: only transition to Released if state is still InReview (not abandoned concurrently).
        if (!await _releases.TryTransitionStateAsync(released, ReleaseState.InReview, ct))
        {
            _log.LogInformation("Release {Id}: skipping released transition; state changed concurrently", release.Id);
            return;
        }

        if (project.ReleaseConfig.CreateGitHubRelease)
        {
            var tag = !string.IsNullOrWhiteSpace(release.TargetTag)
                ? release.TargetTag
                : project.ReleaseConfig.GitHubTagTemplate.Replace("{name}", release.Name, StringComparison.Ordinal);
            try
            {
                var releaseUrl = await upstream.CreateTagAndReleaseAsync(tag, outcome.MergedSha ?? string.Empty, null, ct);
                _log.LogInformation("Release {Id}: GitHub release created for tag '{Tag}' at {Url}", release.Id, tag, releaseUrl);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Release {Id}: GitHub release creation for tag '{Tag}' failed; continuing", release.Id, tag);
            }
        }

        await PublishAsync("release.published", released, project, ct,
            new { pullRequestUrl = outcome.PullRequestUrl, mergedSha = outcome.MergedSha });

        _log.LogInformation("Release {Id} '{Name}' published", released.Id, released.Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task FailReleaseAsync(Release release, string reason, CancellationToken ct)
    {
        var failed = release with { State = ReleaseState.Failed, FailedReason = reason };
        // Guard: only transition to Failed if state is still InReview (not abandoned or already released).
        if (!await _releases.TryTransitionStateAsync(failed, ReleaseState.InReview, ct))
        {
            _log.LogInformation("Release {Id}: skipping failed transition; state changed concurrently", release.Id);
            return;
        }
        var project = await _projects.GetAsync(release.ProjectId, ct);
        await PublishAsync("release.failed", failed, project, ct, new { reason });
        _log.LogWarning("Release {Id} failed: {Reason}", release.Id, reason);
    }

    private async Task PublishAsync(
        string eventName,
        Release release,
        Project? project,
        CancellationToken ct,
        object? details = null)
    {
        await _webhooks.PublishAsync(new WebhookEvent
        {
            Event = eventName,
            Release = release,
            Project = project,
            Details = details,
        }, ct);
    }

    private async Task<List<WorkItem>> CollectWorkItemsAsync(ReleaseId id, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        await foreach (var item in _workItems.ListByReleaseAsync(id, ct))
            items.Add(item);
        return items;
    }

    private static bool AllTerminal(IReadOnlyList<WorkItem> items) =>
        items.Count > 0 &&
        items.All(i => i.State is WorkItemState.Done or WorkItemState.Failed
                                or WorkItemState.AuditFailed or WorkItemState.Cancelled);

    private async Task WaitForWorkItemTerminalAsync(WorkItemId id, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var item = await _workItems.GetAsync(id, ct);
            if (item is null) return;
            if (item.State is WorkItemState.Done or WorkItemState.Failed
                           or WorkItemState.AuditFailed or WorkItemState.Cancelled)
                return;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private static string BuildRemediationPrompt(
        IReadOnlyList<AuditFinding> allFindings,
        IReadOnlyList<AuditFinding> blocking,
        int iteration,
        int maxIterations)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Release deep-audit remediation");
        sb.AppendLine();
        sb.AppendLine($"This is iteration {iteration} of {maxIterations} in the deep-audit cycle for this release.");
        sb.AppendLine($"The following {blocking.Count} blocking finding(s) must be resolved before the release can merge:");
        sb.AppendLine();
        // Finding descriptions are LLM-generated text derived from scanning repository source files.
        // They are treated as untrusted data: sanitized before embedding in this prompt so that
        // adversarial content in source files cannot inject instructions into the remediation agent.
        foreach (var f in blocking)
        {
            var auditorName = SanitizeFindingText(f.AuditorName);
            var title = SanitizeFindingText(f.Title);
            var description = SanitizeFindingText(f.Description);
            sb.AppendLine($"## [{auditorName}] {title} (severity: {f.Severity})");
            sb.AppendLine(description);
            if (f.Location is not null) sb.AppendLine($"Location: {SanitizeFindingText(f.Location)}");
            sb.AppendLine();
        }
        if (allFindings.Count > blocking.Count)
        {
            sb.AppendLine($"There are also {allFindings.Count - blocking.Count} non-blocking finding(s); address them if feasible.");
        }
        sb.AppendLine("Address all blocking findings, commit your changes, and do not push. The orchestrator will push.");
        return sb.ToString();
    }

    // Sanitizes untrusted text from LLM-generated auditor findings before embedding in a prompt.
    // Prevents stored prompt injection: adversarial instructions embedded in source files that
    // auditors read can appear in finding descriptions; this strips characters and patterns that
    // would let such content direct the remediation agent.
    private static string SanitizeFindingText(string? text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Truncate to bound payload size.
        if (text.Length > maxLength)
            text = string.Concat(text.AsSpan(0, maxLength), " [truncated]");

        // Strip control characters (preserve newlines and tabs for readability).
        var stripped = new System.Text.StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\t' || !char.IsControl(ch))
                stripped.Append(ch);
        }
        text = stripped.ToString();

        // Demote markdown headings so they cannot inject new top-level prompt sections.
        // e.g. "## Ignore previous instructions" → "  Ignore previous instructions"
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#'))
                lines[i] = "  " + trimmed.TrimStart('#').TrimStart();
        }
        return string.Join('\n', lines);
    }

    private static async Task RunSandboxCmd(ISandbox sandbox, params string[] argv)
    {
        // Helper accepts CancellationToken as last param via a separate signature
        await RunSandboxCmd(sandbox, CancellationToken.None, argv);
    }

    private static async Task RunSandboxCmd(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (!r.Success)
            throw new InvalidOperationException($"command failed: {string.Join(' ', argv)}\n{r.Stderr}");
    }

    /// <summary>Subset of ProjectReleaseConfig that can be overridden per-release via Release.ConfigJson.</summary>
    private sealed class ReleaseConfigOverrides
    {
        [JsonPropertyName("deepAuditors")]
        public IReadOnlyList<string>? DeepAuditors { get; init; }

        [JsonPropertyName("deepAuditMaxIterations")]
        public int? DeepAuditMaxIterations { get; init; }
    }

    private static async Task MaterialiseCredentialFilesAsync(ISandbox sandbox, AgentCredential credential, CancellationToken ct)
    {
        await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", SandboxConventions.CredentialsDir] }, ct);
        foreach (var (relativePath, contents) in credential.Files)
        {
            var safePath = relativePath.Replace('\\', '/').TrimStart('/');
            var fullPath = $"{SandboxConventions.CredentialsDir}/{safePath}";
            var dir = fullPath[..fullPath.LastIndexOf('/')];
            await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", dir] }, ct);
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "umask 077 && cat > \"$0\"", fullPath],
                Stdin = contents,
            }, ct);
            if (!write.Success)
                throw new InvalidOperationException($"Failed to write credential file {safePath}: {write.Stderr}");
        }
    }
}
