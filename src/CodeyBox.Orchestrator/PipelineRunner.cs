using Microsoft.Extensions.Logging;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-work-item pipeline:
///
///   Work phase  →  Audit + rework loop  →  Merge phase (agent-driven)  →  Upstream push
///
/// The work, audit, and merge phases together are the atomic unit:
/// failure of any of them marks the item Failed (or AuditFailed for the
/// specific case of audit not converging). UpstreamPush runs after success
/// and is retried independently.
///
/// Project-scoped: each work item is bound to a <see cref="Project"/> via
/// <see cref="WorkItem.ProjectId"/>. The pipeline resolves the project at
/// the start of each run and uses its config (repository, default agent,
/// auditor list, upstream remote) for every phase. Different projects
/// running concurrently never share creds — per-project upstream tokens
/// are read fresh from the env, never persisted between runs.
///
/// Merge phase invokes the work item's agent (Claude / Codex / etc.) so
/// non-trivial conflicts can be resolved instead of failing the merge.
/// The orchestrator verifies the agent's output before pushing — see
/// <see cref="VerifyMergeStateAsync"/>.
/// </summary>
public sealed class PipelineRunner
{
    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly IPullRequestService _prs;
    private readonly IProjectRepository _projects;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly ProjectAuditorComposer _auditorComposer;
    private readonly IWorkItemStore _store;
    private readonly PipelineOptions _opts;
    private readonly ILogger<PipelineRunner> _log;

    public PipelineRunner(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        IPullRequestService prs,
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        ProjectAuditorComposer auditorComposer,
        IWorkItemStore store,
        PipelineOptions opts,
        ILogger<PipelineRunner> log)
    {
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _agents = agents;
        _credentials = credentials;
        _prs = prs;
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _auditorComposer = auditorComposer;
        _store = store;
        _opts = opts;
        _log = log;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        Project project;
        try
        {
            project = await _projects.GetAsync(item.ProjectId, ct)
                ?? throw new InvalidOperationException($"Unknown project '{item.ProjectId}'");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} could not resolve project", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None);
            return;
        }

        var agentKind = item.Agent ?? project.DefaultAgent;
        if (!_agents.TryGet(agentKind, out var agentRunner))
        {
            await TransitionFailed(item, $"No runner registered for agent '{agentKind}'", CancellationToken.None);
            return;
        }

        try
        {
            var repoId = await _gitHost.EnsureRepositoryAsync(item.Id, project.RepositoryUrl, ct);
            var baseBranch = item.BaseBranch ?? project.DefaultBaseBranch ?? await _gitHost.GetDefaultBranchAsync(repoId, ct);
            var workBranch = item.WorkBranch ?? $"codeybox/{item.Id.ToString()[..8]}";
            if (string.Equals(workBranch, baseBranch, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"workBranch must differ from baseBranch (both '{baseBranch}'); refusing to bypass merge-phase containment");

            // The retry endpoint sets the entry state to a pre-phase marker
            // (Queued / WorkComplete / AuditPassed / Merged) so we resume at
            // the matching phase. Read once at the top so we don't re-fetch
            // mid-pipeline (TransitionFailed/restart-recovery already handle
            // mid-phase failures).
            var entry = item.State;
            var skipWork = entry is WorkItemState.WorkComplete or WorkItemState.AuditPassed or WorkItemState.Merged;
            var skipAudit = entry is WorkItemState.AuditPassed or WorkItemState.Merged;
            var skipMerge = entry is WorkItemState.Merged;

            // -------- Phase 1: Work --------
            if (!skipWork)
            {
                await Transition(item, WorkItemState.Working, ct);
                using (var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    workCts.CancelAfter(item.WorkTimeout);
                    await RunAgentPhaseAsync(item, agentRunner, repoId, baseBranch, workBranch,
                        item.Prompt, isInitial: true,
                        networkProfile: project.NetworkProfiles.Work,
                        workCts.Token);
                }
                await Transition(item, WorkItemState.WorkComplete, ct);
            }

            // -------- Phase 1.5: Audit + rework loop --------
            var auditors = _auditorComposer.Compose(project, agentRunner);
            if (auditors.Count > 0 && !skipAudit)
            {
                await RunAuditLoopAsync(item, project, agentRunner, auditors, repoId, baseBranch, workBranch, ct);
                await Transition(item, WorkItemState.AuditPassed, ct);
            }

            // Open PR record (local metadata) AFTER the audit converges.
            // Skip if we're resuming past merge — merge is the only consumer.
            PullRequest? pr = null;
            if (!skipMerge)
            {
                pr = await _prs.OpenAsync(new OpenPullRequest(
                    RepositoryId: repoId,
                    SourceBranch: workBranch,
                    TargetBranch: baseBranch,
                    Title: item.Title,
                    Description: $"Work item {item.Id} via {agentKind.Value} (project {project.Id})"), ct);
            }

            // -------- Phase 2: Merge (agent-driven) --------
            string? mergeSha = null;
            if (!skipMerge)
            {
                await Transition(item, WorkItemState.Merging, ct);
                string localMergeSha;
                using (var mergeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    mergeCts.CancelAfter(item.MergeTimeout);
                    localMergeSha = await RunAgentMergePhaseAsync(item, agentRunner, repoId, baseBranch, workBranch,
                        networkProfile: project.NetworkProfiles.Merge,
                        mergeCts.Token);
                }
                mergeSha = localMergeSha;
                await _prs.MarkMergedAsync(pr!.Id, localMergeSha, ct);
                await Transition(item, WorkItemState.Merged, ct);
            }

            // -------- Phase 3: Upstream push (separate atomic unit) --------
            var upstream = _upstreamFactory.Create(project);
            if (item.PushUpstream && project.Upstream.Kind != "noop")
            {
                await RunUpstreamPushPhaseAsync(item, upstream, repoId, baseBranch, workBranch, mergeSha, ct);
            }
            else
            {
                await Transition(item, WorkItemState.Done, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            if (current.State is not WorkItemState.Done and not WorkItemState.Failed)
                await _store.UpdateAsync(current.With(WorkItemState.Cancelled, "cancelled"), CancellationToken.None);
            throw;
        }
        catch (AuditFailedException ex)
        {
            _log.LogWarning("Work item {Id} audit failed: {Error}", item.Id, ex.Message);
            var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
            await _store.UpdateAsync(current.With(WorkItemState.AuditFailed, ex.Message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Work item {Id} failed", item.Id);
            await TransitionFailed(item, ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs the agent in a sandbox against <paramref name="branch"/>. On the
    /// first call (work phase), <paramref name="isInitial"/> is true and the
    /// branch is created from <paramref name="baseBranch"/>. On rework calls
    /// the branch is checked out as-is (with the work-phase commits already
    /// on it) and the agent stacks new commits on top.
    /// </summary>
    private async Task RunAgentPhaseAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string branch,
        string prompt,
        bool isInitial,
        string? networkProfile,
        CancellationToken ct)
    {
        var credential = await _credentials.GetAsync(runner.Kind, ct);
        var access = _gitHost.GetSandboxAccess(repoId);
        var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: true, hostNetworkProfile: networkProfile);

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);

        if (credential is not null && credential.Files.Count > 0)
            await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        if (isInitial)
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch);
        else
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", branch);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "codeybox@local");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "CodeyBox");

        // Capture HEAD before the agent runs. The rework prompt explicitly
        // asks the agent to make new commits, so the agent may move HEAD
        // itself. We compare before/after to distinguish "agent committed"
        // from "agent did nothing" — both end with a clean working tree
        // but only the former is success.
        var beforeHead = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!beforeHead.Success)
            throw new InvalidOperationException($"Failed to read HEAD before agent: {beforeHead.Stderr}");
        var shaBefore = beforeHead.Stdout.Trim();

        var agentResult = await runner.RunAsync(sandbox, SandboxConventions.WorkDir, prompt, credential, ct);
        // Always log a truncated tail of agent output, regardless of
        // success. This is critical when an agent finishes "successfully"
        // but produces no useful diff — without this log, we have no
        // visibility into what the agent reasoned.
        LogAgentOutput(_log, runner.Kind, agentResult);
        if (!agentResult.Success)
        {
            var detail = string.Join("\n",
                new[] {
                    $"Agent {runner.Kind} reported failure: {agentResult.Summary}",
                    !string.IsNullOrEmpty(agentResult.Stderr) ? $"stderr:\n{agentResult.Stderr}" : null,
                    !string.IsNullOrEmpty(agentResult.Stdout) ? $"stdout:\n{agentResult.Stdout}" : null,
                }.Where(s => s is not null));
            throw new InvalidOperationException(detail);
        }

        // Stage anything the agent left dirty in the working tree. If the
        // agent already committed (per the rework prompt's instruction
        // to make new commits), `git add -A` is a no-op.
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "add", "-A");
        var staged = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"],
        }, ct);
        // diff --cached --quiet exits 0 on no-diff, 1 on diff.
        var hasStagedDiff = staged.ExitCode != 0;

        if (hasStagedDiff)
        {
            var commitMessage = isInitial
                ? $"codeybox: {item.Title}"
                : "codeybox rework: address audit findings";
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
        }

        // Did HEAD advance — either via the agent committing itself or
        // via our just-now commit?
        var afterHead = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!afterHead.Success)
            throw new InvalidOperationException($"Failed to read HEAD after agent: {afterHead.Stderr}");
        var shaAfter = afterHead.Stdout.Trim();
        if (string.Equals(shaBefore, shaAfter, StringComparison.Ordinal))
        {
            var msg = isInitial
                ? "Agent produced no changes to commit"
                : "Rework agent produced no changes; cannot resolve audit findings";
            throw new InvalidOperationException(msg);
        }

        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"{branch}:{branch}");
    }

    /// <summary>
    /// Logs a truncated tail of agent stdout/stderr at Information level.
    /// Truncated because agent output can be tens of KB; the tail is
    /// usually where the conclusion / "I'm done" / refusal message lives.
    /// </summary>
    private static void LogAgentOutput(ILogger log, AgentKind kind, AgentResult result)
    {
        const int tailBytes = 2000;
        static string Tail(string? s) =>
            string.IsNullOrEmpty(s) ? "(empty)" :
            s.Length <= tailBytes ? s : "…" + s[^tailBytes..];
        log.LogInformation(
            "Agent {Kind} finished: success={Success} exit={Summary}\nstdout-tail:\n{StdoutTail}\nstderr-tail:\n{StderrTail}",
            kind.Value, result.Success, result.Summary, Tail(result.Stdout), Tail(result.Stderr));
    }

    private async Task RunAuditLoopAsync(
        WorkItem item,
        Project project,
        IAgentRunner runner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        string baseBranch,
        string workBranch,
        CancellationToken ct)
    {
        for (var iteration = 1; iteration <= project.Audit.MaxIterations; iteration++)
        {
            await Transition(item, WorkItemState.Auditing, ct);
            using var auditCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            auditCts.CancelAfter(project.Audit.PerIterationTimeout);

            var ctx = new AuditContext(item.Id, workBranch, baseBranch, iteration, item.Prompt);
            var findings = await CollectFindingsAsync(project, runner, auditors, repoId, ctx, auditCts.Token);

            var blocking = findings.Where(f => f.Severity >= project.Audit.FailingSeverity).ToList();
            if (blocking.Count == 0)
            {
                _log.LogInformation("Audit iteration {Iter} passed for {Id} ({NonBlocking} non-blocking findings)",
                    iteration, item.Id, findings.Count);
                return;
            }

            _log.LogInformation("Audit iteration {Iter} of {Max} found {Count} blocking findings for {Id}",
                iteration, project.Audit.MaxIterations, blocking.Count, item.Id);

            if (iteration == project.Audit.MaxIterations)
            {
                var summary = string.Join("; ", blocking.Take(5).Select(f => $"[{f.AuditorName}] {f.Title}"));
                throw new AuditFailedException(
                    $"Audit did not pass after {iteration} iterations. {blocking.Count} blocking finding(s): {summary}");
            }

            await Transition(item, WorkItemState.Reworking, ct);
            var reworkPrompt = ReworkPromptBuilder.Build(item.Prompt, findings, iteration, project.Audit.MaxIterations);
            using var reworkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reworkCts.CancelAfter(item.WorkTimeout);
            await RunAgentPhaseAsync(item, runner, repoId, baseBranch, workBranch,
                reworkPrompt, isInitial: false,
                networkProfile: project.NetworkProfiles.Rework,
                reworkCts.Token);
        }
    }

    private async Task<IReadOnlyList<AuditFinding>> CollectFindingsAsync(
        Project project,
        IAgentRunner runner,
        IReadOnlyList<IAuditor> auditors,
        string repoId,
        AuditContext ctx,
        CancellationToken ct)
    {
        var findings = new List<AuditFinding>();
        var byCaps = auditors.GroupBy(a => a.Required).ToList();

        foreach (var group in byCaps)
        {
            var needsCreds = group.Key.HasFlag(AuditCapabilities.AgentCredentials);
            var needsNetwork = group.Key.HasFlag(AuditCapabilities.Network);

            AgentCredential? credential = needsCreds ? await _credentials.GetAsync(runner.Kind, ct) : null;
            var access = _gitHost.GetSandboxAccess(repoId);
            // Tool-only auditors get the project's "audit-tool" profile
            // (typically isolated/no-egress); LLM-driven auditors get the
            // "audit-agent" profile (typically same as the work profile).
            var profile = needsCreds ? project.NetworkProfiles.AuditAgent : project.NetworkProfiles.AuditTool;
            var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: needsNetwork, hostNetworkProfile: profile);
            spec = spec with { Mounts = [.. spec.Mounts, new SandboxMount { SandboxPath = "/audit", Tmpfs = true, SizeBytes = 1024 * 1024 }] };

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

            await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", ctx.WorkBranch);

            foreach (var auditor in group)
            {
                _log.LogInformation("Running auditor {Name} (iteration {Iter})", auditor.Name, ctx.Iteration);
                var result = await auditor.RunAsync(sandbox, SandboxConventions.WorkDir, ctx, ct);
                findings.AddRange(result.Findings);
                if (project.Audit.StopOnFirstFailure && result.Findings.Any(f => f.Severity >= project.Audit.FailingSeverity))
                    return findings;
            }
        }

        return findings;
    }

    /// <summary>
    /// Merge phase: invoke the work-item's agent inside a sandbox to perform
    /// the merge, including conflict resolution. The agent does not push;
    /// the orchestrator verifies the merge state and pushes itself.
    ///
    /// Security note: the merge sandbox NOW carries agent credentials (so
    /// the agent can call its API to reason about conflicts). This widens
    /// the attack surface compared to the previous deterministic merge —
    /// see docs/security-audit.md (Finding U). The mitigation is the same
    /// network policy as the work sandbox: only the agent's API endpoint
    /// is reachable.
    /// </summary>
    private async Task<string> RunAgentMergePhaseAsync(
        WorkItem item,
        IAgentRunner runner,
        string repoId,
        string baseBranch,
        string workBranch,
        string? networkProfile,
        CancellationToken ct)
    {
        var credential = await _credentials.GetAsync(runner.Kind, ct);
        var access = _gitHost.GetSandboxAccess(repoId);
        var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: true, hostNetworkProfile: networkProfile);
        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
        if (credential is not null && credential.Files.Count > 0)
            await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "codeybox@local");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "CodeyBox");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", baseBranch);

        var preMerge = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!preMerge.Success) throw new InvalidOperationException($"pre-merge rev-parse failed: {preMerge.Stderr}");
        var preMergeSha = preMerge.Stdout.Trim();

        var prompt = BuildMergePrompt(baseBranch, workBranch);
        var agentResult = await runner.RunAsync(sandbox, SandboxConventions.WorkDir, prompt, credential, ct);
        if (!agentResult.Success)
            throw new InvalidOperationException($"Merge agent {runner.Kind} reported failure: {agentResult.Summary}\n{agentResult.Stderr}");

        var mergeSha = await VerifyMergeStateAsync(sandbox, baseBranch, workBranch, preMergeSha, ct);

        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"{baseBranch}:{baseBranch}");
        return mergeSha;
    }

    /// <summary>
    /// Sanity-check the agent's merge before letting the orchestrator push:
    ///   - working tree is clean (no unmerged paths, no leftover &lt;&lt;&lt;&lt;&lt;&lt;&lt; markers)
    ///   - HEAD advanced past the pre-merge sha
    ///   - the work branch is now reachable from HEAD (i.e. it actually merged)
    ///   - HEAD is on baseBranch (agent didn't sneak onto a different branch)
    /// Throws on any violation.
    /// </summary>
    private static async Task<string> VerifyMergeStateAsync(
        ISandbox sandbox, string baseBranch, string workBranch, string preMergeSha, CancellationToken ct)
    {
        var status = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "status", "--porcelain"],
        }, ct);
        if (!status.Success) throw new InvalidOperationException($"git status failed: {status.Stderr}");
        if (!string.IsNullOrWhiteSpace(status.Stdout))
            throw new InvalidOperationException($"merge agent left unstaged or conflicting changes:\n{status.Stdout}");

        var current = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "branch", "--show-current"],
        }, ct);
        if (!current.Success || current.Stdout.Trim() != baseBranch)
            throw new InvalidOperationException($"merge agent left HEAD on '{current.Stdout.Trim()}', expected '{baseBranch}'");

        var head = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!head.Success) throw new InvalidOperationException($"post-merge rev-parse failed: {head.Stderr}");
        var headSha = head.Stdout.Trim();
        if (headSha == preMergeSha)
            throw new InvalidOperationException("merge agent produced no merge commit (HEAD unchanged)");

        var ancestor = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "merge-base", "--is-ancestor", $"origin/{workBranch}", "HEAD"],
        }, ct);
        if (ancestor.ExitCode != 0)
            throw new InvalidOperationException(
                $"merge agent did not actually merge '{workBranch}' into '{baseBranch}' (workBranch tip not an ancestor of HEAD)");

        return headSha;
    }

    private static string BuildMergePrompt(string baseBranch, string workBranch) => $$"""
        # Merge task

        You are operating inside a sandbox at /work that contains a clone of a
        git repository. Your task: merge branch `{{workBranch}}` into branch `{{baseBranch}}`.

        Constraints:
          - DO NOT push. The orchestrator pushes after verifying your work.
          - DO NOT amend or rebase the existing history.
          - DO NOT delete or comment out code to make conflicts go away.
          - DO NOT take one side blindly when resolving — read both versions
            and preserve the intent of each.

        Steps:
          1. `git fetch origin` (already done by the orchestrator, but safe to repeat)
          2. Confirm you are on `{{baseBranch}}`: `git branch --show-current`
          3. Merge: `git merge --no-ff origin/{{workBranch}} -m "codeybox: merge {{workBranch}}"`
          4. If the merge succeeds without conflicts, you are done. Verify with
             `git log --oneline -3` and exit.
          5. If there are conflicts:
             a. List conflicting files: `git status`
             b. For each file, read both sides (look for `<<<<<<<`, `=======`, `>>>>>>>`)
             c. Resolve carefully, preserving both sides' intent
             d. `git add <file>` for each resolved file
             e. `git commit` (the merge message is already prepared)
             f. Verify: `git status` should be clean; `git log --oneline -3`

        After committing, exit. The orchestrator will:
          - run `git status --porcelain` (must be empty)
          - confirm HEAD is on `{{baseBranch}}`
          - confirm `{{workBranch}}` is reachable from HEAD
          - push `{{baseBranch}}` back to the host bare repo
        """;

    private async Task RunUpstreamPushPhaseAsync(
        WorkItem item,
        IUpstreamRemote upstream,
        string repoId,
        string baseBranch,
        string workBranch,
        string? mergeSha,
        CancellationToken ct)
    {
        await Transition(item, WorkItemState.UpstreamPushing, ct);

        var request = new UpstreamCompletionRequest
        {
            RepositoryId = repoId,
            WorkBranch = workBranch,
            BaseBranch = baseBranch,
            MergeSha = mergeSha,
            Title = item.Title,
            Description = $"Automated via CodeyBox — work item {item.Id}",
        };

        for (var attempt = 1; attempt <= _opts.UpstreamPushMaxAttempts; attempt++)
        {
            var current = await _store.GetAsync(item.Id, ct) ?? item;
            await _store.UpdateAsync(current with { UpstreamPushAttempts = attempt }, ct);

            try
            {
                var outcome = await upstream.CompleteAsync(request, ct);
                if (outcome.PullRequestUrl is not null)
                    _log.LogInformation("Upstream PR: {Url}", outcome.PullRequestUrl);
                if (outcome.MergedSha is not null)
                    _log.LogInformation("Upstream PR auto-merged: {Sha}", outcome.MergedSha);
                if (outcome.Notes is not null)
                    _log.LogInformation("Upstream notes: {Notes}", outcome.Notes);
                await Transition(item, WorkItemState.Done, ct);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Upstream complete attempt {Attempt} failed: {Error}", attempt, ex.Message);
                if (attempt < _opts.UpstreamPushMaxAttempts)
                    await Task.Delay(_opts.UpstreamPushBackoff, ct);
                else
                    await TransitionFailed(item, $"upstream complete failed after {attempt} attempts: {ex.Message}", ct);
            }
        }
    }

    private SandboxSpec BuildSandboxSpec(
        SandboxRepositoryAccess access,
        AgentCredential? includeAgentCredential,
        bool allowAgentNetwork,
        string? hostNetworkProfile = null)
    {
        var mounts = new List<SandboxMount>(access.Mounts)
        {
            new() { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
        };

        var env = new Dictionary<string, string>();
        if (includeAgentCredential is not null)
        {
            mounts.Add(new SandboxMount
            {
                SandboxPath = SandboxConventions.CredentialsDir,
                Tmpfs = true,
                SizeBytes = SandboxConventions.CredentialsTmpfsBytes,
            });
            foreach (var (k, v) in includeAgentCredential.EnvironmentVariables)
                env[k] = v;
        }

        var allowedHosts = allowAgentNetwork ? _opts.AgentAllowedHosts : Array.Empty<string>();
        var net = new SandboxNetworkPolicy
        {
            AllowedHosts = allowedHosts,
            HostGitEndpoint = access.Network.HostGitEndpoint,
            ProfileName = hostNetworkProfile,
        };

        return new SandboxSpec
        {
            ImageReference = _opts.SandboxImageReference,
            Mounts = mounts,
            Environment = env,
            Network = net,
            WorkingDirectory = SandboxConventions.WorkDir,
        };
    }

    private static async Task MaterialiseCredentialFilesAsync(ISandbox sandbox, AgentCredential credential, CancellationToken ct)
    {
        await Run(sandbox, "mkdir", "-p", SandboxConventions.CredentialsDir);
        foreach (var (relativePath, contents) in credential.Files)
        {
            var safePath = SanitiseCredentialFileName(relativePath);
            var fullPath = $"{SandboxConventions.CredentialsDir}/{safePath}";
            var dir = fullPath[..fullPath.LastIndexOf('/')];
            await Run(sandbox, "mkdir", "-p", dir);
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "umask 077 && cat > \"$0\"", fullPath],
                Stdin = contents,
            }, ct);
            if (!write.Success)
                throw new InvalidOperationException($"Failed to write credential file {safePath}: {write.Stderr}");
        }
    }

    private static async Task Run(ISandbox sandbox, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv });
        if (!r.Success)
            throw new InvalidOperationException($"command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}");
    }

    private static string SanitiseCredentialFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Empty credential file name");
        var trimmed = path.Replace('\\', '/').TrimStart('/');
        if (trimmed.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Credential file path must not contain '..': {path}");
        if (trimmed.Length == 0) throw new ArgumentException("Credential file name resolves empty");
        return trimmed;
    }

    private async Task Transition(WorkItem item, WorkItemState state, CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        var next = current.With(state);
        await _store.UpdateAsync(next, ct);
        _log.LogInformation("Work item {Id} → {State}", item.Id, state);
    }

    private async Task TransitionFailed(WorkItem item, string error, CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct) ?? item;
        await _store.UpdateAsync(current.With(WorkItemState.Failed, error), ct);
        _log.LogWarning("Work item {Id} → Failed: {Error}", item.Id, error);
    }
}

internal sealed class AuditFailedException : Exception
{
    public AuditFailedException(string message) : base(message) { }
}

public sealed record PipelineOptions
{
    public required string SandboxImageReference { get; init; }
    public IReadOnlyList<string> AgentAllowedHosts { get; init; } = [];
    public int UpstreamPushMaxAttempts { get; init; } = 5;
    public TimeSpan UpstreamPushBackoff { get; init; } = TimeSpan.FromSeconds(15);
}
