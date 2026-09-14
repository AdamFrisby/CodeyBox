using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Proxy <see cref="IExecutorPhaseRunner"/> that dispatches a phase to a
/// registered executor host and makes delivery safe to retry.
///
/// <para>Per dispatch, in order:</para>
/// <list type="number">
/// <item>Validate the request bounds and compute the idempotency key (work
/// item + phase + attempt) plus the SHA-256 body hash.</item>
/// <item>Look the key up in <see cref="IIdempotencyStore"/>. A Hit replays the
/// original result without provisioning a second sandbox; a Conflict (same
/// key, different body) throws <see cref="ExecutorPhaseConflictException"/>
/// and never executes.</item>
/// <item>Select a registered executor from <see cref="IWorkerRegistry"/>
/// through the pure <see cref="ExecutorPlacement"/> decider: the phase's
/// required agent credential, required network profile and required
/// capabilities are matched against each host's declared attributes, and
/// cordoned, unhealthy, runtime-backed-off and at-capacity hosts are
/// excluded. The decision — chosen host plus the per-candidate refusal
/// reason — is logged for observability. With no executor registered at all,
/// fall back to the in-process runner with unchanged behaviour.</item>
/// <item>Stage the phase's single bare repo to the executor, run the phase
/// there, and stage the repo back as a tar archive. Only the per-item repo
/// path is ever transferred — never the whole repos root. A transport
/// failure fails over to the next eligible host; only when every eligible
/// host fails does the last host-attributed failure propagate.</item>
/// <item>Validate the staged-back archive (size, entry count, expansion
/// ratio, path containment) and install it over the bare repo. Violations
/// fail the phase without writing to the bare repo and without caching.</item>
/// <item>Cache the result under the dispatch key and return it.</item>
/// </list>
///
/// <para>Failure taxonomy: an agent failure on the executor is returned as a
/// result with <see cref="ExecutorPhaseOutcome.AgentFailed"/>; a host,
/// connection or transfer problem throws
/// <see cref="ExecutorPhaseTransportException"/> and stores nothing, so an
/// unreachable host is retried elsewhere rather than charged against the
/// work item as an agent failure. A host that declared a credential it does
/// not actually hold surfaces the same way — as a host-attributed transport
/// failure with failover to another eligible host — never as an agent
/// failure. When hosts are registered but none is currently eligible, the
/// dispatch throws <see cref="SandboxProvisioningDeferredException"/> with
/// the configured placement backoff so the work item is requeued rather than
/// failed; when no registered host provides a required capability, it throws
/// <see cref="ExecutorPlacementUnplaceableException"/> naming the unmet tag
/// instead of dispatching and failing. Neither path returns
/// <see cref="ExecutorPhaseOutcome.AgentFailed"/>, so neither consumes a
/// rework iteration. The proxy never touches the work item table — it
/// carries dispatch only; re-dispatch after failure is driven by the
/// existing pipeline state machine with a new attempt.</para>
/// </summary>
public sealed class ExecutorPhaseProxy : IExecutorPhaseRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex PhaseNamePattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaxWorkItemIdLength = 128;
    private const int MaxRepositoryIdLength = 256;

    /// <summary>
    /// Prefix an executor-side handler uses to report that the host cannot
    /// satisfy the dispatch's required credential (for example the credential
    /// file is absent although the host declared it). An
    /// <see cref="ExecutorPhaseOutcome.AgentFailed"/> result whose
    /// <c>ErrorMessage</c> starts with this prefix (ordinal) is reinterpreted
    /// by the proxy as a host-attributed transport failure: the host is
    /// marked runtime-unhealthy and the phase fails over to the next eligible
    /// host instead of being charged to the work item as an agent failure.
    /// Executor-side code that detects a missing credential directly must
    /// throw <see cref="ExecutorPhaseTransportException"/> instead; the
    /// prefix exists for transports that can only surface the condition as
    /// result text.
    /// </summary>
    public const string CredentialMissingErrorPrefix = "executor-credential-missing:";

    private readonly IWorkerRegistry _registry;
    private readonly IExecutorPhaseTransportFactory _transports;
    private readonly IGitHost _gitHost;
    private readonly IIdempotencyStore _idempotency;
    private readonly IExecutorPhaseRunner _inner;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorPhaseProxy> _log;

    private readonly ConcurrentDictionary<string, int> _inflightByHost = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeUnhealthyState> _runtimeUnhealthy = new(StringComparer.Ordinal);
    private readonly object _runtimeUnhealthyLock = new();

    public ExecutorPhaseProxy(
        IWorkerRegistry registry,
        IExecutorPhaseTransportFactory transports,
        IGitHost gitHost,
        IIdempotencyStore idempotency,
        IExecutorPhaseRunner inner,
        Func<ExecutorPhaseDispatchOptions> optionsAccessor,
        TimeProvider? clock = null,
        ILogger<ExecutorPhaseProxy>? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExecutorPhaseProxy>.Instance;
    }

    public async Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _optionsAccessor();
        options.Validate();
        ValidateRequest(request, options);

        var dispatchKey = BuildDispatchKey(request);
        var bodyHash = ComputeBodyHash(request);
        var now = _clock.GetUtcNow();

        var lookup = await _idempotency.LookupAsync(dispatchKey, bodyHash, now, ct).ConfigureAwait(false);
        switch (lookup.Outcome)
        {
            case IdempotencyLookupOutcome.Hit:
                _log.LogInformation("Executor phase dispatch {DispatchKey} redelivered; replaying original result", dispatchKey);
                return ValidateResult(DeserializeCachedResult(dispatchKey, lookup.Entry!), options);
            case IdempotencyLookupOutcome.Conflict:
                throw new ExecutorPhaseConflictException(dispatchKey);
            case IdempotencyLookupOutcome.Miss:
                break;
            default:
                throw new InvalidOperationException($"Unknown idempotency outcome {(int)lookup.Outcome}.");
        }

        var hostIds = await SelectExecutorChainAsync(request, options, ct).ConfigureAwait(false);
        if (hostIds is null)
        {
            _log.LogInformation("No executor registered for dispatch {DispatchKey}; falling back to in-process execution", dispatchKey);
            var fallback = await _inner.ExecutePhaseAsync(request, ct).ConfigureAwait(false);
            var validatedFallback = ValidateResult(fallback, options);
            await _idempotency.PutAsync(
                new IdempotencyEntry(dispatchKey, bodyHash, 200, SerializeResult(validatedFallback), "application/json", now + options.IdempotencyTtl),
                ct).ConfigureAwait(false);
            return validatedFallback;
        }

        var result = await ExecuteRemoteWithFailoverAsync(request, hostIds, options, dispatchKey, ct).ConfigureAwait(false);
        await _idempotency.PutAsync(
            new IdempotencyEntry(dispatchKey, bodyHash, 200, SerializeResult(result), "application/json", now + options.IdempotencyTtl),
            ct).ConfigureAwait(false);
        return result;
    }

    private async Task<ExecutorPhaseResult> ExecuteRemoteWithFailoverAsync(
        ExecutorPhaseRequest request,
        IReadOnlyList<string> hostIds,
        ExecutorPhaseDispatchOptions options,
        string dispatchKey,
        CancellationToken ct)
    {
        ExecutorPhaseTransportException? lastTransport = null;
        foreach (var hostId in hostIds)
        {
            ct.ThrowIfCancellationRequested();
            TrackDispatchStart(hostId);
            try
            {
                var result = await ExecuteRemoteAsync(request, hostId, options, ct).ConfigureAwait(false);
                return result;
            }
            catch (ExecutorPhaseTransportException ex)
            {
                lastTransport = ex;
                MarkRuntimeUnhealthy(ex.HostId, ex.Message, options.RuntimeUnhealthyBackoff);
                if (hostIds.Count > 1)
                {
                    _log.LogWarning(
                        "Executor phase dispatch {DispatchKey} failed on host {HostId} ({Operation}); failing over to another eligible host",
                        dispatchKey, ex.HostId, ex.Operation);
                }
            }
            finally
            {
                TrackDispatchEnd(hostId);
            }
        }

        throw lastTransport
            ?? new ExecutorPhaseTransportException("(unknown)", "placement", "No eligible executor host was attempted.");
    }

    private async Task<ExecutorPhaseResult> ExecuteRemoteAsync(
        ExecutorPhaseRequest request,
        string hostId,
        ExecutorPhaseDispatchOptions options,
        CancellationToken ct)
    {
        var transport = await ResolveTransportAsync(hostId, ct).ConfigureAwait(false);

        string repoPath;
        try
        {
            repoPath = _gitHost.GetRepoPath(request.RepositoryId);
        }
        catch (NotSupportedException ex)
        {
            throw new ExecutorPhaseTransportException(hostId, "resolve-repo", "Git host exposes no local repository path.", ex);
        }
        var canonicalRepo = CanonicalizeRepoPath(repoPath, _gitHost.RepositoriesRootDirectory, request.RepositoryId);
        if (!Directory.Exists(canonicalRepo))
            throw new ExecutorPhaseException($"Bare repo for '{request.RepositoryId}' does not exist.");

        await CallTransportAsync(hostId, "stage-in", token => transport.StageInAsync(canonicalRepo, token), ct).ConfigureAwait(false);

        var raw = await CallTransportAsync(hostId, "run-phase", token => transport.RunPhaseAsync(request, token), ct).ConfigureAwait(false);
        var result = ValidateResult(raw, options);
        ThrowIfCredentialMissingResult(hostId, result, request);

        var scratchRoot = Path.Combine(Path.GetTempPath(), "codeybox-executor-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        try
        {
            var archivePath = Path.Combine(scratchRoot, "stageout.tar");
            await CallTransportAsync(hostId, "stage-out", token => transport.StageOutToArchiveAsync(archivePath, options.StageOutMaxArchiveBytes, token), ct).ConfigureAwait(false);
            await ExecutorStageOutValidator.ValidateAndInstallAsync(archivePath, canonicalRepo, scratchRoot, options, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true); }
            catch { }
        }

        _log.LogInformation("Executor phase dispatch for host {HostId} completed with outcome {Outcome}", hostId, result.Outcome);
        return result;
    }

    private async Task<IExecutorPhaseTransport> ResolveTransportAsync(string hostId, CancellationToken ct)
    {
        IExecutorPhaseTransport? transport;
        try
        {
            transport = await _transports.ResolveAsync(hostId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExecutorPhaseTransportException(hostId, "resolve-transport", ex.Message, ex);
        }

        if (transport is null)
            throw new ExecutorPhaseTransportException(hostId, "resolve-transport", "No dispatch transport is configured for this host.");
        return transport;
    }

    private static async Task<T> CallTransportAsync<T>(
        string hostId,
        string operation,
        Func<CancellationToken, Task<T>> call,
        CancellationToken ct)
    {
        try
        {
            return await call(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExecutorPhaseTransportException)
        {
            throw;
        }
        catch (ExecutorPhaseException)
        {
            // A stage-out transport that enforces the archive cap (or any
            // phase-side rejection raised mid-transfer) reports a phase
            // failure, not a host failure: the host was reachable. Let it
            // through unwrapped so it is not charged as a transport failure.
            throw;
        }
        catch (Exception ex)
        {
            throw new ExecutorPhaseTransportException(hostId, operation, ex.Message, ex);
        }
    }

    private static Task CallTransportAsync(
        string hostId,
        string operation,
        Func<CancellationToken, Task> call,
        CancellationToken ct) =>
        CallTransportAsync<object?>(hostId, operation, async token => { await call(token).ConfigureAwait(false); return null; }, ct);

    private static void ThrowIfCredentialMissingResult(
        string hostId,
        ExecutorPhaseResult result,
        ExecutorPhaseRequest request)
    {
        if (result.Outcome != ExecutorPhaseOutcome.AgentFailed)
            return;
        if (string.IsNullOrEmpty(result.ErrorMessage)
            || !result.ErrorMessage.StartsWith(CredentialMissingErrorPrefix, StringComparison.Ordinal))
            return;
        var credential = string.IsNullOrWhiteSpace(request.RequiredCredential)
            ? "required"
            : $"'{request.RequiredCredential.Trim()}'";
        throw new ExecutorPhaseTransportException(
            hostId,
            "run-phase",
            $"Host declared credential {credential} but cannot satisfy it: {TruncateForLog(result.ErrorMessage)}");
    }

    private static string TruncateForLog(string value, int maxLength = 256)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..maxLength] + "…";
    }

    private async Task<IReadOnlyList<string>?> SelectExecutorChainAsync(
        ExecutorPhaseRequest request,
        ExecutorPhaseDispatchOptions options,
        CancellationToken ct)
    {
        var workers = await _registry.ListAsync(ct).ConfigureAwait(false);
        var hosts = workers
            .Where(w => w.IsExecutor && w.ExecutorHostId is not null)
            .Select(w => new ExecutorRegistration
            {
                HostId = w.ExecutorHostId!,
                MaxConcurrentSandboxes = w.MaxConcurrentSandboxes,
                AllowedNetworkProfiles = w.ExecutorNetworkProfiles ?? [],
                DeclaredCredentials = w.ExecutorCredentials ?? [],
                DeclaredCapabilities = w.ExecutorCapabilities ?? [],
                Cordoned = w.Cordoned,
                Healthy = w.Healthy,
            })
            .ToList();

        if (hosts.Count == 0)
            return null;

        var requirements = ExecutorPlacementRequirements.FromRequest(request);
        var loads = BuildLoads(workers);
        var now = _clock.GetUtcNow();
        var backedOff = SnapshotRuntimeUnhealthy(now, pruneExpired: true);
        var decision = ExecutorPlacement.Decide(hosts, requirements, loads, backedOff);

        if (decision.SelectedHostId is not null)
        {
            _log.LogInformation(
                "Executor phase dispatch for work item {WorkItemId} phase {Phase} placed on host {HostId}: {Decision}",
                request.WorkItemId, request.Phase, decision.SelectedHostId, decision.Describe());
            var chain = new List<string>(capacity: decision.Candidates.Count);
            chain.Add(decision.SelectedHostId);
            foreach (var candidate in decision.Candidates)
            {
                if (candidate.Eligible
                    && !string.Equals(candidate.HostId, decision.SelectedHostId, StringComparison.Ordinal))
                    chain.Add(candidate.HostId);
            }
            return chain;
        }

        if (decision.UnmetCapability is not null)
        {
            _log.LogWarning(
                "Executor phase dispatch for work item {WorkItemId} phase {Phase} is unplaceable: {Decision}",
                request.WorkItemId, request.Phase, decision.Describe());
            throw new ExecutorPlacementUnplaceableException(
                decision.UnmetCapability,
                $"workItem={request.WorkItemId} phase={request.Phase}; candidates=[{string.Join(", ", decision.Candidates.Select(c => $"{c.HostId}={c.Reason}"))}]",
                decision);
        }

        _log.LogWarning(
            "Executor phase dispatch for work item {WorkItemId} phase {Phase} deferred: {Decision}",
            request.WorkItemId, request.Phase, decision.Describe());
        throw new SandboxProvisioningDeferredException(
            provider: "executor",
            operation: "placement",
            errorClass: "no-eligible-host",
            detail: $"workItem={request.WorkItemId} phase={request.Phase}; hosts=[{string.Join(", ", decision.Candidates.Select(c => $"{c.HostId}={c.Reason}"))}]",
            recheckIn: options.PlacementRecheckIn);
    }

    private IReadOnlyDictionary<string, int> BuildLoads(IReadOnlyList<WorkerRegistration> workers)
    {
        var loads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            if (!worker.IsExecutor || worker.ExecutorHostId is null)
                continue;
            var baseLoad = string.IsNullOrWhiteSpace(worker.CurrentWorkItemId) ? 0 : 1;
            var inflight = _inflightByHost.TryGetValue(worker.ExecutorHostId, out var active) ? Math.Max(0, active) : 0;
            loads[worker.ExecutorHostId] = baseLoad + inflight;
        }
        foreach (var (hostId, active) in _inflightByHost)
        {
            if (!loads.ContainsKey(hostId) && active > 0)
                loads[hostId] = Math.Max(0, active);
        }
        return loads;
    }

    private void TrackDispatchStart(string hostId) =>
        _inflightByHost.AddOrUpdate(hostId, 1, (_, current) => current + 1);

    private void TrackDispatchEnd(string hostId)
    {
        _inflightByHost.AddOrUpdate(hostId, 0, (_, current) => Math.Max(0, current - 1));
        if (_inflightByHost.TryGetValue(hostId, out var current) && current <= 0)
            _inflightByHost.TryRemove(hostId, out _);
    }

    private void MarkRuntimeUnhealthy(string hostId, string reason, TimeSpan backoff)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            return;
        var until = _clock.GetUtcNow() + (backoff > TimeSpan.Zero ? backoff : TimeSpan.FromMinutes(1));
        lock (_runtimeUnhealthyLock)
        {
            _runtimeUnhealthy[hostId] = new RuntimeUnhealthyState(until, reason);
        }
        _log.LogWarning(
            "Executor host {HostId} marked runtime-unhealthy until {Until:O}: {Reason}",
            hostId, until, TruncateForLog(reason));
    }

    private HashSet<string> SnapshotRuntimeUnhealthy(DateTimeOffset now, bool pruneExpired)
    {
        lock (_runtimeUnhealthyLock)
        {
            if (pruneExpired)
            {
                foreach (var (hostId, state) in _runtimeUnhealthy.ToArray())
                {
                    if (state.Until <= now)
                        _runtimeUnhealthy.Remove(hostId);
                }
            }
            return new HashSet<string>(
                _runtimeUnhealthy.Where(kv => kv.Value.Until > now).Select(kv => kv.Key),
                StringComparer.Ordinal);
        }
    }

    private sealed record RuntimeUnhealthyState(DateTimeOffset Until, string Reason);

    internal static string BuildDispatchKey(ExecutorPhaseRequest request) =>
        $"executor-phase/v1/{request.WorkItemId}/{request.Phase}/{request.Attempt}";

    internal static string ComputeBodyHash(ExecutorPhaseRequest request)
    {
        static void WriteField(SHA256 sha, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var lengthPrefix = Encoding.UTF8.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
            sha.TransformBlock(lengthPrefix, 0, lengthPrefix.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        using var sha = SHA256.Create();
        WriteField(sha, request.WorkItemId);
        WriteField(sha, request.Phase);
        WriteField(sha, request.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteField(sha, request.RepositoryId);
        WriteField(sha, request.PayloadJson ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(request.RequiredCredential)
            || !string.IsNullOrWhiteSpace(request.RequiredNetworkProfile)
            || (request.RequiredCapabilities is { Count: > 0 }))
        {
            WriteField(sha, "placement/v1");
            WriteField(sha, request.RequiredCredential?.Trim() ?? string.Empty);
            WriteField(sha, request.RequiredNetworkProfile?.Trim() ?? string.Empty);
            foreach (var tag in request.RequiredCapabilities ?? [])
                WriteField(sha, tag?.Trim() ?? string.Empty);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    internal static void ValidateRequest(ExecutorPhaseRequest request, ExecutorPhaseDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(request.WorkItemId) || request.WorkItemId.Length > MaxWorkItemIdLength)
            throw new ArgumentException($"WorkItemId must be 1..{MaxWorkItemIdLength} chars.", nameof(request));
        if (request.Phase is null || !PhaseNamePattern.IsMatch(request.Phase))
            throw new ArgumentException("Phase must match [A-Za-z0-9_-]{1,64}.", nameof(request));
        if (request.Attempt < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Attempt must be zero or positive.");
        if (string.IsNullOrWhiteSpace(request.RepositoryId) || request.RepositoryId.Length > MaxRepositoryIdLength)
            throw new ArgumentException($"RepositoryId must be 1..{MaxRepositoryIdLength} chars.", nameof(request));
        var payloadBytes = Encoding.UTF8.GetByteCount(request.PayloadJson ?? string.Empty);
        if (payloadBytes > options.MaxRequestPayloadBytes)
            throw new ArgumentException($"PayloadJson exceeds MaxRequestPayloadBytes={options.MaxRequestPayloadBytes}.", nameof(request));
        ExecutorPlacementRequirements.FromRequest(request);
    }

    internal static ExecutorPhaseResult ValidateResult(ExecutorPhaseResult result, ExecutorPhaseDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(result.Outcome))
            throw new ExecutorPhaseException("Executor returned an unknown phase outcome.");
        if (result.Findings is null)
            throw new ExecutorPhaseException("Executor returned no findings collection.");
        if (result.Findings.Count > options.MaxResultFindings)
            throw new ExecutorPhaseException($"Executor returned {result.Findings.Count} findings, exceeding MaxResultFindings={options.MaxResultFindings}.");
        foreach (var finding in result.Findings)
        {
            if (finding is null || finding.Length > options.MaxFindingLengthChars)
                throw new ExecutorPhaseException($"Executor returned an oversized finding (limit {options.MaxFindingLengthChars} chars).");
        }
        if (!string.IsNullOrEmpty(result.CommitSha) && !IsHexSha(result.CommitSha))
            throw new ExecutorPhaseException("Executor returned a malformed commit sha.");
        if (result.Usage is null)
            throw new ExecutorPhaseException("Executor returned no usage.");
        if (result.Usage.InputTokens < 0 || result.Usage.OutputTokens < 0 || result.Usage.CostUsd < 0)
            throw new ExecutorPhaseException("Executor returned negative usage.");
        if (result.ErrorMessage is not null && result.ErrorMessage.Length > options.MaxResultErrorLengthChars)
            throw new ExecutorPhaseException($"Executor returned an oversized error message (limit {options.MaxResultErrorLengthChars} chars).");
        return result;
    }

    internal static string CanonicalizeRepoPath(string repoPath, string rootDirectory, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ExecutorPhaseException($"Bare repo path for '{repositoryId}' is empty.");
        string canonical;
        try
        {
            canonical = Path.GetFullPath(repoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExecutorPhaseException($"Bare repo path for '{repositoryId}' is not a valid path.", ex);
        }

        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            string canonicalRoot;
            try
            {
                canonicalRoot = Path.GetFullPath(rootDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ExecutorPhaseException("Repositories root directory is not a valid path.", ex);
            }

            var prefix = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(prefix, StringComparison.Ordinal))
                throw new ExecutorPhaseException($"Bare repo path for '{repositoryId}' escapes the repositories root.");
        }

        return canonical;
    }

    private static byte[] SerializeResult(ExecutorPhaseResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);

    private static ExecutorPhaseResult DeserializeCachedResult(string dispatchKey, IdempotencyEntry entry)
    {
        ExecutorPhaseResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ExecutorPhaseResult>(entry.ResponseBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ExecutorPhaseException($"Cached dispatch result for '{dispatchKey}' is corrupt.", ex);
        }

        if (result is null)
            throw new ExecutorPhaseException($"Cached dispatch result for '{dispatchKey}' is corrupt.");
        return result;
    }

    private static bool IsHexSha(string value)
    {
        if (value.Length is not (40 or 64))
            return false;
        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }
        return true;
    }
}
