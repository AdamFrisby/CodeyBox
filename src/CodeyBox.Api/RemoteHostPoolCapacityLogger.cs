using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Startup reporter for the remote executor host pool behind an
/// <see cref="ISandboxHostPoolSnapshot"/> provider. The process-wide sandbox
/// ceiling is <b>derived</b> as the sum of host capacities — capacity
/// accounting lives on the members (one admission gate per
/// <see cref="SandboxMember"/>), so there is no independently-imposed global
/// scalar left to clamp host capacity against and no "excess host capacity
/// will not be used" warning to emit. Each host row reports its headroom
/// (capacity minus live reservations) so operators see per-member slack.
/// </summary>
internal static class RemoteHostPoolCapacityLogger
{
    public static void Log(
        ISandboxHostPoolSnapshot hostPool,
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
            $"{r.HostId}={FormatHostCapacity(r.Capacity)}/headroom={FormatHeadroom(r)}" +
            (r.Cordoned ? ":cordoned" : "") +
            (!r.ConfiguredHealthy ? ":unhealthy" : "")));
        startupLog.LogInformation(
            "Remote sandbox host pool: hosts={HostCount}, derivedCapacity={Capacity}, hosts=[{Hosts}]",
            rows.Count,
            renderedTotal,
            hostSummary);
    }

    private static string FormatHostCapacity(int capacity) =>
        capacity == int.MaxValue ? "unbounded" : capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatHeadroom(SandboxHostPoolEntry row)
    {
        if (row.Capacity == int.MaxValue)
            return "unbounded";
        var headroom = Math.Max(0, row.Capacity - Math.Max(0, row.Reserved));
        return headroom.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
