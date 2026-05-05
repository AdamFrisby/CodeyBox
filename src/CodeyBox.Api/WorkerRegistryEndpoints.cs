using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Operator-grade introspection: lists currently-registered workers
/// (heartbeating or stale) from the worker registry.
/// </summary>
internal static class WorkerRegistryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/workers", ListWorkersAsync);
    }

    private static async Task<IResult> ListWorkersAsync(IWorkerRegistry registry, CancellationToken ct)
    {
        var workers = await registry.ListAsync(ct);
        return Results.Ok(workers.Select(w => new
        {
            workerId = w.WorkerId,
            hostName = w.HostName,
            processId = w.ProcessId,
            startedAt = w.StartedAt,
            lastHeartbeatAt = w.LastHeartbeatAt,
            currentWorkItemId = w.CurrentWorkItemId,
        }));
    }
}
