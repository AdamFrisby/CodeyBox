using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of one publication scope within a
/// <see cref="AuditCheckPublicationService"/> pass.
/// </summary>
public enum AuditCheckScopeOutcome
{
    Disabled,
    Unsupported,
    AlreadyPublished,
    DeferredRetry,
    Published,
    Superseded,
    RetryScheduled,
    Blocked,
    RequestInvalid,
    DeliveryRequired,
}

public sealed record AuditCheckScopeResult(
    string Scope,
    string Target,
    int Iteration,
    AuditCheckScopeOutcome Outcome,
    string? Detail = null,
    long? CheckRunId = null);

public sealed record AuditCheckPublicationRunResult(
    bool Enabled,
    IReadOnlyList<AuditCheckScopeResult> Scopes);

/// <summary>
/// Opt-in publisher of structured audit findings as forge check runs. Reads
/// concluded <see cref="AuditReport"/> rows, classifies each scope's verdict
/// from explicit audit evidence (report-row existence, never finding counts),
/// and publishes through the project's <see cref="IUpstreamRemote"/>.
///
/// Guarantees:
/// <list type="bullet">
/// <item>Disabled by default at both the global options and per-project
/// levels; a disabled pass is an explicit no-op.</item>
/// <item>The actual audit verdict is independent of transport success: a
/// failed delivery never rewrites the verdict, and delivery failure is
/// recorded visibly on the publication row.</item>
/// <item>Retries are bounded (<c>RetryMaxAttempts</c>) with exponential
/// backoff honoring the server's <c>Retry-After</c>; auth/permission denial
/// and validation failures are terminal <c>Blocked</c> states, never retried.
/// </item>
/// <item>Restart/concurrent-safe: same-key completions are skipped, stale
/// completions cannot overwrite newer runs (see
/// <see cref="IAuditCheckPublicationStore.TryCompleteAsync"/>), and lost
/// forge responses reconcile by <c>external_id</c> in the remote.</item>
/// </list>
/// This service never changes merge gating: <c>RequireDelivery</c> only
/// surfaces a distinct <c>DeliveryRequired</c> outcome for callers to decide on.
/// </summary>
public sealed class AuditCheckPublicationService
{
    private const int MaxStoredErrorChars = 2000;
    public const string AggregateScope = "aggregate";

    private readonly IAuditReportStore _auditReports;
    private readonly IAuditCheckPublicationStore _publications;
    private readonly Func<AuditCheckPublicationOptions> _optionsAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public AuditCheckPublicationService(
        IAuditReportStore auditReports,
        IAuditCheckPublicationStore publications,
        Func<AuditCheckPublicationOptions> optionsAccessor,
        TimeProvider? time = null,
        ILogger<AuditCheckPublicationService>? log = null)
    {
        ArgumentNullException.ThrowIfNull(auditReports);
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        optionsAccessor().Validate();
        _auditReports = auditReports;
        _publications = publications;
        _optionsAccessor = optionsAccessor;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<AuditCheckPublicationService>.Instance;
    }

    /// <summary>
    /// Publishes the audit scopes of one work-item iteration for the exact
    /// audited commit: one aggregate verdict plus one check run per auditor
    /// with reports in that iteration. When the iteration has no reports, a
    /// single aggregate <c>NotRun(Missing)</c> check is published so the
    /// absence of coverage is visible instead of silent.
    /// </summary>
    public async Task<AuditCheckPublicationRunResult> PublishWorkItemAsync(
        Project project,
        IUpstreamRemote upstream,
        string workItemId,
        string headSha,
        int iteration,
        int attempt,
        string? sourceRevision,
        string? detailsUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);

        // Resolved per call (not cached at construction) so operator edits to
        // the configuration section take effect without a restart.
        var options = _optionsAccessor();
        options.Validate();

        if (!options.Enabled || !project.AuditChecks.Enabled)
            return new AuditCheckPublicationRunResult(false, []);

        var results = new List<AuditCheckScopeResult>();
        AuditCheckPublicationSupport support;
        try
        {
            support = await upstream.GetAuditCheckPublicationSupportAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AuditCheckPublicationRunResult(true, [new AuditCheckScopeResult(
                AggregateScope, string.Empty, iteration, AuditCheckScopeOutcome.Blocked,
                Truncate($"Capability probe failed: {ex.Message}") )]);
        }
        if (!support.Supported)
            return new AuditCheckPublicationRunResult(true, [new AuditCheckScopeResult(
                AggregateScope, string.Empty, iteration, AuditCheckScopeOutcome.Unsupported, support.Reason)]);

        if (!TryResolveRepository(project, out var owner, out var repository, out var repoProblem))
            return new AuditCheckPublicationRunResult(true, [new AuditCheckScopeResult(
                AggregateScope, string.Empty, iteration, AuditCheckScopeOutcome.Unsupported, repoProblem)]);

        try
        {
            AuditCheckPayloadBuilder.ValidateHeadSha(headSha);
        }
        catch (AuditCheckValidationException ex)
        {
            return new AuditCheckPublicationRunResult(true, [new AuditCheckScopeResult(
                AggregateScope, string.Empty, iteration, AuditCheckScopeOutcome.RequestInvalid, ex.Message)]);
        }
        if (iteration < 0 || attempt <= 0)
            return new AuditCheckPublicationRunResult(true, [new AuditCheckScopeResult(
                AggregateScope, string.Empty, iteration, AuditCheckScopeOutcome.RequestInvalid,
                "Iteration must be >= 0 and attempt must be >= 1.")]);

        var reports = await _auditReports.GetByWorkItemAsync(workItemId, ct).ConfigureAwait(false);
        var iterationReports = reports.Where(r => r.Iteration == iteration).ToList();

        var checkName = string.IsNullOrWhiteSpace(project.AuditChecks.CheckName)
            ? options.CheckName
            : project.AuditChecks.CheckName;
        var repositoryKey = $"{owner}/{repository}";
        var now = _time.GetUtcNow();

        var scopes = BuildScopes(iterationReports, project.Audit.FailingSeverity);
        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await PublishScopeAsync(
                upstream, workItemId, headSha, iteration, attempt, scope,
                repositoryKey, owner, repository, checkName, sourceRevision, detailsUrl, options, now, ct)
                .ConfigureAwait(false));
        }
        return new AuditCheckPublicationRunResult(true, results);
    }

    /// <summary>
    /// Pure scope enumeration + verdict classification. Report-row existence is
    /// the coverage evidence — a report with zero findings still means the
    /// audit ran and passed; only a missing row means <c>NotRun</c>.
    /// </summary>
    public static IReadOnlyList<AuditCheckScope> BuildScopes(
        IReadOnlyList<AuditReport> iterationReports,
        AuditSeverity failingSeverity)
    {
        ArgumentNullException.ThrowIfNull(iterationReports);
        if (iterationReports.Count == 0)
            return [new AuditCheckScope(AggregateScope, AuditTarget.Code, [], AuditCheckVerdict.NotRun,
                AuditCheckUnavailabilityReason.Missing)];

        var scopes = new List<AuditCheckScope>();
        foreach (var targetGroup in iterationReports.GroupBy(r => r.Target.Value).OrderBy(g => g.Key))
        {
            var target = new AuditTarget(targetGroup.Key);
            var aggregateReports = targetGroup.ToList();
            scopes.Add(ClassifyScope(AggregateScope, target, aggregateReports, failingSeverity));
            foreach (var auditorGroup in targetGroup.GroupBy(r => r.AuditorName).OrderBy(g => g.Key))
                scopes.Add(ClassifyScope(auditorGroup.Key, target, auditorGroup.ToList(), failingSeverity));
        }
        return scopes;
    }

    public static AuditCheckScope ClassifyScope(
        string scope,
        AuditTarget target,
        IReadOnlyList<AuditReport> scopeReports,
        AuditSeverity failingSeverity)
    {
        // Worst case comes from the structured findings themselves, not the
        // WorstSeverity display string: the pipeline stores "none" for
        // finding-free reports, which a fail-closed string parser would
        // misread as Error. No findings means nothing reached the threshold.
        var findings = scopeReports.SelectMany(r => r.Findings).ToList();
        var worst = AuditSeverity.Info;
        foreach (var finding in findings)
        {
            var parsed = AuditSeverityParser.Parse(finding.Severity);
            if (parsed > worst)
                worst = parsed;
        }
        var verdict = worst >= failingSeverity ? AuditCheckVerdict.Failed : AuditCheckVerdict.Passed;
        return new AuditCheckScope(scope, target, findings, verdict, null);
    }

    private async Task<AuditCheckScopeResult> PublishScopeAsync(
        IUpstreamRemote upstream,
        string workItemId,
        string headSha,
        int iteration,
        int attempt,
        AuditCheckScope scope,
        string repositoryKey,
        string owner,
        string repository,
        string checkName,
        string? sourceRevision,
        string? detailsUrl,
        AuditCheckPublicationOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var externalId = AuditCheckPayloadBuilder.BuildExternalId(
            workItemId, scope.Target, iteration, attempt, scope.Name, headSha);

        var existing = await _publications.GetAsync(
            repositoryKey, headSha, workItemId, scope.Target.Value, iteration, attempt, scope.Name, ct)
            .ConfigureAwait(false);
        if (existing?.State == AuditCheckPublicationState.Completed)
            return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                AuditCheckScopeOutcome.AlreadyPublished, null, existing.CheckRunId);
        if (existing?.State == AuditCheckPublicationState.Blocked)
            return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                AuditCheckScopeOutcome.Blocked, existing.BlockedReason, existing.CheckRunId);
        if (existing?.State == AuditCheckPublicationState.AwaitingRetry &&
            existing.NextRetryUtc.HasValue && existing.NextRetryUtc.Value > now)
            return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                AuditCheckScopeOutcome.DeferredRetry,
                $"Next retry at {existing.NextRetryUtc.Value:O}.", existing.CheckRunId);

        var request = new AuditCheckPublicationRequest
        {
            Owner = owner,
            Repository = repository,
            HeadSha = headSha,
            WorkItemId = workItemId,
            Target = scope.Target,
            Iteration = iteration,
            Attempt = attempt,
            Scope = scope.Name,
            CheckName = checkName,
            ExternalId = externalId,
            Lifecycle = AuditCheckLifecycle.Completed,
            Verdict = scope.Verdict,
            UnavailabilityReason = scope.UnavailabilityReason,
            Findings = scope.Findings,
            DetailsUrl = detailsUrl,
            SourceRevision = sourceRevision,
        };

        var pending = new AuditCheckPublicationRecord
        {
            Repository = repositoryKey,
            HeadSha = headSha,
            WorkItemId = workItemId,
            Target = scope.Target.Value,
            Iteration = iteration,
            Attempt = attempt,
            Scope = scope.Name,
            CheckName = checkName,
            ExternalId = externalId,
            CheckRunId = existing?.CheckRunId,
            State = AuditCheckPublicationState.Pending,
            TransportAttempts = existing?.TransportAttempts ?? 0,
            UpdatedUtc = now,
        };
        await _publications.UpsertAsync(pending, ct).ConfigureAwait(false);

        try
        {
            var result = await upstream.PublishAuditCheckAsync(request, options, ct).ConfigureAwait(false);
            var completed = pending with
            {
                CheckRunId = result.CheckRunId,
                Status = result.Status,
                Conclusion = result.Conclusion,
                AnnotationsPublished = result.AnnotationsPublished,
                BatchesSent = result.BatchesSent,
                LastBatchUncertain = result.BatchUncertain,
                State = AuditCheckPublicationState.Completed,
                LastError = result.BatchUncertain ? "Annotation batches partially reconciled; see check summary." : null,
                UpdatedUtc = _time.GetUtcNow(),
            };
            if (!await _publications.TryCompleteAsync(completed, ct).ConfigureAwait(false))
                return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                    AuditCheckScopeOutcome.Superseded,
                    "A newer iteration/attempt has since been stored; this completion was not applied.",
                    result.CheckRunId);
            return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                AuditCheckScopeOutcome.Published, null, result.CheckRunId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AuditCheckAuthException ex)
        {
            return await BlockAsync(pending, Truncate($"Auth/permission denied: {ex.Message}"), null, ct)
                .ConfigureAwait(false);
        }
        catch (AuditCheckValidationException ex)
        {
            return await BlockAsync(pending, Truncate($"Request rejected: {ex.Message}"), null, ct)
                .ConfigureAwait(false);
        }
        catch (AuditCheckUnsupportedException ex)
        {
            return await BlockAsync(pending, Truncate($"Forge unsupported: {ex.Message}"), null, ct, AuditCheckScopeOutcome.Unsupported)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuditCheckRateLimitedException or AuditCheckTransientException)
        {
            var attempts = pending.TransportAttempts + 1;
            if (attempts > options.RetryMaxAttempts)
            {
                var reason = options.RequireDelivery
                    ? "Delivery required and transport retries exhausted."
                    : "Transport retries exhausted (delivery not required; audit verdict unaffected).";
                var outcome = options.RequireDelivery
                    ? AuditCheckScopeOutcome.DeliveryRequired
                    : AuditCheckScopeOutcome.Blocked;
                return await BlockAsync(pending with { TransportAttempts = attempts },
                    Truncate($"{reason} Last error: {ex.Message}"), null, ct, outcome)
                    .ConfigureAwait(false);
            }
            var delay = ComputeDelay(ex, attempts, options);
            var retry = pending with
            {
                State = AuditCheckPublicationState.AwaitingRetry,
                TransportAttempts = attempts,
                NextRetryUtc = _time.GetUtcNow().Add(delay),
                LastError = Truncate(ex.Message),
                UpdatedUtc = _time.GetUtcNow(),
            };
            await _publications.UpsertAsync(retry, ct).ConfigureAwait(false);
            _log.LogWarning(
                "Audit check publication for {WorkItem} scope {Scope} failed transiently (attempt {Attempts}); retry in {Delay}s",
                workItemId, scope.Name, attempts, delay.TotalSeconds);
            return new AuditCheckScopeResult(scope.Name, scope.Target.Value, iteration,
                AuditCheckScopeOutcome.RetryScheduled, Truncate(ex.Message));
        }
    }

    private async Task<AuditCheckScopeResult> BlockAsync(
        AuditCheckPublicationRecord pending,
        string reason,
        long? checkRunId,
        CancellationToken ct,
        AuditCheckScopeOutcome outcome = AuditCheckScopeOutcome.Blocked)
    {
        var blocked = pending with
        {
            State = AuditCheckPublicationState.Blocked,
            BlockedReason = reason,
            CheckRunId = checkRunId ?? pending.CheckRunId,
            UpdatedUtc = _time.GetUtcNow(),
        };
        await _publications.UpsertAsync(blocked, ct).ConfigureAwait(false);
        _log.LogWarning("Audit check publication blocked for scope {Scope}: {Reason}", pending.Scope, reason);
        return new AuditCheckScopeResult(pending.Scope, pending.Target, pending.Iteration, outcome, reason, blocked.CheckRunId);
    }

    private TimeSpan ComputeDelay(Exception ex, int attempts, AuditCheckPublicationOptions options)
    {
        var backoff = options.RetryBaseDelay * Math.Pow(2, attempts - 1);
        if (ex is AuditCheckRateLimitedException rateLimited && rateLimited.RetryAfter.HasValue)
            backoff = TimeSpan.FromTicks(Math.Max(backoff.Ticks, rateLimited.RetryAfter.Value.Ticks));
        if (backoff < TimeSpan.Zero)
            backoff = TimeSpan.Zero;
        return backoff > options.RetryMaxDelay ? options.RetryMaxDelay : backoff;
    }

    private static bool TryResolveRepository(
        Project project, out string owner, out string repository, out string? problem)
    {
        owner = string.Empty;
        repository = string.Empty;
        problem = null;
        if (!string.Equals(project.Upstream.Kind, "github", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Project upstream kind '{project.Upstream.Kind}' has no repository identity for check runs.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(project.Upstream.GitHubOwner) ||
            string.IsNullOrWhiteSpace(project.Upstream.GitHubRepository))
        {
            problem = "Project upstream kind 'github' requires GitHubOwner and GitHubRepository.";
            return false;
        }
        owner = project.Upstream.GitHubOwner;
        repository = project.Upstream.GitHubRepository;
        return true;
    }

    private static string Truncate(string value, int max = MaxStoredErrorChars) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

/// <summary>One publishable audit scope: aggregate or per-auditor verdict with its findings.</summary>
public sealed record AuditCheckScope(
    string Name,
    AuditTarget Target,
    IReadOnlyList<AuditReportFinding> Findings,
    AuditCheckVerdict Verdict,
    AuditCheckUnavailabilityReason? UnavailabilityReason);
