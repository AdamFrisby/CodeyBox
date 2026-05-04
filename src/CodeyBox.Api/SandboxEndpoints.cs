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
    /// Operator-triggered dispose of a specific sandbox by name, regardless of
    /// the <c>AutoDispose</c> configuration flag. The sandbox must match the
    /// <c>codeybox-*</c> prefix; requests for other names are rejected with 400
    /// to prevent accidental deletion of non-CodeyBox VMs on the host.
    /// </summary>
    private static async Task<IResult> DisposeLeakedAsync(
        string name,
        ISandboxProvider provider,
        ILogger<Program> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required" });

        // Strict prefix check: only touch VMs we own. Never let an operator
        // accidentally delete a non-CodeyBox VM by passing an arbitrary name.
        if (!name.StartsWith("codeybox-", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "name must start with 'codeybox-'" });

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await provider.DisposeLeakedAsync(name, cts.Token);
            AuditLog.SandboxLeakDisposed(name, ageMinutes: 0, diskMb: null);
            log.LogInformation("SandboxEndpoints: operator-triggered dispose of leaked sandbox {Name}", name);
            return Results.Ok(new { disposed = name });
        }
        catch (OperationCanceledException)
        {
            return Results.Problem("Dispose timed out after 5 minutes", statusCode: 504);
        }
        catch (Exception ex)
        {
            AuditLog.SandboxLeakDisposeFailed(name, ex.Message);
            log.LogWarning(ex, "SandboxEndpoints: failed to dispose leaked sandbox {Name}", name);
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }
}
