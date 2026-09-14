using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Remote executor-host registration surface. Executors connect <b>outbound</b>
/// to the orchestrator (plain HTTPS POSTs, authenticated by the existing
/// <c>ApiKeyAuth</c> bearer middleware) and never expose an inbound listening
/// port, so an executor can sit behind NAT or a host firewall.
///
/// <para>Registration is the executor's assertion of what it can run — a
/// stable host id, sandbox capacity, declared network profiles, and the agent
/// credentials it holds. Rows live in the existing worker registry:
/// heartbeats flow through <c>IWorkerRegistry.HeartbeatAsync</c> and a dead
/// executor is reclaimed by the existing dead-worker reaper path. No second
/// liveness scheme is introduced here.</para>
///
/// <para>Dispatching work to registered executors is a separate item and is
/// not handled here; an executor that registers and heartbeats but is never
/// sent work is the acceptance state.</para>
/// </summary>
internal static class ExecutorEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/executors/register", RegisterAsync);
        app.MapPost("/executors/{hostId}/heartbeat", HeartbeatAsync);
        app.MapPost("/executors/{hostId}/deregister", DeregisterAsync);
        app.MapPost("/executors/{hostId}/quota-reports", ReportQuotaAsync);
    }

    private static async Task<IResult> RegisterAsync(
        ExecutorRegistrationRequest req,
        IWorkerRegistry registry,
        CancellationToken ct)
    {
        if (req is null)
            return Results.BadRequest(new { error = "request body is required" });

        string hostId;
        try
        {
            hostId = NormalizeHostId(req.HostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var capacityError = ValidateCapacity(req.MaxConcurrentSandboxes);
        if (capacityError is not null)
            return Results.BadRequest(new { error = capacityError });

        string[] profiles;
        string[] credentials;
        string[] capabilities;
        try
        {
            profiles = NormalizeEntries(req.AllowedNetworkProfiles, nameof(req.AllowedNetworkProfiles));
            credentials = NormalizeEntries(req.DeclaredCredentials, nameof(req.DeclaredCredentials));
            capabilities = NormalizeEntries(req.DeclaredCapabilities, nameof(req.DeclaredCapabilities));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var now = DateTimeOffset.UtcNow;
        var registration = new WorkerRegistration
        {
            WorkerId = ExecutorRegistration.WorkerIdFor(hostId),
            HostName = hostId,
            ProcessId = req.ProcessId is > 0 ? req.ProcessId.Value : 0,
            StartedAt = now,
            LastHeartbeatAt = now,
            ExecutorHostId = hostId,
            MaxConcurrentSandboxes = req.MaxConcurrentSandboxes,
            ExecutorNetworkProfiles = profiles,
            ExecutorCredentials = credentials,
            ExecutorCapabilities = capabilities,
            Cordoned = req.Cordoned,
            Healthy = req.Healthy ?? true,
        };

        await registry.RegisterAsync(registration, ct);
        return Results.Ok(new
        {
            workerId = registration.WorkerId,
            hostId,
            heartbeatIntervalSeconds = (int)TimeSpan.FromSeconds(15).TotalSeconds,
        });
    }

    private static async Task<IResult> HeartbeatAsync(
        string hostId,
        ExecutorHeartbeatRequest? req,
        IWorkerRegistry registry,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var workerId = ExecutorRegistration.WorkerIdFor(normalized);
        var existing = await registry.ListAsync(ct);
        if (!existing.Any(w => string.Equals(w.WorkerId, workerId, StringComparison.Ordinal)))
            return Results.NotFound(new { error = $"no executor registered for host id '{normalized}'" });

        var currentWorkItemId = string.IsNullOrWhiteSpace(req?.CurrentWorkItemId)
            ? null
            : req!.CurrentWorkItemId.Trim();
        if (currentWorkItemId is not null && currentWorkItemId.Length > 128)
            return Results.BadRequest(new { error = "currentWorkItemId must be at most 128 characters" });

        await registry.HeartbeatAsync(workerId, currentWorkItemId, ct);
        return Results.Ok(new { workerId, lastHeartbeatAt = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> DeregisterAsync(
        string hostId,
        IWorkerRegistry registry,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        await registry.DeregisterAsync(ExecutorRegistration.WorkerIdFor(normalized), ct);
        return Results.Ok(new { hostId = normalized });
    }

    internal static string NormalizeHostId(string? hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("hostId is required");
        var trimmed = hostId.Trim();
        if (trimmed.Length > ExecutorRegistration.MaxHostIdLength)
            throw new ArgumentException($"hostId must be at most {ExecutorRegistration.MaxHostIdLength} characters");
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
                throw new ArgumentException("hostId must not contain control characters");
        }
        return trimmed;
    }

    /// <summary>
    /// Ingests one executor-reported quota reading for a pool whose credential
    /// the reporting host holds. The caller is bound to the claimed host in
    /// three layers: the bearer must be a per-executor token bound to the
    /// path host (a shared bearer such as the operator key proves nothing
    /// about which host is calling, so it is rejected here — see
    /// <c>ApiClientOptions.ExecutorHostId</c>); the host must have a live
    /// worker-registry registration (checked here at ingress — an unregistered
    /// host has no 404-free path to this store); and it must be
    /// operator-declared in the pool's <c>HolderHostIds</c> (checked by the
    /// store, which rejects anything else without mutating the stored
    /// reading). The caller check runs before the registry lookup so a
    /// rejected caller cannot probe which host ids are registered. The store
    /// further validates values and reset consistency. This endpoint accepts
    /// or rejects reports only — admission is decided by the orchestrator's
    /// quota gate, never here.
    /// </summary>
    private static async Task<IResult> ReportQuotaAsync(
        string hostId,
        ExecutorQuotaReportRequest? req,
        ExecutorQuotaReportStore store,
        IWorkerRegistry registry,
        HttpContext httpContext,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = NormalizeHostId(hostId);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (CheckQuotaReportCaller(httpContext, normalized) is { } callerRejection)
            return callerRejection;

        if (req is null)
            return Results.BadRequest(new { error = "request body is required" });

        var workerId = ExecutorRegistration.WorkerIdFor(normalized);
        var workers = await registry.ListAsync(ct);
        if (!workers.Any(w => string.Equals(w.WorkerId, workerId, StringComparison.Ordinal)))
            return Results.NotFound(new { error = $"no executor registered for host id '{normalized}'" });

        QuotaUnknownReason? unknown = null;
        if (!string.IsNullOrWhiteSpace(req.Unknown))
        {
            if (!Enum.TryParse<QuotaUnknownReason>(req.Unknown.Trim(), ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
                return Results.BadRequest(
                    new { error = $"unknown must be one of {string.Join(", ", Enum.GetNames<QuotaUnknownReason>())}" });
            unknown = parsed;
        }

        var report = new ExecutorQuotaReport
        {
            PoolName = req.Pool ?? string.Empty,
            AvailablePct = req.AvailablePct,
            BalanceRemaining = req.BalanceRemaining,
            ResetAt = req.ResetAt,
            ObservedAt = req.ObservedAt ?? default,
            Unknown = unknown,
            Notes = req.Notes,
        };

        if (!store.TryReport(normalized, report, out var rejectionReason))
            return Results.BadRequest(new { error = rejectionReason });

        var pool = QuotaPoolResolver.NormalizePoolName(report.PoolName) ?? string.Empty;
        if (store.TryGetStored(pool, out var stored, out _) && stored?.PoolName is { } storedName)
            pool = storedName;
        return Results.Ok(new { accepted = true, pool });
    }

    /// <summary>
    /// Binds the quota-report path host to the authenticated caller. A
    /// host-bound executor token may report only for its own host; anything
    /// else — a missing principal, a token with no host binding (including
    /// the shared operator key), or a bound token calling for a different
    /// host — is rejected. Returns null when the caller may proceed.
    /// Pure apart from reading the already-authenticated principal.
    /// </summary>
    internal static IResult? CheckQuotaReportCaller(HttpContext httpContext, string normalizedHostId)
    {
        if (!ApiKeyAuth.TryGetPrincipal(httpContext, out var principal) || principal is null)
            return Results.Unauthorized();
        if (ApiKeyAuth.IsAuthenticationDisabled(principal))
            return null;
        if (string.IsNullOrWhiteSpace(principal.ExecutorHostId))
            return Results.Json(
                new { error = "quota reports require a host-bound executor token (CodeyBox:ApiClients ExecutorHostId); shared bearer tokens cannot report readings" },
                statusCode: StatusCodes.Status403Forbidden);
        if (!string.Equals(principal.ExecutorHostId, normalizedHostId, StringComparison.Ordinal))
            return Results.Json(
                new { error = $"this token is bound to executor host '{principal.ExecutorHostId}' and cannot report for host '{normalizedHostId}'" },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    internal static string? ValidateCapacity(int? capacity)
    {
        if (capacity is null)
            return null;
        if (capacity < 0 || capacity > ExecutorRegistration.MaxDeclaredCapacity)
            return $"maxConcurrentSandboxes must be between 0 and {ExecutorRegistration.MaxDeclaredCapacity}";
        return null;
    }

    internal static string[] NormalizeEntries(IReadOnlyList<string>? entries, string fieldName)
    {
        if (entries is null || entries.Count == 0)
            return [];
        if (entries.Count > ExecutorRegistration.MaxDeclaredEntries)
            throw new ArgumentException($"{fieldName} may contain at most {ExecutorRegistration.MaxDeclaredEntries} entries");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(entries.Count);
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException($"{fieldName} entries must be non-empty");
            var trimmed = raw.Trim();
            if (trimmed.Length > ExecutorRegistration.MaxDeclaredEntryLength)
                throw new ArgumentException($"{fieldName} entries must be at most {ExecutorRegistration.MaxDeclaredEntryLength} characters");
            if (trimmed.Any(char.IsControl))
                throw new ArgumentException($"{fieldName} entries must not contain control characters");
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }
        return [.. result];
    }

    /// <summary>Registration assertion posted by an executor host. All bounds enforced before any registry write.</summary>
    public sealed class ExecutorRegistrationRequest
    {
        public string? HostId { get; set; }
        public int? MaxConcurrentSandboxes { get; set; }
        public List<string>? AllowedNetworkProfiles { get; set; }
        public List<string>? DeclaredCredentials { get; set; }
        public List<string>? DeclaredCapabilities { get; set; }
        public bool Cordoned { get; set; }
        public bool? Healthy { get; set; }
        public int? ProcessId { get; set; }
    }

    public sealed class ExecutorHeartbeatRequest
    {
        public string? CurrentWorkItemId { get; set; }
    }

    /// <summary>
    /// One executor-reported quota reading. The pool identity, the availability
    /// reading, the reset time, and the time it was observed travel here; the
    /// orchestrator validates and stores the report and keeps the gate
    /// decision for itself.
    /// </summary>
    public sealed class ExecutorQuotaReportRequest
    {
        public string? Pool { get; set; }
        public double? AvailablePct { get; set; }
        public double? BalanceRemaining { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
        public DateTimeOffset? ObservedAt { get; set; }
        public string? Unknown { get; set; }
        public string? Notes { get; set; }
    }
}
