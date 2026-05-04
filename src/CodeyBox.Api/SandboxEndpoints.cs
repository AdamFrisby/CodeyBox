using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class SandboxEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/sandboxes");
        group.MapGet("/leaked", GetLeakedAsync);
        group.MapPost("/leaked/{name}/dispose", DisposeLeakedAsync);
    }

    /// <summary>
    /// Returns the list of sandboxes most recently detected as leaked by the
    /// <see cref="SandboxLeakReaper"/>. The list is updated after each periodic
    /// sweep (default every 15 minutes). An empty array means no leaks were
    /// detected on the last sweep, not that no leaks exist.
    /// </summary>
    private static IResult GetLeakedAsync(SandboxLeakReaper reaper)
    {
        var leaks = reaper.GetLatestLeaks();
        var dto = leaks.Select(l => new
        {
            name = l.Name,
            createdAt = l.CreatedAt,
            ageMinutes = Math.Round(l.Age.TotalMinutes, 1),
            diskMb = l.DiskBytes.HasValue ? l.DiskBytes.Value / (1024 * 1024) : (long?)null,
        });
        return Results.Ok(dto);
    }

    /// <summary>
    /// Operator-triggered dispose of a specific leaked sandbox by name. The sandbox
    /// must be present in the reaper's latest leaked list — this prevents accidental
    /// deletion of an active codeybox-* VM mid-run. Use GET /sandboxes/leaked first
    /// to confirm the sandbox is detected as leaked.
    /// </summary>
    private static async Task<IResult> DisposeLeakedAsync(
        string name,
        ISandboxProvider provider,
        SandboxLeakReaper reaper,
        ILogger<Program> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required" });

        // Strict prefix check: only touch VMs we own.
        if (!name.StartsWith("codeybox-", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "name must start with 'codeybox-'" });

        // Cross-check against the latest leak list so that active sandboxes (those
        // tied to a running work item) cannot be purged via this endpoint.
        var leak = reaper.GetLatestLeaks().FirstOrDefault(l => l.Name == name);
        if (leak is null)
            return Results.NotFound(new { error = "sandbox not found in latest leaked list; verify via GET /sandboxes/leaked" });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await provider.DisposeLeakedAsync(name, cts.Token);
            AuditLog.SandboxLeakDisposed(name,
                ageMinutes: leak.Age.TotalMinutes,
                diskMb: leak.DiskBytes.HasValue ? leak.DiskBytes.Value / (1024 * 1024) : null);
            log.LogInformation("SandboxEndpoints: operator-triggered dispose of leaked sandbox {Name}", name);
            return Results.Ok(new { disposed = name });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected before dispose completed — log server-side; no response possible.
            log.LogInformation("SandboxEndpoints: client disconnected while disposing {Name}", name);
            return Results.StatusCode(499);
        }
        catch (OperationCanceledException)
        {
            // The 5-minute per-disposal timeout fired.
            return Results.Problem("Dispose timed out after 5 minutes", statusCode: 504);
        }
        catch (Exception ex)
        {
            AuditLog.SandboxLeakDisposeFailed(name, ex.Message);
            log.LogWarning(ex, "SandboxEndpoints: failed to dispose leaked sandbox {Name}", name);
            // Return a generic message; full details (including multipass stderr) are in the server log.
            return Results.Problem("Dispose failed; see server logs for details", statusCode: 500);
        }
    }
}
