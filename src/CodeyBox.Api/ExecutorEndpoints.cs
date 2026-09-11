using CodeyBox.Core;

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
        try
        {
            profiles = NormalizeEntries(req.AllowedNetworkProfiles, nameof(req.AllowedNetworkProfiles));
            credentials = NormalizeEntries(req.DeclaredCredentials, nameof(req.DeclaredCredentials));
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
        public bool Cordoned { get; set; }
        public bool? Healthy { get; set; }
        public int? ProcessId { get; set; }
    }

    public sealed class ExecutorHeartbeatRequest
    {
        public string? CurrentWorkItemId { get; set; }
    }
}
