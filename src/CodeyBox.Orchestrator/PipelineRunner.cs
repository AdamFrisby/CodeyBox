using Microsoft.Extensions.Logging;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-work-item pipeline:
///
///   Work phase  →  Audit + rework loop  →  Merge phase  →  Upstream push
///
/// Work, audit (with rework iterations), and merge together are the atomic
/// unit: failure of any of them marks the item Failed (or AuditFailed for
/// the specific case of audit not converging). UpstreamPush runs after
/// success and is retried independently.
///
/// The audit loop is skipped entirely if no <see cref="IAuditor"/> is
/// registered — keeping the pipeline backward-compatible with deployments
/// that don't want the extra phase.
/// </summary>
public sealed class PipelineRunner
{
    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly IPullRequestService _prs;
    private readonly IUpstreamRemote _upstream;
    private readonly IWorkItemStore _store;
    private readonly IAuditorRegistry _auditors;
    private readonly AuditOptions _auditOpts;
    private readonly PipelineOptions _opts;
    private readonly ILogger<PipelineRunner> _log;

    public PipelineRunner(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        IPullRequestService prs,
        IUpstreamRemote upstream,
        IWorkItemStore store,
        IAuditorRegistry auditors,
        AuditOptions auditOpts,
        PipelineOptions opts,
        ILogger<PipelineRunner> log)
    {
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _agents = agents;
        _credentials = credentials;
        _prs = prs;
        _upstream = upstream;
        _store = store;
        _auditors = auditors;
        _auditOpts = auditOpts;
        _opts = opts;
        _log = log;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            // Resolve repo on host (idempotent).
            var repoId = await _gitHost.EnsureRepositoryAsync(item.Id, item.RepositoryUrl, ct);
            var baseBranch = item.BaseBranch ?? await _gitHost.GetDefaultBranchAsync(repoId, ct);
            var workBranch = item.WorkBranch ?? $"codeybox/{item.Id.ToString()[..8]}";
            if (string.Equals(workBranch, baseBranch, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"workBranch must differ from baseBranch (both '{baseBranch}'); refusing to bypass merge-phase containment");

            // -------- Phase 1: Work --------
            await Transition(item, WorkItemState.Working, ct);
            using (var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                workCts.CancelAfter(item.WorkTimeout);
                await RunAgentPhaseAsync(item, repoId, baseBranch, workBranch, item.Prompt, isInitial: true, workCts.Token);
            }

            await Transition(item, WorkItemState.WorkComplete, ct);

            // -------- Phase 1.5: Audit + rework loop --------
            if (_auditors.All.Count > 0)
            {
                await RunAuditLoopAsync(item, repoId, baseBranch, workBranch, ct);
                await Transition(item, WorkItemState.AuditPassed, ct);
            }

            // Open PR record (local metadata) AFTER the audit converges.
            var pr = await _prs.OpenAsync(new OpenPullRequest(
                RepositoryId: repoId,
                SourceBranch: workBranch,
                TargetBranch: baseBranch,
                Title: item.Title,
                Description: $"Work item {item.Id} via {item.Agent.Value}"), ct);

            // -------- Phase 2: Merge (atomic with Phase 1 + audit) --------
            await Transition(item, WorkItemState.Merging, ct);
            string mergeSha;
            using (var mergeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                mergeCts.CancelAfter(item.MergeTimeout);
                mergeSha = await RunMergePhaseAsync(repoId, baseBranch, workBranch, mergeCts.Token);
            }
            await _prs.MarkMergedAsync(pr.Id, mergeSha, ct);
            await Transition(item, WorkItemState.Merged, ct);

            // -------- Phase 3: Upstream push (separate atomic unit) --------
            if (item.PushUpstream && _upstream is not Upstream.NoopUpstreamRemote)
            {
                await RunUpstreamPushPhaseAsync(item, repoId, baseBranch, ct);
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
            await TransitionFailed(item, ex.Message, ct: CancellationToken.None);
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
        string repoId,
        string baseBranch,
        string branch,
        string prompt,
        bool isInitial,
        CancellationToken ct)
    {
        if (!_agents.TryGet(item.Agent, out var runner))
            throw new InvalidOperationException($"No runner registered for agent '{item.Agent}'");

        var credential = await _credentials.GetAsync(item.Agent, ct);
        var access = _gitHost.GetSandboxAccess(repoId);
        var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: true);

        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);

        if (credential is not null && credential.Files.Count > 0)
            await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        if (isInitial)
        {
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "-B", branch);
        }
        else
        {
            // Rework: branch already exists in the bare repo; check it out.
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", branch);
        }
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "codeybox@local");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "CodeyBox");

        var agentResult = await runner.RunAsync(sandbox, SandboxConventions.WorkDir, prompt, credential, ct);
        if (!agentResult.Success)
            throw new InvalidOperationException($"Agent {runner.Kind} reported failure: {agentResult.Summary}\n{agentResult.Stderr}");

        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "add", "-A");
        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--cached", "--quiet"],
        }, ct);
        if (diff.ExitCode == 0)
        {
            // No changes. On the initial work phase this is always a failure.
            // On a rework iteration this means the agent didn't address the
            // findings; fail fast rather than looping uselessly.
            var msg = isInitial
                ? "Agent produced no changes to commit"
                : "Rework agent produced no changes; cannot resolve audit findings";
            throw new InvalidOperationException(msg);
        }

        var commitMessage = isInitial
            ? $"codeybox: {item.Title}"
            : $"codeybox rework: address audit findings";
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "commit", "-m", commitMessage);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"{branch}:{branch}");
    }

    /// <summary>
    /// Runs all configured auditors against the latest workBranch. On
    /// failure, hands findings to the agent and re-runs. Caps at
    /// <see cref="AuditOptions.MaxIterations"/> rounds before throwing
    /// <see cref="AuditFailedException"/>.
    /// </summary>
    private async Task RunAuditLoopAsync(WorkItem item, string repoId, string baseBranch, string workBranch, CancellationToken ct)
    {
        for (var iteration = 1; iteration <= _auditOpts.MaxIterations; iteration++)
        {
            await Transition(item, WorkItemState.Auditing, ct);
            using var auditCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            auditCts.CancelAfter(_auditOpts.PerIterationTimeout);

            var ctx = new AuditContext(item.Id, workBranch, baseBranch, iteration, item.Prompt);
            var findings = await CollectFindingsAsync(repoId, ctx, auditCts.Token);

            var blocking = findings.Where(f => f.Severity >= _auditOpts.FailingSeverity).ToList();
            if (blocking.Count == 0)
            {
                _log.LogInformation("Audit iteration {Iter} passed for {Id} ({NonBlocking} non-blocking findings)",
                    iteration, item.Id, findings.Count);
                return;
            }

            _log.LogInformation("Audit iteration {Iter} of {Max} found {Count} blocking findings for {Id}",
                iteration, _auditOpts.MaxIterations, blocking.Count, item.Id);

            if (iteration == _auditOpts.MaxIterations)
            {
                var summary = string.Join("; ", blocking.Take(5).Select(f => $"[{f.AuditorName}] {f.Title}"));
                throw new AuditFailedException(
                    $"Audit did not pass after {iteration} iterations. {blocking.Count} blocking finding(s): {summary}");
            }

            // Rework: hand findings to the agent and ask it to fix.
            await Transition(item, WorkItemState.Reworking, ct);
            var reworkPrompt = ReworkPromptBuilder.Build(item.Prompt, findings, iteration, _auditOpts.MaxIterations);
            using var reworkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reworkCts.CancelAfter(item.WorkTimeout);
            await RunAgentPhaseAsync(item, repoId, baseBranch, workBranch, reworkPrompt, isInitial: false, reworkCts.Token);
        }
    }

    /// <summary>
    /// Groups auditors by capability and spawns a separate sandbox per
    /// group. Tool-only auditors (no agent creds, no network) run first in
    /// a credential-free sandbox; LLM auditors run after in a sandbox with
    /// agent creds + network. Findings are merged.
    /// </summary>
    private async Task<IReadOnlyList<AuditFinding>> CollectFindingsAsync(string repoId, AuditContext ctx, CancellationToken ct)
    {
        var findings = new List<AuditFinding>();
        var byCaps = _auditors.All.GroupBy(a => a.Required).ToList();

        foreach (var group in byCaps)
        {
            var needsCreds = group.Key.HasFlag(AuditCapabilities.AgentCredentials);
            var needsNetwork = group.Key.HasFlag(AuditCapabilities.Network);

            // Build a SandboxSpec scoped to this capability set. A
            // credential-free sandbox cannot exfiltrate the agent's API key
            // even if a tool inside it tries to.
            AgentCredential? credential = null;
            if (needsCreds)
            {
                // The work item's agent is the default audit agent too. An
                // operator wanting a different identity for review should
                // register an LlmReviewAuditor that wraps a different
                // IAgentRunner with its own credential mapping.
                credential = await _credentials.GetAsync(ctx.WorkItemId.ToString() is { } ? AgentKindForAudit() : default, ct);
            }

            var access = _gitHost.GetSandboxAccess(repoId);
            var spec = BuildSandboxSpec(access, includeAgentCredential: credential, allowAgentNetwork: needsNetwork);
            // Audit sandbox needs an /audit dir for LLM auditors to drop their JSON verdict.
            spec = spec with { Mounts = [.. spec.Mounts, new SandboxMount { SandboxPath = "/audit", Tmpfs = true, SizeBytes = 1024 * 1024 }] };

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            if (credential is not null && credential.Files.Count > 0)
                await MaterialiseCredentialFilesAsync(sandbox, credential, ct);

            // Clone the work branch read-mostly. Auditors should not need
            // to push; they may write into /work transiently. The bare repo
            // mount is technically writable, but auditors are not expected
            // to touch it.
            await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
            await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", ctx.WorkBranch);

            foreach (var auditor in group)
            {
                _log.LogInformation("Running auditor {Name} (iteration {Iter})", auditor.Name, ctx.Iteration);
                var result = await auditor.RunAsync(sandbox, SandboxConventions.WorkDir, ctx, ct);
                findings.AddRange(result.Findings);
                if (_auditOpts.StopOnFirstFailure && result.Findings.Any(f => f.Severity >= _auditOpts.FailingSeverity))
                    return findings;
            }
        }

        return findings;
    }

    // The audit phase uses the same agent kind as the work item. Pulled out
    // for symmetry with future per-auditor agent overrides.
    private static AgentKind AgentKindForAudit() => AgentKind.Claude;

    private async Task<string> RunMergePhaseAsync(string repoId, string baseBranch, string workBranch, CancellationToken ct)
    {
        // Merge sandbox: NO agent credentials. Even if the merge sandbox is
        // compromised, it cannot reach any LLM API. Network is constrained
        // to the host git endpoint only.
        var access = _gitHost.GetSandboxAccess(repoId);
        var spec = BuildSandboxSpec(access, includeAgentCredential: null, allowAgentNetwork: false);
        await using var sandbox = await _sandboxes.CreateAsync(spec, ct);

        await Run(sandbox, "git", "clone", access.CloneUrlInsideSandbox, SandboxConventions.WorkDir);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", baseBranch);
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "codeybox@local");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "CodeyBox");
        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "merge", "--no-ff",
            "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}");

        var sha = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "HEAD"],
        }, ct);
        if (!sha.Success) throw new InvalidOperationException($"rev-parse failed: {sha.Stderr}");

        await Run(sandbox, "git", "-C", SandboxConventions.WorkDir, "push", "origin", $"{baseBranch}:{baseBranch}");
        return sha.Stdout.Trim();
    }

    private async Task RunUpstreamPushPhaseAsync(WorkItem item, string repoId, string baseBranch, CancellationToken ct)
    {
        await Transition(item, WorkItemState.UpstreamPushing, ct);
        for (var attempt = 1; attempt <= _opts.UpstreamPushMaxAttempts; attempt++)
        {
            var current = await _store.GetAsync(item.Id, ct) ?? item;
            await _store.UpdateAsync(current with { UpstreamPushAttempts = attempt }, ct);

            var result = await _upstream.PushAsync(repoId, baseBranch, ct);
            if (result.Success)
            {
                await Transition(item, WorkItemState.Done, ct);
                return;
            }
            _log.LogWarning("Upstream push attempt {Attempt} failed: {Error}", attempt, result.Error);
            if (attempt < _opts.UpstreamPushMaxAttempts)
                await Task.Delay(_opts.UpstreamPushBackoff, ct);
            else
                await TransitionFailed(item, $"upstream push failed after {attempt} attempts: {result.Error}", ct);
        }
    }

    private SandboxSpec BuildSandboxSpec(SandboxRepositoryAccess access, AgentCredential? includeAgentCredential, bool allowAgentNetwork)
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
