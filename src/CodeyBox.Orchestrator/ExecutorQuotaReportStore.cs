using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A quota reading observed by the executor host holding a pool's credential,
/// reported to the orchestrator. Carries the pool identity, the availability
/// reading, the reset time, and the time it was observed — the four facts the
/// gate needs. It never carries an admission verdict: the orchestrator remains
/// the sole authority for the gate decision and re-evaluates every stored
/// reading through <see cref="QuotaGatePolicy"/> on each dispatch.
/// </summary>
public sealed record ExecutorQuotaReport
{
    /// <summary>Pool this reading meters, matching a configured pool name (case-insensitive).</summary>
    public required string PoolName { get; init; }

    /// <summary>
    /// Percentage of quota remaining (0–100) for resetting-window pools.
    /// Required for a known resetting-window reading; informational otherwise.
    /// </summary>
    public double? AvailablePct { get; init; }

    /// <summary>
    /// Absolute remaining balance for depleting-balance pools, in the pool's
    /// native unit. Required for a known depleting-balance reading.
    /// </summary>
    public double? BalanceRemaining { get; init; }

    /// <summary>
    /// When the quota window resets, if known. Meaningful only for
    /// resetting-window pools — a depleting-balance pool never resets, so a
    /// report carrying one for such a pool is rejected rather than stored.
    /// </summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>
    /// When the executor observed this reading. Freshness (and therefore
    /// whether the pool meters from this report or reads as unknown) is
    /// evaluated against this instant, not arrival time.
    /// </summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// Set when the executor could produce no real reading: its local probe's
    /// unknown reason, preserved so the orchestrator applies the same unknown
    /// handling as a direct probe (<see cref="QuotaUnknownReason.Transient"/>
    /// retains recent-good data, <see cref="QuotaUnknownReason.Permanent"/> /
    /// <see cref="QuotaUnknownReason.NoCredential"/> discard it). Null means
    /// this report carries a real reading.
    /// </summary>
    public QuotaUnknownReason? Unknown { get; init; }

    /// <summary>Human-readable notes, e.g. the probe endpoint outcome. Bounded on arrival.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Orchestrator-side store for executor-reported quota readings. Executors
/// holding a pool's credential probe locally and POST their readings here;
/// the quota gate meters <see cref="QuotaProbeSource.ExecutorReported"/> pools
/// from the latest fresh report instead of probing directly (the orchestrator
/// holds no credential for those accounts and must not probe against nothing).
///
/// <para>Trust boundary: every report is validated at this sink before it is
/// stored. The pool must exist and be executor-reported, the reporting host
/// must be operator-declared in the pool's <c>HolderHostIds</c> (exact ordinal
/// match — a misconfigured or compromised executor cannot overwrite a meter
/// it does not own), numeric readings must be in range, and the reset time
/// must be consistent with the pool's kind. Rejected reports leave the stored
/// reading unchanged.</para>
///
/// <para>Freshness is evaluated at read time against the pool's
/// <c>ReportedReadingMaxAge</c>: a stale or missing report reads as an unknown
/// snapshot (never as healthy headroom) and flows through the same unknown
/// handling as a direct probe, including fail-closed whenever a non-zero floor
/// is in force. This store never decides admission — it only accepts or
/// rejects reports and serves snapshots to the gate.</para>
///
/// <para>Thread-safe. The options reference is the live shared instance, so
/// hot-reload edits to probe source, staleness bound, and holder allowlists
/// take effect on the next report or read without a restart.</para>
/// </summary>
public sealed class ExecutorQuotaReportStore
{
    /// <summary>Maximum note length accepted on a report; longer notes are rejected, not truncated.</summary>
    public const int MaxReportNotesLength = 512;

    private readonly QuotaRouterOptions _options;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, StoredReport> _readings = new(StringComparer.OrdinalIgnoreCase);

    private sealed record StoredReport(ExecutorQuotaReport Report, string ReportedByHostId);

    public ExecutorQuotaReportStore(QuotaRouterOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates <paramref name="report"/> from <paramref name="hostId"/> and,
    /// when valid, stores it as the pool's current reading (replacing any
    /// prior report). Returns true with a null reason on acceptance; false
    /// with a human-readable reason on rejection. Rejection never mutates the
    /// stored reading. The outcome is a storage verdict only — never a gate
    /// admission decision, which stays with <see cref="QuotaGatePolicy"/> on
    /// the orchestrator.
    /// </summary>
    public bool TryReport(string? hostId, ExecutorQuotaReport? report, out string? rejectionReason)
    {
        var now = _time.GetUtcNow();
        if (!TryValidate(hostId, report, now, out var poolName, out var pool, out var normalized, out rejectionReason))
            return false;

        lock (_lock)
            _readings[poolName!] = new StoredReport(normalized!, hostId!.Trim());
        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Serves the pool's current snapshot for the gate: the stored reading
    /// while it is fresh, otherwise an unknown snapshot. A missing report, a
    /// report older than the pool's <c>ReportedReadingMaxAge</c>, or a pool
    /// that is not executor-reported all read as
    /// <see cref="QuotaUnknownReason.Transient"/> unknown so the gate's
    /// standard unknown handling (including fail-closed on a non-zero floor)
    /// applies and silence never presents as headroom. A fresh explicitly
    /// unknown report preserves its <see cref="QuotaUnknownReason"/> so
    /// retain-vs-discard semantics match a direct probe.
    /// </summary>
    public AgentQuotaSnapshot GetSnapshot(string? poolName)
    {
        var now = _time.GetUtcNow();
        var normalized = QuotaPoolResolver.NormalizePoolName(poolName);
        if (normalized is null)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient, "executor-reported quota read with no pool name");
        if (_options.Pools is not { } pools
            || !pools.TryGetValue(normalized, out var pool)
            || pool is null)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient, $"no configured quota pool '{normalized}'");
        if (pool.ProbeSource != QuotaProbeSource.ExecutorReported)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient,
                $"quota pool '{normalized}' is orchestrator-probed; no executor reading applies");

        StoredReport? stored;
        lock (_lock)
            _readings.TryGetValue(normalized, out stored);
        if (stored is null)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient,
                $"no executor reading reported for pool '{normalized}'");

        var age = now - stored.Report.ObservedAt;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age > pool.ReportedReadingMaxAge)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient,
                $"executor reading for pool '{normalized}' is stale " +
                $"(age {(long)Math.Round(age.TotalSeconds)}s > bound {(long)Math.Round(pool.ReportedReadingMaxAge.TotalSeconds)}s)");

        if (stored.Report.Unknown is { } reason)
            return AgentQuotaSnapshot.UnknownSnapshot(
                reason,
                $"executor '{stored.ReportedByHostId}' reported unknown for pool '{normalized}'" +
                (string.IsNullOrWhiteSpace(stored.Report.Notes) ? "" : $" ({stored.Report.Notes})"));

        var ageSeconds = (long)Math.Round(age.TotalSeconds);
        var balanceUnit = string.IsNullOrWhiteSpace(pool.BalanceUnit) ? null : pool.BalanceUnit.Trim();
        var noteDetail = string.IsNullOrWhiteSpace(stored.Report.Notes) ? "" : $" ({stored.Report.Notes})";
        if (pool.Kind == QuotaPoolKind.DepletingBalance)
            return new AgentQuotaSnapshot
            {
                AvailablePct = stored.Report.AvailablePct ?? -1,
                BalanceRemaining = stored.Report.BalanceRemaining,
                BalanceUnit = balanceUnit,
                Notes = $"executor '{stored.ReportedByHostId}' reading (age {ageSeconds}s){noteDetail}",
            };
        if (stored.Report.AvailablePct is not { } pct)
            return AgentQuotaSnapshot.UnknownSnapshot(
                QuotaUnknownReason.Transient,
                $"executor reading for pool '{normalized}' carries no percentage " +
                "(pool reconfigured since the report was stored)");
        return new AgentQuotaSnapshot
        {
            AvailablePct = pct,
            ResetAt = stored.Report.ResetAt,
            BalanceRemaining = stored.Report.BalanceRemaining,
            BalanceUnit = balanceUnit,
            Notes = $"executor '{stored.ReportedByHostId}' reading (age {ageSeconds}s){noteDetail}",
        };
    }

    /// <summary>
    /// Returns the currently stored report for <paramref name="poolName"/>,
    /// regardless of freshness, for diagnostics and tests. Freshness is a
    /// read-time concern (see <see cref="GetSnapshot"/>); this accessor does
    /// not apply it.
    /// </summary>
    public bool TryGetStored(string? poolName, out ExecutorQuotaReport? report, out string? reportedByHostId)
    {
        report = null;
        reportedByHostId = null;
        var normalized = QuotaPoolResolver.NormalizePoolName(poolName);
        if (normalized is null)
            return false;
        lock (_lock)
        {
            if (!_readings.TryGetValue(normalized, out var stored))
                return false;
            report = stored.Report;
            reportedByHostId = stored.ReportedByHostId;
            return true;
        }
    }

    private bool TryValidate(
        string? hostId,
        ExecutorQuotaReport? report,
        DateTimeOffset now,
        out string? poolName,
        out QuotaPoolOptions? pool,
        out ExecutorQuotaReport? normalized,
        out string? rejectionReason)
    {
        poolName = null;
        pool = null;
        normalized = null;
        rejectionReason = null;

        var host = string.IsNullOrWhiteSpace(hostId) ? null : hostId.Trim();
        if (host is null)
        {
            rejectionReason = "executor report rejected: reporting host id is required.";
            return false;
        }
        if (report is null)
        {
            rejectionReason = "executor report rejected: report body is required.";
            return false;
        }

        poolName = QuotaPoolResolver.NormalizePoolName(report.PoolName);
        if (poolName is null)
        {
            rejectionReason = "executor report rejected: pool name is required.";
            return false;
        }
        if (_options.Pools is not { } pools
            || !pools.TryGetValue(poolName, out pool)
            || pool is null)
        {
            pool = null;
            rejectionReason = $"executor report rejected: no configured quota pool '{poolName}'.";
            return false;
        }
        if (pool.ProbeSource != QuotaProbeSource.ExecutorReported)
        {
            rejectionReason =
                $"executor report rejected: quota pool '{poolName}' is orchestrator-probed; " +
                "reports are accepted only for executor-reported pools.";
            return false;
        }
        if (!HoldsPool(pool, host))
        {
            rejectionReason =
                $"executor report rejected: host '{host}' is not declared as holding quota pool '{poolName}'.";
            return false;
        }
        if (report.ObservedAt == default)
        {
            rejectionReason = $"executor report rejected for pool '{poolName}': observed time is required.";
            return false;
        }
        if (report.ObservedAt > now + _options.ReportedReadingClockSkew)
        {
            rejectionReason = $"executor report rejected for pool '{poolName}': observed time is in the future.";
            return false;
        }
        if (report.Notes is { Length: > MaxReportNotesLength })
        {
            rejectionReason =
                $"executor report rejected for pool '{poolName}': notes exceed {MaxReportNotesLength} characters.";
            return false;
        }
        if (report.Notes is { } notes && notes.Any(char.IsControl))
        {
            rejectionReason =
                $"executor report rejected for pool '{poolName}': notes must not contain control characters.";
            return false;
        }
        if (report.Unknown is { } unknown && !Enum.IsDefined(unknown))
        {
            rejectionReason = $"executor report rejected for pool '{poolName}': unknown reason is not recognised.";
            return false;
        }
        if (ValidateReading(pool, report, now, _options.ReportedReadingClockSkew, MaxResetHorizon(_options)) is { } readingRejection)
        {
            rejectionReason = readingRejection;
            return false;
        }

        normalized = report with { PoolName = poolName };
        return true;
    }

    /// <summary>
    /// Validates the reading carried by a report against its pool's kind:
    /// percentages within 0-100, balances finite and non-negative, no reset
    /// instant on a depleting-balance pool (which never resets), and — for a
    /// resetting-window pool carrying a reset — a reset after the observed
    /// instant (within clock skew) and inside the plausible horizon (the
    /// widest configured ramp window plus skew). An unbounded executor-set
    /// reset would otherwise pin the gate's ramped floor at one end of its
    /// range or surface a bogus retry hint. Returns null when the reading is
    /// acceptable, otherwise the rejection reason. Pure.
    /// </summary>
    private static string? ValidateReading(
        QuotaPoolOptions pool,
        ExecutorQuotaReport report,
        DateTimeOffset now,
        TimeSpan clockSkew,
        TimeSpan resetHorizon)
    {
        var poolName = pool.Name;
        if (pool.Kind == QuotaPoolKind.DepletingBalance)
        {
            if (report.ResetAt is not null)
                return $"executor report rejected for pool '{poolName}': a depleting-balance pool " +
                    "never carries a reset instant.";
            if (report.Unknown is null)
            {
                if (report.BalanceRemaining is not { } balance
                    || !double.IsFinite(balance)
                    || balance < 0)
                    return $"executor report rejected for pool '{poolName}': balance must be " +
                        "a finite non-negative value.";
                if (report.AvailablePct is { } pct && (!double.IsFinite(pct) || pct is < 0 or > 100))
                    return $"executor report rejected for pool '{poolName}': percentage must be " +
                        "within 0-100 when present.";
                return null;
            }
            return ValidateAccompanyingValues(poolName, report);
        }

        if (report.ResetAt is { } reset)
        {
            if (reset <= report.ObservedAt - clockSkew)
                return $"executor report rejected for pool '{poolName}': reset must be " +
                    "after the time the reading was observed.";
            if (reset > now + resetHorizon + clockSkew)
                return $"executor report rejected for pool '{poolName}': reset is " +
                    "beyond the plausible horizon for this pool.";
        }
        if (report.Unknown is null)
        {
            if (report.AvailablePct is not { } pct || !double.IsFinite(pct) || pct is < 0 or > 100)
                return $"executor report rejected for pool '{poolName}': available percentage " +
                    "must be within 0-100.";
            if (!NonNegativeOrAbsent(report.BalanceRemaining))
                return $"executor report rejected for pool '{poolName}': balance must be " +
                    "a finite non-negative value when present.";
            return null;
        }
        return ValidateAccompanyingValues(poolName, report);
    }

    private static string? ValidateAccompanyingValues(string poolName, ExecutorQuotaReport report) =>
        RangedOrAbsent(report.AvailablePct, 0, 100)
            && NonNegativeOrAbsent(report.BalanceRemaining)
            ? null
            : $"executor report rejected for pool '{poolName}': accompanying values " +
                "are outside their valid ranges.";

    /// <summary>
    /// Widest ramp window configured anywhere (global, per-agent, per-pool),
    /// bounding how far ahead an executor-reported reset may lie. A reset
    /// beyond this horizon could never key a live ramp, so it is rejected as
    /// inconsistent rather than stored. Pure.
    /// </summary>
    private static TimeSpan MaxResetHorizon(QuotaRouterOptions options)
    {
        var horizon = options.RampWindow;
        if (options.RampWindowByAgent is { } byAgent)
        {
            foreach (var window in byAgent.Values)
            {
                if (window > horizon)
                    horizon = window;
            }
        }
        if (options.FloorByPool is { } floors)
        {
            foreach (var floor in floors.Values)
            {
                if (floor?.RampWindow is { } window && window > horizon)
                    horizon = window;
            }
        }
        return horizon > TimeSpan.Zero ? horizon : QuotaRouterDefaults.DefaultRampWindow;
    }

    /// <summary>
    /// True when <paramref name="hostId"/> is operator-declared as holding the
    /// pool. Exact ordinal equality — never substring — so "exec-1" never
    /// implies "exec-10".
    /// </summary>
    private static bool HoldsPool(QuotaPoolOptions pool, string hostId)
    {
        foreach (var entry in pool.HolderHostIds)
        {
            if (string.Equals(entry?.Trim(), hostId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool RangedOrAbsent(double? value, double min, double max) =>
        value is not { } v || (double.IsFinite(v) && v >= min && v <= max);

    private static bool NonNegativeOrAbsent(double? value) =>
        value is not { } v || (double.IsFinite(v) && v >= 0);
}
