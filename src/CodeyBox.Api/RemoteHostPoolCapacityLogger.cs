using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

internal static class RemoteHostPoolCapacityLogger
{
    public static void Log(
        ISandboxHostPoolSnapshot hostPool,
        OrchestratorOptions orchestratorOptions,
        ILogger startupLog)
    {
        var rows = hostPool.SnapshotHostPool();
        if (rows.Count == 0)
            return;

        var unbounded = rows.Any(r => r.Capacity == int.MaxValue);
        long total = 0;
        if (!unbounded)
        {
            foreach (var row in rows)
                total += row.Capacity;
        }

        var renderedTotal = unbounded ? "unbounded" : total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hostSummary = string.Join(", ", rows.Select(r =>
            $"{r.HostId}={FormatHostCapacity(r.Capacity)}" +
            (r.Cordoned ? ":cordoned" : "") +
            (!r.ConfiguredHealthy ? ":unhealthy" : "")));
        startupLog.LogInformation(
            "Remote sandbox host pool: hosts={HostCount}, configuredCapacity={Capacity}, hosts=[{Hosts}]",
            rows.Count,
            renderedTotal,
            hostSummary);

        var globalCap = Math.Min(orchestratorOptions.MaxConcurrentWorkers, orchestratorOptions.MaxConcurrentSandboxes);
        if (unbounded || total > globalCap)
        {
            startupLog.LogWarning(
                "Remote sandbox host capacity ({HostCapacity}) exceeds global fan-out cap {GlobalCap} " +
                "(min(MaxConcurrentWorkers={MaxWorkers}, MaxConcurrentSandboxes={MaxSandboxes})); excess host capacity will not be used until the global cap is raised",
                renderedTotal,
                globalCap,
                orchestratorOptions.MaxConcurrentWorkers,
                orchestratorOptions.MaxConcurrentSandboxes);
        }
    }

    private static string FormatHostCapacity(int capacity) =>
        capacity == int.MaxValue ? "unbounded" : capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
