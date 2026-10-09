using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Production orchestrator for provider-neutral external builds. Persists
/// intent before dispatch, reconciles uncertain submissions before any retry
/// (no duplicate paid run after timeout/restart), enforces exclusive
/// ownership with compare-and-set fencing, bounds polling/backoff, treats
/// callbacks as wakeups to authoritative reconciliation, and releases
/// reservations exactly once. Vendor types never appear here.
/// </summary>
public sealed class ExternalBuildService
{
    private readonly IExternalBuildStore _store;
    private readonly IReadOnlyDictionary<string, IExternalBuildProvider> _providers;
    private readonly Func<ExternalBuildOptions> _options;
    private readonly TimeProvider _clock;
    private readonly object _outboxGate = new();
    private readonly List<ExternalBuildCompletionEvent> _outbox = [];
    private readonly HashSet<string> _acked = new(StringComparer.Ordinal);

    public ExternalBuildService(
        IExternalBuildStore store,
        IEnumerable<IExternalBuildProvider> providers,
        Func<ExternalBuildOptions> options,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.ProviderId, p => p, StringComparer.Ordinal);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ExternalBuildRecord> StartAsync(
        ExternalBuildStartRequest request, ExternalBuildSubmitInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        var opts = _options();
        if (!opts.Enabled) throw new ExternalBuildNotEnabledException();
        if (!opts.ApprovedTargets.TryGetValue(request.ApprovedTargetName, out var approval))
            throw new ExternalBuildTargetNotApprovedException(request.ApprovedTargetName);
        if (!_providers.TryGetValue(approval.ProviderId, out var provider))
            throw new ExternalBuildTargetNotApprovedException(request.ApprovedTargetName + " (no provider)");
        if (request.Source is null) throw new ArgumentException("Source identity is required.", nameof(request));

        var target = new ExternalBuildTargetKey
        {
            ProviderId = approval.ProviderId,
            TargetId = approval.TargetId,
            Configuration = approval.Configuration,
        };
        var configDigest = ExternalBuildLifecycle.ComputeConfigDigest(target, request.AdapterParameters);
        var bodyHash = ExternalBuildLifecycle.BodyHash(request, configDigest);
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"ext-{request.WorkItemId}-{request.Phase}-{request.Iteration}-{request.Attempt}-{request.ApprovedTargetName}"
            : request.IdempotencyKey.Trim();

        var existing = await _store.GetByIdempotencyAsync(request.ProjectId, idempotencyKey, bodyHash, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        if (await _store.CountActiveAsync(request.ProjectId, ct).ConfigureAwait(false) >= opts.MaxQueuedPerProject)
            throw new ExternalBuildBudgetExceededException("per-project queued build budget exceeded");
        if (await _store.CountActiveForProviderAsync(approval.ProviderId, ct).ConfigureAwait(false) >= opts.MaxConcurrentPerProvider)
            throw new ExternalBuildBudgetExceededException("per-provider concurrency budget exceeded");

        var now = _clock.GetUtcNow();
        var record = new ExternalBuildRecord
        {
            Id = "xb-" + Guid.NewGuid().ToString("N")[..16],
            ProjectId = request.ProjectId,
            WorkItemId = request.WorkItemId,
            Phase = request.Phase,
            Iteration = request.Iteration,
            Attempt = request.Attempt,
            State = ExternalBuildState.IntentRecorded,
            Target = target,
            Source = request.Source,
            ConfigDigest = configDigest,
            IdempotencyKey = idempotencyKey,
            IdempotencyBodyHash = bodyHash,
            RequestId = Guid.NewGuid().ToString("N"),
            DispatchAttempts = 0,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddSeconds(opts.BuildDeadlineSeconds),
        };
        await _store.CreateAsync(record, ct).ConfigureAwait(false);
        return await DispatchAsync(record, provider, input, approval, ct).ConfigureAwait(false);
    }

    public async Task<ExternalBuildRecord> ReconcileAsync(string buildId, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(buildId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown external build '{buildId}'.");
        var opts = _options();
        if (!_providers.TryGetValue(record.Target.ProviderId, out var provider))
            throw new InvalidOperationException($"No provider '{record.Target.ProviderId}'.");
        var now = _clock.GetUtcNow();

        if (record.State == ExternalBuildState.SubmitUncertain)
        {
            if (record.ProviderRunId is not null)
                return await ObserveProviderAsync(record, provider, ct).ConfigureAwait(false);
            if (record.DispatchAttempts >= opts.MaxDispatchAttempts)
                return await TransitionAsync(record, ExternalBuildState.ReconciliationBlocked,
                    cause: ExternalBuildTerminalCause.ReconciliationImpossible,
                    detail: "retry bound reached while submission stayed uncertain; refusing silent pass", ct).ConfigureAwait(false);
            var probe = await provider.GetStatusAsync("request:" + record.RequestId, ct).ConfigureAwait(false);
            if (probe.Phase != ExternalBuildExecutionPhase.Unknown)
                return await TransitionAsync(record, ExternalBuildState.ReconciliationBlocked,
                    cause: ExternalBuildTerminalCause.ReconciliationImpossible,
                    detail: "provider state ambiguous after uncertain submit; operator adoption required", ct).ConfigureAwait(false);
            var input = new ExternalBuildSubmitInput { Parameters = new Dictionary<string, string>(StringComparer.Ordinal) };
            return await DispatchAsync(record, provider, input, null, ct).ConfigureAwait(false);
        }

        if (record.ProviderRunId is null || ExternalBuildLifecycle.IsTerminal(record.State))
            return record;
        if (record.ExpiresAt.HasValue && now >= record.ExpiresAt.Value)
            return await TransitionAsync(record, ExternalBuildState.Failed,
                cause: ExternalBuildTerminalCause.DeadlineExceeded, detail: "build deadline exceeded", ct).ConfigureAwait(false);
        return await ObserveProviderAsync(record, provider, ct).ConfigureAwait(false);
    }

    public async Task<ExternalBuildRecord> CancelAsync(
        string buildId, ExternalBuildTerminalCause cause, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(buildId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown external build '{buildId}'.");
        if (ExternalBuildLifecycle.IsTerminal(record.State))
            return record;
        if (cause is not (ExternalBuildTerminalCause.UserCancelled or ExternalBuildTerminalCause.ClientDisconnected
            or ExternalBuildTerminalCause.RequestTimeout or ExternalBuildTerminalCause.DeadlineExceeded))
            throw new ArgumentException("Cancel cause must be a cancellation-shaped cause.", nameof(cause));

        var now = _clock.GetUtcNow();
        var cancelled = record with
        {
            State = ExternalBuildState.Cancelled,
            TerminalCause = cause,
            CompletedAt = now,
            UpdatedAt = now,
            FenceOwner = null,
        };
        await _store.UpdateAsync(cancelled, ct).ConfigureAwait(false);
        if (record.ProviderRunId is not null && _providers.TryGetValue(record.Target.ProviderId, out var provider))
        {
            try
            {
                var result = await provider.CancelAsync(record.ProviderRunId, ct).ConfigureAwait(false);
                var reconciled = cancelled with
                {
                    TerminalCause = result.Confirmed
                        ? ExternalBuildTerminalCause.ProviderConfirmedCancellation
                        : cause,
                    FailureDetail = result.Detail,
                    UpdatedAt = _clock.GetUtcNow(),
                };
                await _store.UpdateAsync(reconciled, ct).ConfigureAwait(false);
                return reconciled;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var noted = cancelled with { FailureDetail = "cancel raced completion: " + ex.Message, UpdatedAt = _clock.GetUtcNow() };
                await _store.UpdateAsync(noted, ct).ConfigureAwait(false);
                return await ReconcileAsync(buildId, ct).ConfigureAwait(false);
            }
        }
        return cancelled;
    }

    public async Task<ExternalBuildRecord> AdoptAsync(string buildId, string providerRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        var record = await _store.GetAsync(buildId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown external build '{buildId}'.");
        if (ExternalBuildLifecycle.IsTerminal(record.State))
            throw new ExternalBuildConflictException("Cannot adopt a terminal build.");
        var now = _clock.GetUtcNow();
        var adopted = record with { ProviderRunId = providerRunId, State = ExternalBuildState.Queued, UpdatedAt = now };
        await _store.UpdateAsync(adopted, ct).ConfigureAwait(false);
        return adopted;
    }

    public async Task<string?> HandleCallbackAsync(
        ExternalBuildCallback callback, Func<ExternalBuildCallback, bool> verifySignature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(verifySignature);
        var record = await _store.GetAsync(callback.BuildId, ct).ConfigureAwait(false);
        if (record is null) return "unknown build";
        var error = ExternalBuildCallbackValidator.Validate(
            callback, record.Target.ProviderId, _clock.GetUtcNow(),
            TimeSpan.FromSeconds(_options().MaxCallbackAgeSeconds), verifySignature);
        if (error is not null) return error;
        await ReconcileAsync(record.Id, ct).ConfigureAwait(false);
        return null;
    }

    public IReadOnlyList<ExternalBuildCompletionEvent> DrainOutbox()
    {
        lock (_outboxGate) return _outbox.Where(e => !_acked.Contains(e.BuildId)).ToList();
    }

    public void AckDelivery(string buildId)
    {
        lock (_outboxGate) _acked.Add(buildId);
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var opts = _options();
        await _store.DeleteOlderThanAsync(
            _clock.GetUtcNow().AddDays(-opts.RetentionDays), ct).ConfigureAwait(false);
    }

    private async Task<ExternalBuildRecord> DispatchAsync(
        ExternalBuildRecord record, IExternalBuildProvider provider,
        ExternalBuildSubmitInput input, ExternalBuildTargetApproval? approval, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var dispatched = record with
        {
            State = ExternalBuildState.SubmitUncertain,
            DispatchAttempts = record.DispatchAttempts + 1,
            UpdatedAt = now,
        };
        await _store.UpdateAsync(dispatched, ct).ConfigureAwait(false);
        ExternalBuildSubmitResult result;
        try
        {
            result = await provider.SubmitAsync(dispatched, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failed = dispatched with
            {
                State = ExternalBuildState.SubmitUncertain,
                FailureDetail = "submit threw before acceptance: " + ex.Message,
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _store.UpdateAsync(failed, ct).ConfigureAwait(false);
            return failed;
        }
        if (provider is FakeExternalBuildProviderBase fake && result.ProviderRunId is not null)
            fake.AttachIntent(result.ProviderRunId, dispatched);
        if (!result.Accepted || string.IsNullOrWhiteSpace(result.ProviderRunId))
        {
            var uncertain = dispatched with
            {
                State = ExternalBuildState.SubmitUncertain,
                FailureDetail = result.Error ?? "provider did not accept",
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _store.UpdateAsync(uncertain, ct).ConfigureAwait(false);
            return uncertain;
        }
        var queued = dispatched with
        {
            State = ExternalBuildState.Queued,
            ProviderRunId = result.ProviderRunId,
            FailureDetail = null,
            UpdatedAt = _clock.GetUtcNow(),
        };
        await _store.UpdateAsync(queued, ct).ConfigureAwait(false);
        return queued;
    }

    private async Task<ExternalBuildRecord> ObserveProviderAsync(
        ExternalBuildRecord record, IExternalBuildProvider provider, CancellationToken ct)
    {
        var opts = _options();
        ExternalBuildProviderStatus status;
        try
        {
            status = await provider.GetStatusAsync(record.ProviderRunId!, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var bumped = record with { PollCount = record.PollCount + 1, UpdatedAt = _clock.GetUtcNow() };
            if (bumped.PollCount >= opts.MaxPollAttempts)
                return await TransitionAsync(bumped, ExternalBuildState.ReconciliationBlocked,
                    cause: ExternalBuildTerminalCause.ReconciliationImpossible,
                    detail: "provider unreachable; poll bound reached", ct).ConfigureAwait(false);
            await _store.UpdateAsync(bumped, ct).ConfigureAwait(false);
            return bumped;
        }
        var now = _clock.GetUtcNow();
        switch (status.Phase)
        {
            case ExternalBuildExecutionPhase.Queued:
            case ExternalBuildExecutionPhase.Running:
            {
                var next = record.State switch
                {
                    ExternalBuildState.SubmitUncertain or ExternalBuildState.Queued or ExternalBuildState.Running
                        => ExternalBuildState.Running,
                    _ => record.State,
                };
                if (record.ExpiresAt.HasValue && now >= record.ExpiresAt.Value)
                    return await TransitionAsync(record, ExternalBuildState.Failed,
                        cause: ExternalBuildTerminalCause.DeadlineExceeded, detail: "build deadline exceeded", ct).ConfigureAwait(false);
                var bumped = record with { State = next, PollCount = record.PollCount + 1, UpdatedAt = now };
                await _store.UpdateAsync(bumped, ct).ConfigureAwait(false);
                return bumped;
            }
            case ExternalBuildExecutionPhase.Succeeded:
            case ExternalBuildExecutionPhase.Failed:
            case ExternalBuildExecutionPhase.Cancelled:
            {
                var collecting = record with { State = ExternalBuildState.Collecting, UpdatedAt = now };
                await _store.UpdateAsync(collecting, ct).ConfigureAwait(false);
                var terminal = collecting with
                {
                    State = status.Phase == ExternalBuildExecutionPhase.Succeeded
                        ? ExternalBuildState.Succeeded
                        : status.Phase == ExternalBuildExecutionPhase.Cancelled
                            ? ExternalBuildState.Cancelled : ExternalBuildState.Failed,
                    TerminalCause = status.Phase == ExternalBuildExecutionPhase.Succeeded
                        ? ExternalBuildTerminalCause.ProviderSucceeded
                        : status.Phase == ExternalBuildExecutionPhase.Cancelled
                            ? ExternalBuildTerminalCause.ProviderConfirmedCancellation
                            : ExternalBuildTerminalCause.ProviderFailed,
                    Evidence = status.Evidence,
                    FailureDetail = status.Detail,
                    CompletedAt = now,
                    UpdatedAt = now,
                    FenceOwner = null,
                };
                await _store.UpdateAsync(terminal, ct).ConfigureAwait(false);
                lock (_outboxGate) _outbox.Add(new ExternalBuildCompletionEvent(terminal.Id, terminal.State, now));
                if (provider is FakeExternalBuildProviderBase fake && terminal.ProviderRunId is not null)
                    fake.MarkTerminalDelivered(terminal.ProviderRunId);
                return terminal;
            }
            default:
            {
                var bumped = record with { PollCount = record.PollCount + 1, UpdatedAt = now };
                if (bumped.PollCount >= opts.MaxPollAttempts)
                    return await TransitionAsync(bumped, ExternalBuildState.ReconciliationBlocked,
                        cause: ExternalBuildTerminalCause.ReconciliationImpossible,
                        detail: "provider run unknown; refusing silent pass", ct).ConfigureAwait(false);
                await _store.UpdateAsync(bumped, ct).ConfigureAwait(false);
                return bumped;
            }
        }
    }

    private async Task<ExternalBuildRecord> TransitionAsync(
        ExternalBuildRecord record, ExternalBuildState state,
        ExternalBuildTerminalCause cause, string? detail, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var next = record with
        {
            State = state,
            TerminalCause = cause,
            FailureDetail = detail,
            UpdatedAt = now,
            CompletedAt = ExternalBuildLifecycle.IsTerminal(state) ? now : record.CompletedAt,
            FenceOwner = ExternalBuildLifecycle.IsTerminal(state) ? null : record.FenceOwner,
        };
        await _store.UpdateAsync(next, ct).ConfigureAwait(false);
        if (ExternalBuildLifecycle.IsTerminal(state))
            lock (_outboxGate) _outbox.Add(new ExternalBuildCompletionEvent(next.Id, next.State, now));
        return next;
    }
}

public sealed record ExternalBuildCompletionEvent(string BuildId, ExternalBuildState State, DateTimeOffset At);
