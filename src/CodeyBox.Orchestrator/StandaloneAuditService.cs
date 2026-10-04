using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Prepares an isolated working directory at the run's resolved SHA and
/// provisions the tool sandbox for auditor execution. The production
/// implementation clones/checks out under the project's repository policy;
/// tests substitute a fixture directory. Never mounts coding-agent or git
/// push credentials.
/// </summary>
public interface IStandaloneAuditWorkspaceFactory
{
    Task<StandaloneAuditWorkspace> PrepareAsync(AuditRunRecord run, CancellationToken ct = default);
}

public sealed record StandaloneAuditWorkspace(
    string WorkingDirectory,
    string SandboxProvider,
    string? BaselineIdentity,
    ISandbox? Sandbox = null,
    Func<ValueTask>? TeardownAsync = null);

/// <summary>
/// Executes one auditor against a prepared workspace. Tool-only: the
/// executor must never invoke a coding agent, commit, merge, or push.
/// </summary>
public interface IStandaloneAuditExecutor
{
    Task<StandaloneAuditorExecution> ExecuteAsync(
        IAuditor auditor,
        StandaloneAuditWorkspace workspace,
        AuditContext context,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public sealed record StandaloneAuditorExecution(
    bool Passed,
    IReadOnlyList<AuditFinding> Findings,
    string? RawOutput,
    int ExitCode,
    string? ToolVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

/// <summary>
/// Outcome of a create call: either the durable run or a rejection.
/// </summary>
public sealed record StandaloneAuditCreateOutcome(
    AuditRunRecord? Run,
    string? Error,
    bool Replay,
    bool Conflict = false)
{
    public static StandaloneAuditCreateOutcome Created(AuditRunRecord run, bool replay = false) => new(run, null, replay);
    public static StandaloneAuditCreateOutcome Rejected(string error, bool conflict = false) => new(null, error, false, conflict);
}

/// <summary>
/// Standalone audit-run service. Owns validation, durable lifecycle,
/// bounded execution, cancellation, and restart reconciliation.
///
/// Never calls the work-item pipeline: no <see cref="IAgentRunner"/>,
/// no synthetic <c>WorkItem</c>, no commit/merge/push. Tool auditors run
/// through <see cref="IStandaloneAuditExecutor"/> with credential-free
/// workspaces; LLM / agent-credential auditors are rejected as Unsupported
/// before provisioning.
/// </summary>
public sealed class StandaloneAuditService
{
    private readonly IAuditRunStore _runs;
    private readonly IAuditRunArtifactStore _artifacts;
    private readonly IAuditorRegistry _auditors;
    private readonly IProjectRepository _projects;
    private readonly IStandaloneAuditRefResolver _refs;
    private readonly IStandaloneAuditWorkspaceFactory _workspaces;
    private readonly IStandaloneAuditExecutor _executor;
    private readonly IOptionsMonitor<AuditRunOptions> _options;
    private readonly ILogger<StandaloneAuditService> _log;
    private readonly SemaphoreSlim _capacity;
    private readonly Dictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _maxCapacity;

    public StandaloneAuditService(
        IAuditRunStore runs,
        IAuditRunArtifactStore artifacts,
        IAuditorRegistry auditors,
        IProjectRepository projects,
        IStandaloneAuditRefResolver refs,
        IStandaloneAuditWorkspaceFactory workspaces,
        IStandaloneAuditExecutor executor,
        IOptionsMonitor<AuditRunOptions> options,
        ILogger<StandaloneAuditService> log)
    {
        _runs = runs;
        _artifacts = artifacts;
        _auditors = auditors;
        _projects = projects;
        _refs = refs;
        _workspaces = workspaces;
        _executor = executor;
        _options = options;
        _log = log;
        _maxCapacity = Math.Max(1, options.CurrentValue.MaxConcurrentRuns);
        _capacity = new SemaphoreSlim(_maxCapacity, _maxCapacity);
        options.OnChange(o => { _maxCapacity = Math.Max(1, o.MaxConcurrentRuns); });
    }

    private AuditRunOptions Opts => _options.CurrentValue;

    public async Task<StandaloneAuditCreateOutcome> CreateAsync(AuditRunCreateRequest request, string? initiator = null, CancellationToken ct = default)
    {
        if (!Opts.Enabled)
            return StandaloneAuditCreateOutcome.Rejected("Standalone audit runs are disabled by configuration.");
        var selError = AuditRunValidation.ValidateSelection(request.Selection);
        if (selError is not null)
            return StandaloneAuditCreateOutcome.Rejected(selError);
        var refError = AuditRunValidation.ValidateRef(request.Ref, "ref");
        if (refError is not null)
            return StandaloneAuditCreateOutcome.Rejected(refError);
        if (request.BaseRef is not null)
        {
            var baseError = AuditRunValidation.ValidateRef(request.BaseRef, "baseRef");
            if (baseError is not null)
                return StandaloneAuditCreateOutcome.Rejected(baseError);
        }
        if (string.IsNullOrWhiteSpace(request.ProjectId))
            return StandaloneAuditCreateOutcome.Rejected("project is required.");
        var projectId = request.ProjectId.Trim();
        var project = await _projects.GetAsync(new ProjectId(projectId), ct).ConfigureAwait(false);
        if (project is null)
            return StandaloneAuditCreateOutcome.Rejected($"Unknown project '{projectId}'.");
        if (!string.IsNullOrWhiteSpace(request.RepositoryOverride)
            && !string.Equals(request.RepositoryOverride.Trim(), project.RepositoryUrl, StringComparison.Ordinal))
            return StandaloneAuditCreateOutcome.Rejected(
                "repository override does not match the project's configured repository policy.");

        var resolved = await _refs.ResolveAsync(projectId, request.Ref.Trim(), ct).ConfigureAwait(false);
        if (!resolved.Ok || string.IsNullOrWhiteSpace(resolved.Sha))
            return StandaloneAuditCreateOutcome.Rejected($"Invalid ref: {resolved.Error ?? "unresolvable"}.");
        string? baseSha = null;
        if (request.BaseRef is not null)
        {
            var br = await _refs.ResolveAsync(projectId, request.BaseRef.Trim(), ct).ConfigureAwait(false);
            if (!br.Ok || string.IsNullOrWhiteSpace(br.Sha))
                return StandaloneAuditCreateOutcome.Rejected($"Invalid baseRef: {br.Error ?? "unresolvable"}.");
            baseSha = br.Sha;
        }

        var selection = await ResolveSelectionAsync(request.Selection, baseSha is not null, ct).ConfigureAwait(false);
        if (selection.Error is not null)
            return StandaloneAuditCreateOutcome.Rejected(selection.Error);

        var bodyHash = AuditRunValidation.BodyHash(request);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var replay = await _runs.GetByIdempotencyAsync(projectId, request.IdempotencyKey, bodyHash, ct).ConfigureAwait(false);
            if (replay is not null)
                return StandaloneAuditCreateOutcome.Created(replay, replay: true);
            var clash = await _runs.GetByIdempotencyKeyAsync(projectId, request.IdempotencyKey, ct).ConfigureAwait(false);
            if (clash is not null)
                return StandaloneAuditCreateOutcome.Rejected(
                    "Idempotency-Key was already used with a different request body.", conflict: true);
        }

        var active = await _runs.CountActiveAsync(ct).ConfigureAwait(false);
        if (active >= Opts.MaxQueuedRuns)
            return StandaloneAuditCreateOutcome.Rejected("Audit-run capacity exhausted; retry later.");

        var now = DateTimeOffset.UtcNow;
        var run = new AuditRunRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            State = AuditRunState.Queued,
            AggregateOutcome = AuditRunAggregateOutcome.Unknown,
            Provenance = new AuditRunProvenance
            {
                ProjectId = projectId,
                RequestedRef = request.Ref.Trim(),
                ResolvedSha = resolved.Sha,
                RequestedBaseRef = request.BaseRef?.Trim(),
                ResolvedBaseSha = baseSha,
                ResolvedAuditorIds = selection.AuditorIds,
                ResolvedProfile = selection.Profile,
                ConfigDigest = AuditRunOptions.ComputeConfigDigest(Opts, selection.AuditorIds),
                CreatedAt = now,
                CreatedBy = initiator,
            },
            AuditorResults = selection.Unsupported.Select(u => new AuditRunAuditorResult
            {
                AuditorName = u.Name,
                AuditorKind = u.Kind,
                Outcome = AuditRunAuditorOutcome.Unsupported,
                UnsupportedReason = u.Reason,
                UnsupportedDetail = u.Detail,
                StartedAt = now,
                EndedAt = now,
            }).ToList(),
            IdempotencyKey = request.IdempotencyKey,
            IdempotencyBodyHash = bodyHash,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _runs.CreateAsync(run, ct).ConfigureAwait(false);
        _ = Task.Run(() => ExecuteAsync(run.Id, CancellationToken.None));
        return StandaloneAuditCreateOutcome.Created(run);
    }

    private sealed record ResolvedSelection(IReadOnlyList<string> AuditorIds, string? Profile, string? Error, IReadOnlyList<UnsupportedMember> Unsupported);
    private sealed record UnsupportedMember(string Name, string Kind, AuditRunUnsupportedReason Reason, string? Detail);

    private async Task<ResolvedSelection> ResolveSelectionAsync(
        AuditRunSelection selection, bool hasBaseSha, CancellationToken ct)
    {
        await Task.CompletedTask;
        IReadOnlyList<string> ids;
        string? profile = null;
        if (selection.IsExplicit)
        {
            ids = selection.AuditorIds.Select(a => a.Trim()).ToList();
        }
        else
        {
            var name = selection.Profile!.Trim();
            if (!Opts.Profiles.TryGetValue(name, out var members) || members.Count == 0)
                return new ResolvedSelection([], null, $"Unknown audit profile '{name}'.", []);
            profile = name;
            ids = members.Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            if (ids.Count == 0)
                return new ResolvedSelection([], null, $"Unknown audit profile '{name}'.", []);
        }
        var byName = _auditors.All.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var unknown = ids.Where(id => !byName.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            return new ResolvedSelection([], null, $"Unknown auditor(s): {string.Join(", ", unknown)}.", []);
        var unsupported = new List<UnsupportedMember>();
        foreach (var id in ids)
        {
            var auditor = byName[id];
            var diffBased = string.Equals(auditor.Kind, "diff-pattern", StringComparison.OrdinalIgnoreCase);
            var (supported, reason, detail) = AuditRunValidation.ClassifyAuditor(
                auditor, diffBased, hasBaseSha, auditorEnabled: true);
            if (!supported)
                unsupported.Add(new UnsupportedMember(auditor.Name, auditor.Kind, reason, detail));
        }
        return new ResolvedSelection(ids, profile, null, unsupported);
    }

    public Task<AuditRunRecord?> GetAsync(string runId, string? projectScope, CancellationToken ct = default) =>
        GetScopedAsync(runId, projectScope, ct);

    internal async Task<AuditRunRecord?> GetScopedAsync(string runId, string? projectScope, CancellationToken ct)
    {
        var run = await _runs.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return null;
        if (!string.IsNullOrWhiteSpace(projectScope)
            && !string.Equals(run.ProjectId, projectScope, StringComparison.Ordinal))
            return null;
        return run;
    }

    public Task<IReadOnlyList<AuditRunRecord>> ListAsync(string? projectId, int limit, CancellationToken ct = default) =>
        _runs.ListAsync(projectId, Math.Clamp(limit, 1, 200), ct);

    public async Task<(bool Ok, string? Error)> CancelAsync(string runId, CancellationToken ct = default)
    {
        var run = await _runs.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return (false, "Not found.");
        if (AuditRunLifecycle.IsTerminal(run.State))
            return (false, $"Run is already {run.State}.");
        CancellationTokenSource? cts;
        lock (_gate) _inflight.TryGetValue(runId, out cts);
        try
        {
            if (cts is not null)
                await cts.CancelAsync();
        }
        catch (ObjectDisposedException) { }
        var updated = run with
        {
            State = AuditRunState.Cancelled,
            AggregateOutcome = AuditRunAggregateOutcome.Cancelled,
            AuditorResults = PreservePartialWithCancellation(run.AuditorResults),
            CompletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _runs.UpdateAsync(updated, ct).ConfigureAwait(false);
        await TeardownQuietlyAsync(runId).ConfigureAwait(false);
        return (true, null);
    }

    private static IReadOnlyList<AuditRunAuditorResult> PreservePartialWithCancellation(
        IReadOnlyList<AuditRunAuditorResult> results) =>
        results.Select(r => r.Outcome is AuditRunAuditorOutcome.Pass or AuditRunAuditorOutcome.Fail
            ? r
            : r with { Outcome = AuditRunAuditorOutcome.Cancelled }).ToList();

    /// <summary>
    /// Restart reconciliation: mark interrupted non-terminal runs Failed with
    /// an explicit marker. Never auto-reruns a possibly completed tool.
    /// </summary>
    public async Task<int> ReconcileInterruptedAsync(CancellationToken ct = default)
    {
        var actives = await _runs.ListAsync(null, 1000, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var run in actives.Where(r => !AuditRunLifecycle.IsTerminal(r.State)))
        {
            var updated = run with
            {
                State = AuditRunState.Failed,
                AggregateOutcome = AuditRunAggregateOutcome.InfrastructureFailure,
                FailureDetail = "interrupted: worker restarted before completion; not auto-rerun — resubmit to retry.",
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _runs.UpdateAsync(updated, ct).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private async Task ExecuteAsync(string runId, CancellationToken hostCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        lock (_gate) _inflight[runId] = cts;
        var ct = cts.Token;
        try
        {
            var run = await _runs.GetAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (run is null || AuditRunLifecycle.IsTerminal(run.State))
                return;
            if (!await _capacity.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                await _runs.UpdateAsync(run with
                {
                    State = AuditRunState.Failed,
                    AggregateOutcome = AuditRunAggregateOutcome.InfrastructureFailure,
                    FailureDetail = "capacity: no execution slot available.",
                    CompletedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ConfigureAwait(false);
                return;
            }
            try
            {
                await RunPhasesAsync(run, ct).ConfigureAwait(false);
            }
            finally
            {
                _capacity.Release();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Standalone audit run {RunId} failed", runId);
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(runId);
            }
            cts.Dispose();
        }
    }

    private async Task RunPhasesAsync(AuditRunRecord run, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Opts.TotalRunTimeoutSeconds);
        if (!await TransitionAsync(run.Id, AuditRunState.Queued, AuditRunState.Provisioning).ConfigureAwait(false))
            return;
        run = (await _runs.GetAsync(run.Id).ConfigureAwait(false))!;

        StandaloneAuditWorkspace? workspace = null;
        try
        {
            try
            {
                using var provisionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                provisionCts.CancelAfter(TimeSpan.FromSeconds(Opts.PerAuditorTimeoutSeconds));
                workspace = await _workspaces.PrepareAsync(run, provisionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await FailAsync(run.Id, $"provisioning: {TrimOneLine(ex.Message)}").ConfigureAwait(false);
                return;
            }
            if (!await TransitionAsync(run.Id, AuditRunState.Provisioning, AuditRunState.Running).ConfigureAwait(false))
                return;
            if (!await TransitionAsync(run.Id, AuditRunState.Running, AuditRunState.Collecting).ConfigureAwait(false))
                return;

            var results = new List<AuditRunAuditorResult>(run.AuditorResults);
            var byName = _auditors.All.ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var id in run.Provenance.ResolvedAuditorIds)
            {
                if (ct.IsCancellationRequested)
                    break;
                if (results.Any(r => string.Equals(r.AuditorName, id, StringComparison.Ordinal)
                    && r.Outcome == AuditRunAuditorOutcome.Unsupported))
                    continue;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    results.Add(TimeoutResult(id, byName[id].Kind));
                    continue;
                }
                var auditor = byName[id];
                var outcome = await RunOneAuditorAsync(run, auditor, workspace!, deadline, ct).ConfigureAwait(false);
                results.Add(outcome);
                await PersistPartialAsync(run.Id, results).ConfigureAwait(false);
                if (outcome.Outcome == AuditRunAuditorOutcome.Cancelled)
                    break;
            }

            var current = await _runs.GetAsync(run.Id).ConfigureAwait(false);
            if (current is null || AuditRunLifecycle.IsTerminal(current.State))
                return;
            var aggregate = ct.IsCancellationRequested
                ? AuditRunAggregateOutcome.Cancelled
                : AuditRunLifecycle.Aggregate(results);
            await _runs.UpdateAsync(current with
            {
                State = AuditRunLifecycle.TerminalStateFor(aggregate),
                AggregateOutcome = aggregate,
                AuditorResults = results,
                Attempts = current.Attempts + 1,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);
        }
        finally
        {
            if (workspace?.TeardownAsync is not null)
            {
                try { await workspace.TeardownAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Audit run {RunId} sandbox teardown failed", run.Id); }
            }
        }
    }

    private async Task<AuditRunAuditorResult> RunOneAuditorAsync(
        AuditRunRecord run, IAuditor auditor, StandaloneAuditWorkspace workspace,
        DateTimeOffset deadline, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var remaining = deadline - DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Min(Opts.PerAuditorTimeoutSeconds, Math.Max(10, remaining.TotalSeconds)));
        var attempt = 1;
        Exception? lastError = null;
        for (; attempt <= Opts.MaxAttemptsPerAuditor; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(timeout);
                var context = new AuditContext(
                    new WorkItemId(Guid.Empty),
                    run.Provenance.ResolvedSha,
                    run.Provenance.ResolvedBaseSha ?? run.Provenance.ResolvedSha,
                    1,
                    string.Empty);
                var exec = await _executor.ExecuteAsync(auditor, workspace, context, timeout, attemptCts.Token).ConfigureAwait(false);
                var ended = DateTimeOffset.UtcNow;
                var excerpt = AuditRunValidation.TruncateBounded(
                    AuditRunValidation.Redact(exec.RawOutput ?? string.Empty), Opts.MaxRawOutputChars);
                var findings = exec.Findings.Select(f => new AuditReportFinding(
                    FindingIdComputer.Compute(f.AuditorName, f.Title, f.Location is null ? [] : [f.Location]),
                    f.Severity.ToString(),
                    f.Title,
                    f.Description,
                    f.Location is null ? [] : [f.Location],
                    [])).ToList();
                var fullArtifact = Encoding.UTF8.GetBytes(exec.RawOutput ?? string.Empty);
                string? artifactDigest = null;
                if (fullArtifact.Length > 0 && fullArtifact.Length <= Opts.MaxArtifactBytes)
                {
                    artifactDigest = Convert.ToHexString(SHA256.HashData(fullArtifact)).ToLowerInvariant();
                    try
                    {
                        await _artifacts.PutAsync(run.Id, $"auditor-{auditor.Name}.log", "text/plain; charset=utf-8", fullArtifact).ConfigureAwait(false);
                        await AppendArtifactRefAsync(run.Id, $"auditor-{auditor.Name}.log", artifactDigest, fullArtifact.LongLength).ConfigureAwait(false);
                    }
                    catch (AuditRunArtifactTooLargeException) { artifactDigest = null; }
                }
                else if (fullArtifact.Length > Opts.MaxArtifactBytes)
                {
                    findings.Add(new AuditReportFinding(
                        FindingIdComputer.Compute(auditor.Name, "artifact-oversized", []),
                        nameof(AuditSeverity.Warning),
                        "Artifact oversized",
                        $"Full output ({fullArtifact.Length} bytes) exceeded the {Opts.MaxArtifactBytes}-byte artifact cap and was not stored; the log excerpt carries the truncated prefix.",
                        [], []));
                }
                return new AuditRunAuditorResult
                {
                    AuditorName = auditor.Name,
                    AuditorKind = auditor.Kind,
                    Outcome = exec.Passed ? AuditRunAuditorOutcome.Pass : AuditRunAuditorOutcome.Fail,
                    Findings = findings,
                    ExitCode = exec.ExitCode,
                    StartedAt = started,
                    EndedAt = ended,
                    DurationMs = (long)(ended - started).TotalMilliseconds,
                    ToolVersion = exec.ToolVersion,
                    Attempt = attempt,
                    EvidenceSufficient = true,
                    LogExcerpt = excerpt,
                    LogArtifactDigest = artifactDigest,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CancelledResult(auditor, started, attempt);
            }
            catch (OperationCanceledException)
            {
                return TimeoutResult(auditor.Name, auditor.Kind, started, attempt);
            }
            catch (AuditUnavailableException ex)
            {
                return UnavailableResult(auditor, started, attempt, ex.Message);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        return new AuditRunAuditorResult
        {
            AuditorName = auditor.Name,
            AuditorKind = auditor.Kind,
            Outcome = AuditRunAuditorOutcome.Error,
            StartedAt = started,
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            Attempt = attempt - 1,
            UnsupportedDetail = TrimOneLine(lastError?.Message ?? "unknown error"),
        };
    }

    private AuditRunAuditorResult TimeoutResult(string name, string kind, DateTimeOffset? started = null, int attempt = 1)
    {
        var s = started ?? DateTimeOffset.UtcNow;
        return new AuditRunAuditorResult
        {
            AuditorName = name, AuditorKind = kind, Outcome = AuditRunAuditorOutcome.TimedOut,
            StartedAt = s, EndedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)(DateTimeOffset.UtcNow - s).TotalMilliseconds,
            Attempt = attempt,
            UnsupportedDetail = "Tool timeout: partial findings preserved; aggregate cannot Pass.",
        };
    }

    private static AuditRunAuditorResult CancelledResult(IAuditor auditor, DateTimeOffset started, int attempt) =>
        new()
        {
            AuditorName = auditor.Name, AuditorKind = auditor.Kind, Outcome = AuditRunAuditorOutcome.Cancelled,
            StartedAt = started, EndedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            Attempt = attempt,
        };

    private static AuditRunAuditorResult UnavailableResult(IAuditor auditor, DateTimeOffset started, int attempt, string detail) =>
        new()
        {
            AuditorName = auditor.Name, AuditorKind = auditor.Kind, Outcome = AuditRunAuditorOutcome.Unavailable,
            StartedAt = started, EndedAt = DateTimeOffset.UtcNow,
            DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            Attempt = attempt,
            UnsupportedDetail = detail,
        };

    private async Task<bool> TransitionAsync(string runId, AuditRunState expected, AuditRunState next)
    {
        var cur = await _runs.GetAsync(runId).ConfigureAwait(false);
        if (cur is null || cur.State != expected || !AuditRunLifecycle.CanTransition(expected, next))
            return false;
        return await _runs.TryUpdateStateAsync(runId, expected, cur with { State = next, UpdatedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
    }

    private async Task PersistPartialAsync(string runId, IReadOnlyList<AuditRunAuditorResult> results)
    {
        var cur = await _runs.GetAsync(runId).ConfigureAwait(false);
        if (cur is null || AuditRunLifecycle.IsTerminal(cur.State))
            return;
        await _runs.UpdateAsync(cur with { AuditorResults = results, UpdatedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
    }

    private async Task AppendArtifactRefAsync(string runId, string name, string digest, long size)
    {
        var cur = await _runs.GetAsync(runId).ConfigureAwait(false);
        if (cur is null || AuditRunLifecycle.IsTerminal(cur.State))
            return;
        if (cur.Artifacts.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
            return;
        await _runs.UpdateAsync(cur with
        {
            Artifacts = [.. cur.Artifacts, new AuditRunArtifactRef
            {
                Name = name,
                ContentDigest = digest,
                SizeBytes = size,
                MediaType = "text/plain; charset=utf-8",
            }],
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);
    }

    private async Task FailAsync(string runId, string detail)
    {
        var cur = await _runs.GetAsync(runId).ConfigureAwait(false);
        if (cur is null || AuditRunLifecycle.IsTerminal(cur.State))
            return;
        await _runs.UpdateAsync(cur with
        {
            State = AuditRunState.Failed,
            AggregateOutcome = AuditRunAggregateOutcome.InfrastructureFailure,
            FailureDetail = detail,
            CompletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);
    }

    private async Task TeardownQuietlyAsync(string runId)
    {
        await Task.CompletedTask;
    }

    private static string TrimOneLine(string message)
    {
        var line = (message ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}

/// <summary>
/// Built-in ref resolver: accepts only explicit 40-hex SHAs. Branch names
/// and other symbolic refs are rejected so resolution never depends on
/// mutable upstream state at create time.
/// </summary>
public sealed class ExplicitShaAuditRefResolver : IStandaloneAuditRefResolver
{
    public Task<RefResolveResult> ResolveAsync(string projectId, string requestedRef, CancellationToken ct = default)
    {
        var value = requestedRef.Trim();
        return Task.FromResult(AuditRunValidation.IsExplicitSha(value)
            ? RefResolveResult.Resolved(value.ToLowerInvariant())
            : RefResolveResult.Failed("Only explicit 40-hex SHAs are accepted; symbolic refs are rejected."));
    }
}

/// <summary>
/// Direct executor: invokes the registered tool auditor against the prepared
/// sandbox. Tool-only by construction — the caller never supplies an
/// <see cref="IAgentRunner"/>, and unsupported (LLM / agent-credential)
/// auditors are rejected before provisioning, so this path cannot mount
/// coding-agent credentials or reach commit/merge/push.
/// </summary>
public sealed class DirectSandboxAuditExecutor : IStandaloneAuditExecutor
{
    public async Task<StandaloneAuditorExecution> ExecuteAsync(
        IAuditor auditor,
        StandaloneAuditWorkspace workspace,
        AuditContext context,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (workspace.Sandbox is null)
            throw new AuditUnavailableException(
                $"could-not-verify: no sandbox was provisioned for auditor {auditor.Name}.");
        var started = DateTimeOffset.UtcNow;
        var result = await auditor.RunAsync(workspace.Sandbox, workspace.WorkingDirectory, context, ct).ConfigureAwait(false);
        var ended = DateTimeOffset.UtcNow;
        return new StandaloneAuditorExecution(
            result.Passed,
            result.Findings,
            result.RawOutput,
            result.Passed ? 0 : 1,
            null,
            started,
            ended);
    }
}

/// <summary>
/// Workspace factory used when no real provider is configured: always fails
/// visibly so a missing provider can never masquerade as a pass.
/// </summary>
public sealed class UnavailableAuditWorkspaceFactory(string reason) : IStandaloneAuditWorkspaceFactory
{
    public Task<StandaloneAuditWorkspace> PrepareAsync(AuditRunRecord run, CancellationToken ct = default) =>
        throw new AuditUnavailableException(reason);
}
