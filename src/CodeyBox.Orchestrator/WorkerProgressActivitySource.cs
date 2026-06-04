using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A live worker-side signal that should count as watchdog progress even when
/// the work item row and agent stream files are quiet.
/// </summary>
public sealed record WorkerProgressActivity(string Reason);

/// <summary>
/// Narrow activity probe settings resolved by the watchdog for the current
/// sweep. Activity sources should not depend on the watchdog's full mutable
/// options object.
/// </summary>
public readonly record struct WorkerProgressActivityProbe(
    bool ProcessCpuProgressSignalEnabled,
    bool ActiveSandboxProgressSignalEnabled);

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
        WorkerProgressActivityProbe probe,
        CancellationToken ct);
}

/// <summary>
/// Default watchdog activity source. It combines exact host-side process CPU
/// sampling for sandbox processes that carry
/// <see cref="SandboxConventions.WorkItemIdEnvironmentVariable"/> with a
/// provider-owned active sandbox signal for VM-backed providers whose guest
/// processes are not visible from host <c>/proc</c>.
/// </summary>
public sealed class DefaultWorkerProgressActivitySource : IWorkerProgressActivitySource
{
    public const string WorkItemIdEnvironmentVariable = SandboxConventions.WorkItemIdEnvironmentVariable;

    private const int MaxAncestorWalk = 32;
    private readonly IActiveSandboxProgressProvider? _activeSandboxProvider;
    private readonly ConcurrentDictionary<WorkItemId, ProcessCpuSample> _processSamples = new();
    private readonly ConcurrentDictionary<WorkItemId, ActiveSandboxSample> _activeSandboxSamples = new();

    public DefaultWorkerProgressActivitySource(IActiveSandboxProgressProvider? activeSandboxProvider = null)
        => _activeSandboxProvider = activeSandboxProvider;

    public ValueTask<WorkerProgressActivity?> ObserveAsync(
        WorkerRegistration worker,
        WorkItemId itemId,
        WorkerProgressActivityProbe probe,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(worker.CurrentWorkItemId, itemId.ToString(), StringComparison.Ordinal))
            return ValueTask.FromResult<WorkerProgressActivity?>(null);

        if (probe.ProcessCpuProgressSignalEnabled
            && TryObserveProcessCpu(itemId, out var cpuReason))
        {
            return ValueTask.FromResult<WorkerProgressActivity?>(
                new WorkerProgressActivity(cpuReason));
        }

        if (probe.ActiveSandboxProgressSignalEnabled
            && TryObserveActiveSandbox(itemId, out var sandboxReason))
        {
            return ValueTask.FromResult<WorkerProgressActivity?>(
                new WorkerProgressActivity(sandboxReason));
        }

        return ValueTask.FromResult<WorkerProgressActivity?>(null);
    }

    private bool TryObserveActiveSandbox(WorkItemId itemId, out string reason)
    {
        reason = "";
        if (_activeSandboxProvider is not { } activeProvider)
            return false;

        IReadOnlyList<ActiveSandboxProgress> snapshot;
        try
        {
            snapshot = activeProvider.SnapshotActiveSandboxProgress();
        }
        catch
        {
            return false;
        }

        var sandboxIds = snapshot
            .Where(entry => entry.WorkItemId == itemId)
            .Select(entry => entry.SandboxId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (sandboxIds.Length == 0)
        {
            _activeSandboxSamples.TryRemove(itemId, out _);
            return false;
        }

        var sample = new ActiveSandboxSample(string.Join("\0", sandboxIds));
        if (!_activeSandboxSamples.TryGetValue(itemId, out var previous)
            || !string.Equals(sample.SandboxSetSignature, previous.SandboxSetSignature, StringComparison.Ordinal))
        {
            _activeSandboxSamples[itemId] = sample;
            reason = "active-sandbox";
            return true;
        }

        _activeSandboxSamples[itemId] = sample;
        reason = "active-sandbox";
        return true;
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

        if (!_processSamples.TryGetValue(itemId, out var previous)
            || !string.Equals(sample.ProcessSetSignature, previous.ProcessSetSignature, StringComparison.Ordinal))
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
        var processIdentities = new List<string>();

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

                if (!TryParseStat(stat, out var cpuTicks, out _, out var startTimeTicks))
                    continue;
                if (!IsDescendantOf(pid, ownPid))
                    continue;
                if (!ProcessEnvironmentContains(procDir, envEntry))
                    continue;

                totalCpuTicks += cpuTicks;
                processIdentities.Add($"{pid}:{startTimeTicks}");
                processCount++;
            }
        }
        catch
        {
            return false;
        }

        if (processCount == 0)
            return false;

        processIdentities.Sort(StringComparer.Ordinal);
        sample = new ProcessCpuSample(
            totalCpuTicks,
            string.Join("\0", processIdentities));
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

            if (!TryParseStat(stat, out _, out var ppid, out _))
                return false;
            if (ppid == current)
                return false;
            current = ppid;
        }

        return false;
    }

    private static bool TryParseStat(string stat, out long ticks, out int ppid, out long startTimeTicks)
    {
        ticks = 0;
        ppid = 0;
        startTimeTicks = 0;
        var close = stat.LastIndexOf(')');
        if (close < 0) return false;

        var parts = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 20) return false;
        if (!int.TryParse(parts[1], out ppid)) return false;
        if (!long.TryParse(parts[11], out var utime)) return false;
        if (!long.TryParse(parts[12], out var stime)) return false;
        if (!long.TryParse(parts[19], out startTimeTicks)) return false;
        ticks = utime + stime;
        return true;
    }

    private readonly record struct ProcessCpuSample(long CpuTicks, string ProcessSetSignature);
    private readonly record struct ActiveSandboxSample(string SandboxSetSignature);
}
