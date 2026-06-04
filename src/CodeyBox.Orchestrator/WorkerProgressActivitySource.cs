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
/// <see cref="SandboxConventions.WorkItemIdEnvironmentVariable"/> with
/// provider-owned sandbox activity transitions. Static sandbox ownership is
/// tracked only as a baseline and is not progress by itself.
/// </summary>
public sealed class DefaultWorkerProgressActivitySource : IWorkerProgressActivitySource
{
    public const string WorkItemIdEnvironmentVariable = SandboxConventions.WorkItemIdEnvironmentVariable;

    private const int MaxAncestorWalk = 32;
    private static readonly TimeSpan InitialCpuSampleDelay = TimeSpan.FromMilliseconds(50);
    private readonly IActiveSandboxProgressProvider? _activeSandboxProvider;
    private readonly ConcurrentDictionary<WorkItemId, ProcessCpuSample> _processSamples = new();
    private readonly ConcurrentDictionary<WorkItemId, string> _activeSandboxSignatures = new();

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

        var signatureParts = snapshot
            .Where(entry =>
                entry.WorkItemId == itemId &&
                !string.IsNullOrWhiteSpace(entry.SandboxId))
            .Select(entry => $"{entry.SandboxId}\0{entry.Status ?? ""}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (signatureParts.Length == 0)
        {
            _activeSandboxSignatures.TryRemove(itemId, out _);
            return false;
        }

        var signature = string.Join("\0\0", signatureParts);
        if (!_activeSandboxSignatures.TryGetValue(itemId, out var previous))
        {
            _activeSandboxSignatures[itemId] = signature;
            return false;
        }

        _activeSandboxSignatures[itemId] = signature;
        if (string.Equals(signature, previous, StringComparison.Ordinal))
            return false;

        reason = "active-sandbox-change";
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
            if (TryConfirmImmediateCpuProgress(itemId, sample, out var observedSample))
            {
                _processSamples[itemId] = observedSample;
                reason = "process-cpu";
                return true;
            }

            _processSamples[itemId] = observedSample;
            return false;
        }

        _processSamples[itemId] = sample;
        if (sample.CpuTicks > previous.CpuTicks)
        {
            reason = "process-cpu";
            return true;
        }

        return false;
    }

    private static bool TryConfirmImmediateCpuProgress(
        WorkItemId itemId,
        ProcessCpuSample baseline,
        out ProcessCpuSample observedSample)
    {
        observedSample = baseline;

        // Establish a CPU baseline for workers first observed after durable
        // progress is already stale without treating mere process presence as
        // progress.
        Thread.Sleep(InitialCpuSampleDelay);

        if (!TryReadWorkItemCpuTicks(itemId, out var next))
            return false;

        observedSample = next;
        return string.Equals(next.ProcessSetSignature, baseline.ProcessSetSignature, StringComparison.Ordinal)
            && next.CpuTicks > baseline.CpuTicks;
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
}
