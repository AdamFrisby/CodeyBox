using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A live worker-side signal that should count as watchdog progress even when
/// the work item row and agent stream files are quiet.
/// </summary>
public sealed record WorkerProgressActivity(string Reason);

/// <summary>
/// Observes whether a worker still appears to be doing item-owned work.
/// Implementations must be best-effort: probe failures should return
/// <c>null</c> so the watchdog can still use durable progress signals.
/// </summary>
public interface IWorkerProgressActivitySource
{
    ValueTask<WorkerProgressActivity?> ObserveAsync(
        WorkerRegistration worker,
        WorkItemId itemId,
        WorkerProgressWatchdogOptions opts,
        CancellationToken ct);
}

/// <summary>
/// Default watchdog activity source. It combines exact host-side process CPU
/// sampling for sandbox processes that carry <see cref="WorkItemIdEnvironmentVariable"/>
/// with provider-owned active sandbox snapshots for VM-backed providers whose
/// guest processes are not visible from host <c>/proc</c>.
/// </summary>
public sealed class DefaultWorkerProgressActivitySource : IWorkerProgressActivitySource
{
    public const string WorkItemIdEnvironmentVariable = "CODEYBOX_WORK_ITEM_ID";

    private const int MaxAncestorWalk = 32;
    private readonly ISandboxProvider? _sandboxProvider;
    private readonly ConcurrentDictionary<WorkItemId, ProcessCpuSample> _processSamples = new();

    public DefaultWorkerProgressActivitySource(ISandboxProvider? sandboxProvider = null)
        => _sandboxProvider = sandboxProvider;

    public ValueTask<WorkerProgressActivity?> ObserveAsync(
        WorkerRegistration worker,
        WorkItemId itemId,
        WorkerProgressWatchdogOptions opts,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(worker.CurrentWorkItemId, itemId.ToString(), StringComparison.Ordinal))
            return ValueTask.FromResult<WorkerProgressActivity?>(null);

        if (opts.ActiveSandboxProgressSignalEnabled
            && IsOwnedByActiveSandbox(itemId))
        {
            return ValueTask.FromResult<WorkerProgressActivity?>(
                new WorkerProgressActivity("active-sandbox"));
        }

        if (opts.ProcessCpuProgressSignalEnabled
            && TryObserveProcessCpu(itemId, out var reason))
        {
            return ValueTask.FromResult<WorkerProgressActivity?>(
                new WorkerProgressActivity(reason));
        }

        return ValueTask.FromResult<WorkerProgressActivity?>(null);
    }

    private bool IsOwnedByActiveSandbox(WorkItemId itemId)
    {
        if (_sandboxProvider is not IActiveSandboxProvider activeProvider)
            return false;

        try
        {
            return activeProvider.SnapshotActiveSandboxes()
                .Any(entry => entry.WorkItemId == itemId);
        }
        catch
        {
            return false;
        }
    }

    private bool TryObserveProcessCpu(WorkItemId itemId, out string reason)
    {
        reason = "";
        if (!OperatingSystem.IsLinux())
            return false;

        if (!TryReadWorkItemCpuTicks(itemId, out var sample))
        {
            _processSamples.TryRemove(itemId, out _);
            return false;
        }

        if (!_processSamples.TryGetValue(itemId, out var previous))
        {
            _processSamples[itemId] = sample;
            reason = "process-observed";
            return true;
        }

        _processSamples[itemId] = sample;
        if (sample.CpuTicks > previous.CpuTicks)
        {
            reason = "process-cpu";
            return true;
        }

        return false;
    }

    private static bool TryReadWorkItemCpuTicks(WorkItemId itemId, out ProcessCpuSample sample)
    {
        sample = default;

        long totalCpuTicks = 0;
        var processCount = 0;
        var ownPid = Environment.ProcessId;
        var envEntry = $"{WorkItemIdEnvironmentVariable}={itemId}";

        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out var pid))
                    continue;
                if (pid == ownPid)
                    continue;

                string stat;
                try { stat = File.ReadAllText(Path.Combine(procDir, "stat")); }
                catch { continue; }

                if (!TryParseStat(stat, out var cpuTicks, out _))
                    continue;
                if (!IsDescendantOf(pid, ownPid))
                    continue;
                if (!ProcessEnvironmentContains(procDir, envEntry))
                    continue;

                totalCpuTicks += cpuTicks;
                processCount++;
            }
        }
        catch
        {
            return false;
        }

        if (processCount == 0)
            return false;

        sample = new ProcessCpuSample(totalCpuTicks, processCount);
        return true;
    }

    private static bool ProcessEnvironmentContains(string procDir, string envEntry)
    {
        string environ;
        try { environ = File.ReadAllText(Path.Combine(procDir, "environ")); }
        catch { return false; }

        var start = 0;
        while (start < environ.Length)
        {
            var index = environ.IndexOf(envEntry, start, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 || environ[index - 1] == '\0';
            var afterIndex = index + envEntry.Length;
            var after = afterIndex == environ.Length || environ[afterIndex] == '\0';
            if (before && after)
                return true;

            start = index + 1;
        }

        return false;
    }

    private static bool IsDescendantOf(int pid, int ancestorPid)
    {
        var current = pid;
        for (var i = 0; i < MaxAncestorWalk; i++)
        {
            if (current == ancestorPid)
                return true;
            if (current <= 1)
                return false;

            string stat;
            try { stat = File.ReadAllText($"/proc/{current}/stat"); }
            catch { return false; }

            if (!TryParseStat(stat, out _, out var ppid))
                return false;
            if (ppid == current)
                return false;
            current = ppid;
        }

        return false;
    }

    private static bool TryParseStat(string stat, out long ticks, out int ppid)
    {
        ticks = 0;
        ppid = 0;
        var close = stat.LastIndexOf(')');
        if (close < 0) return false;

        var parts = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 13) return false;
        if (!int.TryParse(parts[1], out ppid)) return false;
        if (!long.TryParse(parts[11], out var utime)) return false;
        if (!long.TryParse(parts[12], out var stime)) return false;
        ticks = utime + stime;
        return true;
    }

    private readonly record struct ProcessCpuSample(long CpuTicks, int ProcessCount);
}
