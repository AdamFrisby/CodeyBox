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
/// using <see cref="ExecutorEligibility"/> (zero-capacity, cordoned and
/// unhealthy hosts register but are never selected). With no executor
/// registered, fall back to the in-process runner with unchanged behaviour.</item>
/// <item>Stage the phase's single bare repo to the executor, run the phase
/// there, and stage the repo back as a tar archive. Only the per-item repo
/// path is ever transferred — never the whole repos root.</item>
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
/// work item as an agent failure. The proxy never touches the work item
/// table — it carries dispatch only; re-dispatch after failure is driven by
/// the existing pipeline state machine with a new attempt.</para>
/// </summary>
public sealed class ExecutorPhaseProxy : IExecutorPhaseRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex PhaseNamePattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int MaxWorkItemIdLength = 128;
    private const int MaxRepositoryIdLength = 256;

    private readonly IWorkerRegistry _registry;
    private readonly IExecutorPhaseTransportFactory _transports;
    private readonly IGitHost _gitHost;
    private readonly IIdempotencyStore _idempotency;
    private readonly IExecutorPhaseRunner _inner;
    private readonly Func<ExecutorPhaseDispatchOptions> _optionsAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorPhaseProxy> _log;

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

        var hostId = await SelectExecutorAsync(ct).ConfigureAwait(false);
        if (hostId is null)
        {
            _log.LogInformation("No executor registered for dispatch {DispatchKey}; falling back to in-process execution", dispatchKey);
            var fallback = await _inner.ExecutePhaseAsync(request, ct).ConfigureAwait(false);
            var validatedFallback = ValidateResult(fallback, options);
            await _idempotency.PutAsync(
                new IdempotencyEntry(dispatchKey, bodyHash, 200, SerializeResult(validatedFallback), "application/json", now + options.IdempotencyTtl),
                ct).ConfigureAwait(false);
            return validatedFallback;
        }

        var result = await ExecuteRemoteAsync(request, hostId, options, ct).ConfigureAwait(false);
        await _idempotency.PutAsync(
            new IdempotencyEntry(dispatchKey, bodyHash, 200, SerializeResult(result), "application/json", now + options.IdempotencyTtl),
            ct).ConfigureAwait(false);
        return result;
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

    private async Task<string?> SelectExecutorAsync(CancellationToken ct)
    {
        var workers = await _registry.ListAsync(ct).ConfigureAwait(false);
        return workers
            .Where(w => w.IsExecutor && w.ExecutorHostId is not null)
            .Select(w => new ExecutorRegistration
            {
                HostId = w.ExecutorHostId!,
                MaxConcurrentSandboxes = w.MaxConcurrentSandboxes,
                AllowedNetworkProfiles = w.ExecutorNetworkProfiles ?? [],
                DeclaredCredentials = w.ExecutorCredentials ?? [],
                Cordoned = w.Cordoned,
                Healthy = w.Healthy,
            })
            .Where(reg => ExecutorEligibility.IsEligibleForPlacement(reg, currentLoad: 0))
            .OrderBy(reg => reg.HostId, StringComparer.Ordinal)
            .Select(reg => reg.HostId)
            .FirstOrDefault();
    }

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
    }

    internal static ExecutorPhaseResult ValidateResult(ExecutorPhaseResult result, ExecutorPhaseDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(result.Outcome))
            throw new ExecutorPhaseException("Executor returned an unknown phase outcome.");
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
