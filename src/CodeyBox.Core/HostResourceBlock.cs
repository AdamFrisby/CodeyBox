namespace CodeyBox.Core;

/// <summary>
/// Kind tags for <see cref="HostResourceBlockStatus"/> entries. Host-level
/// preconditions (disk guard, sandbox provisioning deferrals) are recorded
/// here so queue status can show WHY dispatch is holding instead of letting
/// the pipeline look quota-starved.
/// </summary>
public static class HostResourceBlockKinds
{
    /// <summary>Disk-space preflight refused provisioning (see <see cref="SandboxDiskDeferredException"/>).</summary>
    public const string DiskSpace = "disk-space";

    /// <summary>Sandbox provisioning deferred on a host-level precondition (see <see cref="SandboxProvisioningDeferredException"/>).</summary>
    public const string SandboxProvisioning = "sandbox-provisioning";
}

/// <summary>
/// Point-in-time view of one active host-level resource block: the condition
/// that is currently holding dispatch, when it was first observed, and when
/// the tracker will re-admit a probe. Surfaced in <c>/queue/status</c>.
/// </summary>
public sealed record HostResourceBlockStatus
{
    /// <summary>Stable dedup key (normalized mount path or provider/operation/errorClass).</summary>
    public required string Key { get; init; }

    /// <summary>One of <see cref="HostResourceBlockKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Single-line operator-facing reason (bounded, no secrets).</summary>
    public required string Reason { get; init; }

    /// <summary>Monitored mount path for disk-space blocks; null otherwise.</summary>
    public string? MountPath { get; init; }

    /// <summary>Reporting sandbox provider for provisioning blocks; null for disk blocks.</summary>
    public string? Provider { get; init; }

    /// <summary>Provisioning operation for provisioning blocks; null for disk blocks.</summary>
    public string? Operation { get; init; }

    /// <summary>Provisioning error class for provisioning blocks; null for disk blocks.</summary>
    public string? ErrorClass { get; init; }

    /// <summary>Free bytes observed at last deferral (disk blocks only).</summary>
    public long? FreeBytes { get; init; }

    /// <summary>Threshold that was violated (disk blocks only).</summary>
    public long? ThresholdBytes { get; init; }

    /// <summary>Bounded provider-supplied detail for provisioning blocks; null for disk blocks.</summary>
    public string? Detail { get; init; }

    /// <summary>When this block was first observed (held across re-observations).</summary>
    public required DateTimeOffset BlockedSince { get; init; }

    /// <summary>When the block lapses and a fresh provisioning probe is admitted.</summary>
    public required DateTimeOffset BlockedUntil { get; init; }

    /// <summary>When the block was most recently re-observed.</summary>
    public required DateTimeOffset LastObservedAt { get; init; }

    /// <summary>
    /// True when the block holds ALL dispatch pickup (host-wide resource
    /// exhaustion, e.g. disk below the guard threshold). False for blocks that
    /// are surfaced for visibility but are item/project-scoped — e.g. a shared
    /// folder writer lease or a placement member race — where holding the
    /// whole queue would starve unrelated work.
    /// </summary>
    public required bool DispatchBlocking { get; init; }
}

/// <summary>
/// Read-side provider exposing the host-level resource blocks currently
/// holding dispatch. Implemented by <see cref="HostResourceBlockTracker"/>;
/// consumed by <c>/queue/status</c>.
/// </summary>
public interface IHostResourceBlockStatusProvider
{
    /// <summary>Active (non-expired, non-recovered) host resource blocks.</summary>
    IReadOnlyList<HostResourceBlockStatus> GetActiveHostBlocks();
}

/// <summary>
/// Process-wide record of host-level resource blocks observed at sandbox
/// provisioning time. Sandbox providers raise
/// <see cref="SandboxDiskDeferredException"/> / <see cref="SandboxProvisioningDeferredException"/>
/// when a host precondition (disk below threshold, baseline bake failure,
/// placement refusal) refuses work; the orchestrator records each deferral
/// here so the dispatch loop can HOLD new pickups while the condition holds —
/// instead of pick-up/defer churn that used to feed every eligible agent's
/// circuit breaker — and so <c>/queue/status</c> can name the real cause.
///
/// <para>Blocks are keyed (disk: normalized mount path; provisioning:
/// provider/operation/errorClass) so distinct host conditions are tracked
/// independently. A block lapses at <c>BlockedUntil</c> (the deferral's own
/// <see cref="SandboxProvisioningDeferredException.RecheckIn"/>, i.e. the
/// provider-configured recheck cadence); the next pickup then re-probes the
/// real condition and either dispatches or re-records the block. Disk blocks
/// additionally revalidate against live
/// <see cref="IDiskGuardedSandboxProvider"/> samples when a sampler is wired,
/// so freed space releases the hold before the recheck deadline.</para>
///
/// <para>Thread-safe; all mutation is serialized on an internal gate.</para>
/// </summary>
public sealed class HostResourceBlockTracker : IHostResourceBlockStatusProvider
{
    /// <summary>Largest accepted recheck interval — bounds how long a stale block can hold dispatch.</summary>
    public static readonly TimeSpan MaxRecheckIn = TimeSpan.FromHours(24);

    /// <summary>Fallback recheck when the deferral carried a non-positive interval.</summary>
    public static readonly TimeSpan DefaultRecheckIn = TimeSpan.FromMinutes(5);

    /// <summary>Most blocks retained (bounds the table against pathological key churn).</summary>
    public const int MaxTrackedBlocks = 64;

    /// <summary>Provider detail is bounded before it reaches status surfaces.</summary>
    private const int MaxDetailChars = 500;

    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<DiskGuardSample>>? _diskSampler;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _blocks = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required string Key;
        public required string Kind;
        public required string Reason;
        public string? MountPath;
        public string? Provider;
        public string? Operation;
        public string? ErrorClass;
        public long? FreeBytes;
        public long? ThresholdBytes;
        public string? Detail;
        public DateTimeOffset BlockedSince;
        public DateTimeOffset BlockedUntil;
        public DateTimeOffset LastObservedAt;
        public bool DispatchBlocking;
    }

    /// <param name="timeProvider">Clock source; injectable for tests.</param>
    /// <param name="diskSampler">
    /// Optional live sampler of <see cref="IDiskGuardedSandboxProvider"/> state
    /// (typically aggregated across the sandbox provider registry). When wired,
    /// a disk block is released as soon as a current sample shows the mount
    /// back above its configured threshold, instead of waiting out the full
    /// recheck interval. Null disables revalidation — blocks then lapse purely
    /// on <see cref="SandboxProvisioningDeferredException.RecheckIn"/>.
    /// </param>
    public HostResourceBlockTracker(
        TimeProvider? timeProvider = null,
        Func<IReadOnlyList<DiskGuardSample>>? diskSampler = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _diskSampler = diskSampler;
    }

    /// <summary>
    /// Records (or refreshes) the disk-space block for <paramref name="mountPath"/>.
    /// Called when a <see cref="SandboxDiskDeferredException"/> parks a work item.
    /// </summary>
    public void RecordDiskBlock(string mountPath, long freeBytes, long thresholdBytes, TimeSpan recheckIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        var now = _time.GetUtcNow();
        var trimmed = mountPath.Trim();
        lock (_gate)
        {
            UpsertLocked(
                DiskKey(trimmed),
                now,
                recheckIn,
                entry =>
                {
                    entry.Kind = HostResourceBlockKinds.DiskSpace;
                    entry.Reason = $"disk preflight: only {freeBytes:N0} bytes free on '{trimmed}' (threshold {thresholdBytes:N0})";
                    entry.MountPath = trimmed;
                    entry.Provider = null;
                    entry.Operation = null;
                    entry.ErrorClass = null;
                    entry.FreeBytes = freeBytes;
                    entry.ThresholdBytes = thresholdBytes;
                    entry.Detail = null;
                    entry.DispatchBlocking = true;
                });
        }
    }

    /// <summary>
    /// Records (or refreshes) the provisioning block for one
    /// provider/operation/errorClass triple. Called when a
    /// <see cref="SandboxProvisioningDeferredException"/> parks a work item.
    /// <paramref name="detail"/> is bounded before storage.
    /// </summary>
    public void RecordProvisioningBlock(
        string provider,
        string operation,
        string errorClass,
        string? detail,
        TimeSpan recheckIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorClass);
        var now = _time.GetUtcNow();
        var boundedDetail = Bound(detail);
        lock (_gate)
        {
            UpsertLocked(
                $"prov:{provider.Trim()}|{operation.Trim()}|{errorClass.Trim()}",
                now,
                recheckIn,
                entry =>
                {
                    entry.Kind = HostResourceBlockKinds.SandboxProvisioning;
                    entry.Reason =
                        $"sandbox provisioning deferred: provider={provider.Trim()} operation={operation.Trim()} " +
                        $"errorClass={errorClass.Trim()}" +
                        (boundedDetail is null ? "" : $": {boundedDetail}");
                    entry.MountPath = null;
                    entry.Provider = provider.Trim();
                    entry.Operation = operation.Trim();
                    entry.ErrorClass = errorClass.Trim();
                    entry.FreeBytes = null;
                    entry.ThresholdBytes = null;
                    entry.Detail = boundedDetail;
                    // Provisioning deferrals can be item/project-scoped
                    // (shared-folder writer lease, placement member race), so
                    // they are surfaced for visibility but never hold the
                    // whole dispatch queue — the item's own RecheckIn requeue
                    // already bounds the churn and the breaker classifier
                    // already treats them as host conditions.
                    entry.DispatchBlocking = false;
                });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HostResourceBlockStatus> GetActiveHostBlocks()
    {
        var now = _time.GetUtcNow();
        // Only pay for a live disk sampling pass when a disk block exists.
        // The sampler runs outside the gate: provider probes may touch the
        // filesystem and must not serialize against block recording.
        bool hasDiskBlock;
        lock (_gate)
        {
            hasDiskBlock = HasDiskBlockLocked();
        }
        var samples = hasDiskBlock ? SampleDiskSafely() : null;

        lock (_gate)
        {
            PruneExpiredLocked(now);
            if (samples is { Count: > 0 })
                RevalidateDiskBlocksLocked(samples);
            if (_blocks.Count == 0)
                return [];
            var result = new List<HostResourceBlockStatus>(_blocks.Count);
            foreach (var entry in _blocks.Values)
                result.Add(ToStatus(entry));
            result.Sort(static (a, b) => a.BlockedSince.CompareTo(b.BlockedSince));
            return result;
        }
    }

    /// <summary>
    /// The active dispatch-blocking block whose recheck deadline is soonest,
    /// or null when no host-wide block holds dispatch. Item/project-scoped
    /// provisioning blocks never gate pickup. Used by the dispatch loop to
    /// size its hold.
    /// </summary>
    public HostResourceBlockStatus? GetBlockingBlock()
    {
        var blocks = GetActiveHostBlocks();
        HostResourceBlockStatus? earliest = null;
        foreach (var block in blocks)
        {
            if (!block.DispatchBlocking)
                continue;
            if (earliest is null || block.BlockedUntil < earliest.BlockedUntil)
                earliest = block;
        }
        return earliest;
    }

    private bool HasDiskBlockLocked()
    {
        foreach (var entry in _blocks.Values)
        {
            if (entry.Kind == HostResourceBlockKinds.DiskSpace)
                return true;
        }
        return false;
    }

    private void UpsertLocked(string key, DateTimeOffset now, TimeSpan recheckIn, Action<Entry> fill)
    {
        if (_blocks.TryGetValue(key, out var existing) && existing.BlockedUntil > now)
        {
            // Still blocked: refresh the observation and extend the deadline to
            // now + recheckIn, keeping the original BlockedSince so "since"
            // reflects when the condition first appeared.
            existing.BlockedUntil = now + NormalizeRecheck(recheckIn);
            existing.LastObservedAt = now;
            fill(existing);
            return;
        }

        if (_blocks.Count >= MaxTrackedBlocks)
            EvictSoonestExpiringLocked();

        var entry = new Entry
        {
            Key = key,
            Kind = string.Empty,
            Reason = string.Empty,
            BlockedSince = now,
            BlockedUntil = now + NormalizeRecheck(recheckIn),
            LastObservedAt = now,
        };
        fill(entry);
        _blocks[key] = entry;
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        if (_blocks.Count == 0)
            return;
        foreach (var (key, entry) in _blocks.ToArray())
        {
            if (entry.BlockedUntil <= now)
                _blocks.Remove(key);
        }
    }

    /// <summary>
    /// Releases disk blocks whose monitored mount currently samples at or
    /// above its configured threshold. A mount with no current sample (or an
    /// inconclusive null free-bytes reading) keeps its timed block — only
    /// positive recovery evidence clears early.
    /// </summary>
    private void RevalidateDiskBlocksLocked(IReadOnlyList<DiskGuardSample> samples)
    {
        var byPath = new Dictionary<string, DiskGuardSample>(StringComparer.Ordinal);
        foreach (var sample in samples)
            byPath[NormalizeMountPath(sample.Path)] = sample;

        foreach (var (key, entry) in _blocks.ToArray())
        {
            if (entry.Kind != HostResourceBlockKinds.DiskSpace || entry.MountPath is null)
                continue;
            if (!byPath.TryGetValue(NormalizeMountPath(entry.MountPath), out var sample))
                continue;
            if (sample.FreeBytes is not long free || free < sample.ThresholdBytes)
                continue;
            _blocks.Remove(key);
        }
    }

    private IReadOnlyList<DiskGuardSample>? SampleDiskSafely()
    {
        if (_diskSampler is null)
            return null;
        try
        {
            return _diskSampler();
        }
        catch (Exception)
        {
            // A probe fault must never break dispatch or status reporting;
            // timed expiry still bounds every block.
            return null;
        }
    }

    private void EvictSoonestExpiringLocked()
    {
        string? victim = null;
        var victimUntil = DateTimeOffset.MaxValue;
        foreach (var (key, entry) in _blocks)
        {
            if (entry.BlockedUntil < victimUntil)
            {
                victimUntil = entry.BlockedUntil;
                victim = key;
            }
        }
        if (victim is not null)
            _blocks.Remove(victim);
    }

    private static HostResourceBlockStatus ToStatus(Entry entry) =>
        new()
        {
            Key = entry.Key,
            Kind = entry.Kind,
            Reason = entry.Reason,
            MountPath = entry.MountPath,
            Provider = entry.Provider,
            Operation = entry.Operation,
            ErrorClass = entry.ErrorClass,
            FreeBytes = entry.FreeBytes,
            ThresholdBytes = entry.ThresholdBytes,
            Detail = entry.Detail,
            BlockedSince = entry.BlockedSince,
            BlockedUntil = entry.BlockedUntil,
            LastObservedAt = entry.LastObservedAt,
            DispatchBlocking = entry.DispatchBlocking,
        };

    private static string DiskKey(string mountPath) => $"disk:{NormalizeMountPath(mountPath)}";

    private static string NormalizeMountPath(string path) =>
        path.Trim().TrimEnd('/', '\\');

    private static TimeSpan NormalizeRecheck(TimeSpan recheckIn)
    {
        if (recheckIn <= TimeSpan.Zero)
            return DefaultRecheckIn;
        return recheckIn > MaxRecheckIn ? MaxRecheckIn : recheckIn;
    }

    private static string? Bound(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;
        var trimmed = detail.Trim();
        return trimmed.Length <= MaxDetailChars ? trimmed : trimmed[..MaxDetailChars];
    }
}
