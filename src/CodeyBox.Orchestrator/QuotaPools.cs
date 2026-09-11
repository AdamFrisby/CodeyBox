using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// How a quota pool's allowance replenishes. The two kinds are not
/// interchangeable: a resetting window returns to full at a known instant
/// (its reading is a proportion and its reset time is meaningful), while a
/// depleting balance is prepaid credit that never resets and may be topped up
/// by an arbitrary amount at any time (its meaningful quantity is the
/// absolute remaining value, and it has no reset instant).
/// </summary>
public enum QuotaPoolKind
{
    /// <summary>
    /// Subscription allowance that returns to full at a known instant.
    /// Readings are proportions (<c>AvailablePct</c>), floors are percentages,
    /// and reset times are meaningful.
    /// </summary>
    ResettingWindow,

    /// <summary>
    /// Prepaid credit that never resets. The meaningful quantity is the
    /// absolute remaining value (<see cref="AgentQuotaSnapshot.BalanceRemaining"/>);
    /// a proportional floor is not interpretable against it, so the floor must
    /// be expressed in the same absolute unit as the reading. A zero balance
    /// is exhausted (terminal), not awaiting replenishment.
    /// </summary>
    DepletingBalance,
}

/// <summary>
/// Operator-declared identity for one underlying account or subscription.
/// Pool membership is declared per class member
/// (<see cref="AgentMembership.Pool"/>); members that name the same pool are
/// metered as one quantity: one reading, one floor, one reservation escrow.
/// Membership is never derived by fingerprinting credential material, which
/// cannot distinguish two keys issued against one account from two accounts.
/// </summary>
public sealed class QuotaPoolOptions
{
    /// <summary>Pool name, matching <see cref="AgentMembership.Pool"/> (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Replenishment kind; determines the reading and floor units.</summary>
    public QuotaPoolKind Kind { get; set; } = QuotaPoolKind.ResettingWindow;

    /// <summary>
    /// Human-readable unit for absolute balances on a
    /// <see cref="QuotaPoolKind.DepletingBalance"/> pool (e.g. <c>"credits"</c>).
    /// Informational only; the gate compares raw magnitudes. Ignored for
    /// resetting-window pools.
    /// </summary>
    public string? BalanceUnit { get; set; }

    /// <summary>
    /// Estimated cost of one dispatch in the pool's native unit (percentage
    /// points for resetting-window pools, absolute balance units for
    /// depleting-balance pools). Overrides the global/agent reservation
    /// estimates for members of this pool. Null falls back to the existing
    /// <see cref="QuotaRouterOptions.DispatchReservationEstimatePct"/> chain
    /// (interpreted in the pool's native unit — set this explicitly for
    /// balance pools whose absolute scale differs from percentage points).
    /// Hot-reloadable.
    /// </summary>
    public double? ReservationEstimate { get; set; }
}

/// <summary>
/// Per-pool floor override, keyed by pool name in
/// <see cref="QuotaRouterOptions.FloorByPool"/>. The expressible fields depend
/// on the pool's <see cref="QuotaPoolKind"/>: resetting-window pools use the
/// percentage fields, depleting-balance pools use <see cref="MinBalance"/>.
/// Mixing units is rejected at configuration load.
/// </summary>
public sealed class QuotaPoolFloorOptions
{
    /// <summary>Fallback percentage floor for resetting-window pools.</summary>
    public double? MinQuotaPct { get; set; }

    /// <summary>Early-window ramp percentage floor for resetting-window pools.</summary>
    public double? StartFloorPct { get; set; }

    /// <summary>Late-window ramp percentage floor for resetting-window pools.</summary>
    public double? EndFloorPct { get; set; }

    /// <summary>Optional ramp-window length for resetting-window pools.</summary>
    public TimeSpan? RampWindow { get; set; }

    /// <summary>
    /// Absolute floor for depleting-balance pools, in the same unit as the
    /// pool's balance reading. Dispatch is refused terminally at or below
    /// this value. Defaults to <c>0</c> when unset (refuse only when empty).
    /// </summary>
    public double? MinBalance { get; set; }
}

/// <summary>
/// Pure pool-membership resolution over <see cref="QuotaRouterOptions"/>.
/// A member with no declared <see cref="AgentMembership.Pool"/> keeps legacy
/// per-agent keying; a member that names an unconfigured pool fails closed
/// (the gate refuses it and the reason names the member and the pool) rather
/// than falling back to an unkeyed reading.
/// </summary>
public static class QuotaPoolResolver
{
    /// <summary>
    /// Normalises an operator-declared pool reference (trims; empty becomes null). Pure.
    /// </summary>
    public static string? NormalizePoolName(string? pool) =>
        string.IsNullOrWhiteSpace(pool) ? null : pool.Trim();

    /// <summary>
    /// Resolves <paramref name="member"/> to its configured pool.
    /// Returns true with the canonical pool name and options when the member
    /// declares a pool that exists. Returns false when the member declares no
    /// pool (legacy per-agent keying, <paramref name="failureReason"/> null)
    /// or declares an unknown pool (fail-closed signal,
    /// <paramref name="failureReason"/> names the member and the unresolved
    /// pool). Pure.
    /// </summary>
    public static bool TryResolvePool(
        QuotaRouterOptions options,
        AgentMembership member,
        out string? poolName,
        out QuotaPoolOptions? pool,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(member);
        poolName = null;
        pool = null;
        failureReason = null;
        var name = NormalizePoolName(member.Pool);
        if (name is null)
            return false;
        if (options.Pools is { Count: > 0 } pools)
        {
            foreach (var key in pools.Keys)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    poolName = key;
                    pool = pools[key];
                    return true;
                }
            }
        }
        failureReason = $"member '{member.RouteKey}' references unresolved quota pool '{name}'";
        return false;
    }

    /// <summary>
    /// Resolves <paramref name="member"/> to its configured pool options.
    /// Convenience overload when the canonical name is not needed. Pure.
    /// </summary>
    public static bool TryResolvePool(
        QuotaRouterOptions options,
        AgentMembership member,
        out QuotaPoolOptions? pool,
        out string? failureReason)
    {
        var resolved = TryResolvePool(options, member, out _, out pool, out failureReason);
        return resolved;
    }

    /// <summary>
    /// True when any pools are configured. With no pools declared, all members
    /// keep legacy per-agent keying and existing configuration behaves as before.
    /// </summary>
    public static bool HasPools(QuotaRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Pools is { Count: > 0 };
    }
}

/// <summary>
/// Read-surface masking for depleting-balance pools: a balance pool is never
/// reported with a reset instant, even if a probe echoed one. Single source
/// of truth for every quota read surface (HTTP, history, snapshots).
/// </summary>
public static class QuotaPoolMasks
{
    /// <summary>
    /// Returns <paramref name="snapshot"/> with every reset instant nulled:
    /// top-level, account windows, per-model entries and their nested
    /// windows. All other fields (including the absolute balance) pass
    /// through unchanged. Pure.
    /// </summary>
    public static AgentQuotaSnapshot WithoutResetInstants(AgentQuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var windows = snapshot.Windows.Count == 0
            ? snapshot.Windows
            : snapshot.Windows.Select(w => w with { ResetAt = null }).ToList();
        IReadOnlyDictionary<string, ModelQuota> perModel = snapshot.PerModel;
        if (snapshot.PerModel.Count > 0)
        {
            var masked = new Dictionary<string, ModelQuota>(snapshot.PerModel.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, model) in snapshot.PerModel)
            {
                var modelWindows = model.Windows.Count == 0
                    ? model.Windows
                    : model.Windows.Select(w => w with { ResetAt = null }).ToList();
                masked[key] = model with { ResetAt = null, Windows = modelWindows };
            }
            perModel = masked;
        }
        return snapshot with { ResetAt = null, Windows = windows, PerModel = perModel };
    }
}

/// <summary>
/// Configuration-load validation for quota pools. Rejects unit mismatches —
/// a proportional floor on a balance pool, or an absolute floor on a
/// resetting-window pool — with an error naming the pool and the expected unit.
/// </summary>
public static class QuotaPoolValidation
{
    /// <summary>
    /// Validates the pool/floor configuration, throwing
    /// <see cref="InvalidOperationException"/> naming the offending pool and
    /// the expected unit on the first mismatch. Pure (reads only).
    /// </summary>
    public static void Validate(QuotaRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Pools is { } pools)
        {
            foreach (var (key, pool) in pools)
            {
                if (pool is null)
                    throw new InvalidOperationException(
                        $"Quota pool '{key}' has no configuration; declare its replenishment kind.");
            }
        }
        if (options.FloorByPool is not { } floors)
            return;
        foreach (var (key, floor) in floors)
        {
            if (floor is null)
                continue;
            if (options.Pools is null || !options.Pools.TryGetValue(key, out var pool) || pool is null)
                throw new InvalidOperationException(
                    $"Quota floor entry '{key}' names no configured quota pool; " +
                    $"declare the pool under QuotaRouter:Pools or remove the floor entry.");
            if (pool.Kind == QuotaPoolKind.DepletingBalance)
            {
                if (floor.MinQuotaPct is not null
                    || floor.StartFloorPct is not null
                    || floor.EndFloorPct is not null
                    || floor.RampWindow is not null)
                    throw new InvalidOperationException(
                        $"Quota pool '{key}' is a depleting-balance pool; express its floor " +
                        $"in absolute '{pool.BalanceUnit ?? "balance"}' units via MinBalance, " +
                        $"not in percent (MinQuotaPct/StartFloorPct/EndFloorPct/RampWindow).");
                if (floor.MinBalance is < 0)
                    throw new InvalidOperationException(
                        $"Quota pool '{key}' is a depleting-balance pool; MinBalance must be " +
                        $"non-negative absolute '{pool.BalanceUnit ?? "balance"}' units.");
            }
            else
            {
                if (floor.MinBalance is not null)
                    throw new InvalidOperationException(
                        $"Quota pool '{key}' is a resetting-window pool; express its floor " +
                        $"in percent via MinQuotaPct/StartFloorPct/EndFloorPct, not absolute MinBalance.");
            }
        }
    }
}
