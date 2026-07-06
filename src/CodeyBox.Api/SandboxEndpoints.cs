using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class SandboxEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/sandboxes");
        group.MapGet("/leaked", GetLeaked);
        group.MapPost("/leaked/{name}/dispose", DisposeLeakedAsync);
        app.MapGet("/admin/sandbox-leaks", GetSandboxLeakSummary);
    }

    /// <summary>
    /// Returns the list of sandboxes most recently detected as leaked by the
    /// <see cref="SandboxLeakReaper"/> and not yet successfully disposed. The
    /// list is updated after each periodic sweep (default every 15 minutes). An
    /// empty array means no pending leaked sandboxes remain from the last sweep.
    /// </summary>
    private static IResult GetLeaked(SandboxLeakReaper reaper)
    {
        var leaks = reaper.GetLatestLeaks();
        var dto = leaks.Select(ToLeakDto);
        return Results.Ok(dto);
    }

    /// <summary>
    /// Operator-visible summary of leaked sandboxes that are known from the latest
    /// sweep and have not yet been successfully disposed.
    /// </summary>
    private static IResult GetSandboxLeakSummary(SandboxLeakReaper reaper)
    {
        var leaks = reaper.GetLatestLeaks();
        return Results.Ok(new
        {
            count = leaks.Count,
            agesMinutes = leaks.Select(l => Math.Round(l.Age.TotalMinutes, 1)).ToArray(),
            leaks = leaks.Select(ToLeakDto).ToArray(),
        });
    }

    /// <summary>
    /// Operator-triggered dispose of a specific leaked sandbox by name. The sandbox
    /// must be present in the reaper's latest leaked list — this prevents accidental
    /// deletion of an active codeybox-* VM mid-run. Use GET /sandboxes/leaked first
    /// to confirm the sandbox is detected as leaked.
    /// </summary>
    private static async Task<IResult> DisposeLeakedAsync(
        string name,
        IManagedSandboxLifecycle provider,
        SandboxLeakReaper reaper,
        IWebhookDispatcher webhooks,
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

        var diskMb = leak.DiskBytes.HasValue ? leak.DiskBytes.Value / (1024 * 1024) : (long?)null;
        try
        {
            await provider.DisposeLeakedAsync(ToManagedSandboxInfo(leak), cts.Token);
            // Remove from the in-memory list immediately so a repeated call returns 404
            // instead of attempting a redundant multipass delete and returning 500.
            reaper.RemoveFromLatestLeaks(leak);
            var disposedAt = DateTimeOffset.UtcNow;
            AuditLog.SandboxLeakDisposed(name,
                ageMinutes: leak.Age.TotalMinutes,
                diskMb: diskMb,
                disposedAt: disposedAt,
                reason: leak.Reason);
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_disposed",
                Details = new SandboxLeakDetails
                {
                    Name = name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    DisposedAt = disposedAt,
                    Reason = leak.Reason,
                },
            }, ct);
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
            AuditLog.SandboxLeakDisposeFailed(name, leak.Age.TotalMinutes, diskMb, "timeout", leak.Reason);
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = "timeout",
                    Reason = leak.Reason,
                },
            }, ct);
            return Results.Problem("Dispose timed out after 5 minutes", statusCode: 504);
        }
        catch (Exception ex)
        {
            AuditLog.SandboxLeakDisposeFailed(name, leak.Age.TotalMinutes, diskMb, ex.Message, leak.Reason);
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "sandbox.leak_dispose_failed",
                Details = new SandboxLeakDetails
                {
                    Name = name,
                    AgeMinutes = Math.Round(leak.Age.TotalMinutes, 1),
                    DiskMb = diskMb,
                    Error = ex.Message,
                    Reason = leak.Reason,
                },
            }, ct);
            log.LogWarning(ex, "SandboxEndpoints: failed to dispose leaked sandbox {Name}", name);
            // Return a generic message; full details (including multipass stderr) are in the server log.
            return Results.Problem("Dispose failed; see server logs for details", statusCode: 500);
        }
    }

    private static object ToLeakDto(LeakedSandboxInfo l) => new
    {
        name = l.Name,
        createdAt = l.CreatedAt,
        ageMinutes = Math.Round(l.Age.TotalMinutes, 1),
        diskMb = l.DiskBytes.HasValue ? l.DiskBytes.Value / (1024 * 1024) : (long?)null,
        reason = l.Reason,
    };

    private static ManagedSandboxInfo ToManagedSandboxInfo(LeakedSandboxInfo leak)
        => new(
            leak.Name,
            leak.CreatedAt,
            leak.DiskBytes,
            IsTrackedActive: false,
            LifecycleProviderId: leak.LifecycleProviderId);
}
