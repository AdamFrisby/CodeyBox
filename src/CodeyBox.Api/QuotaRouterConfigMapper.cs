using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class QuotaRouterConfigMapper
{
    public static QuotaRouterOptions ToOptions(QuotaRouterConfig qr)
    {
        var paused = BuildPausedQuotaOptions(qr);
        var options = new QuotaRouterOptions
        {
            MinQuotaPct = qr.MinQuotaPct,
            MinQuotaPctByWindow = BuildWindowFloorOverrides(qr.MinQuotaPctByWindow),
            StartFloorPct = qr.StartFloorPct,
            EndFloorPct = qr.EndFloorPct,
            FloorByAgent = BuildFloorOverrides(qr.FloorByAgent),
            Pools = BuildPoolOptions(qr.Pools),
            FloorByPool = BuildPoolFloorOverrides(qr.FloorByPool, qr.Pools),
            RampWindow = TimeSpan.FromSeconds(qr.RampWindowSeconds),
            RampWindowByAgent = BuildRampWindowOverrides(qr.RampWindowByAgentSeconds),
            QuotaRecheckInterval = TimeSpan.FromSeconds(qr.QuotaRecheckIntervalSeconds),
            QuotaRecoveryProbeInterval = BuildPositiveDuration(
                qr.QuotaRecoveryProbeIntervalSeconds,
                QuotaRouterDefaults.DefaultQuotaRecoveryProbeInterval),
            MaxQuotaRecoveryProbeEligibilityScan = BuildPositiveLimit(
                qr.MaxQuotaRecoveryProbeEligibilityScan,
                QuotaRouterDefaults.DefaultQuotaRecoveryProbeEligibilityScanLimit),
            QuotaCacheTtl = TimeSpan.FromSeconds(qr.QuotaCacheTtlSeconds),
            PausedQuotaCacheTtl = paused.CacheTtl,
            PausedProbeMaxStaleness = paused.MaxStaleness,
            PausedQuotaMaxCacheEntries = paused.MaxCacheEntries,
            UnknownPolicy = qr.UnknownPolicy,
            ObservedFailureWindow = TimeSpan.FromMinutes(qr.ObservedFailureWindowMinutes),
            ObservedFailureRetention = TimeSpan.FromMinutes(qr.ObservedFailureRetentionMinutes),
            CapRetryRecheckInterval = TimeSpan.FromSeconds(qr.CapRetryIntervalSeconds),
            ColdStartFitInWindow = qr.ColdStartFitInWindow,
            DrainAggressiveness = qr.DrainAggressiveness,
            DispatchReservationEstimatePct = qr.DispatchReservationEstimatePct,
            DispatchReservationEstimatePctByAgent = new Dictionary<string, double>(qr.DispatchReservationEstimatePctByAgent, StringComparer.OrdinalIgnoreCase),
            DispatchReservationMinPct = qr.DispatchReservationMinPct,
            DispatchReservationMaxPct = qr.DispatchReservationMaxPct,
            QuotaReservationMaxAge = BuildPositiveDuration(
                qr.QuotaReservationMaxAgeSeconds,
                QuotaRouterDefaults.DefaultQuotaReservationMaxAge),
            ExpectedResets = BuildExpectedResetOverrides(qr.ExpectedResets),
            IntraKindRoutingPolicy = qr.IntraKindRoutingPolicy,
        };
        QuotaPoolValidation.Validate(options);
        return options;
    }

    public static void ApplyHotReload(QuotaRouterOptions dst, QuotaRouterConfig src)
    {
        dst.MinQuotaPct = src.MinQuotaPct;
        dst.MinQuotaPctByWindow = BuildWindowFloorOverrides(src.MinQuotaPctByWindow);
        dst.StartFloorPct = src.StartFloorPct;
        dst.EndFloorPct = src.EndFloorPct;
        dst.FloorByAgent = BuildFloorOverrides(src.FloorByAgent);
        dst.Pools = BuildPoolOptions(src.Pools);
        dst.FloorByPool = BuildPoolFloorOverrides(src.FloorByPool, src.Pools);
        if (src.RampWindowSeconds > 0)
            dst.RampWindow = TimeSpan.FromSeconds(src.RampWindowSeconds);
        dst.RampWindowByAgent = BuildRampWindowOverrides(src.RampWindowByAgentSeconds);
        var paused = BuildPausedQuotaOptions(src);
        dst.QuotaRecheckInterval = TimeSpan.FromSeconds(src.QuotaRecheckIntervalSeconds);
        dst.QuotaRecoveryProbeInterval = BuildPositiveDuration(
            src.QuotaRecoveryProbeIntervalSeconds,
            QuotaRouterDefaults.DefaultQuotaRecoveryProbeInterval);
        dst.MaxQuotaRecoveryProbeEligibilityScan = BuildPositiveLimit(
            src.MaxQuotaRecoveryProbeEligibilityScan,
            QuotaRouterDefaults.DefaultQuotaRecoveryProbeEligibilityScanLimit);
        dst.PausedQuotaCacheTtl = paused.CacheTtl;
        dst.PausedProbeMaxStaleness = paused.MaxStaleness;
        dst.PausedQuotaMaxCacheEntries = paused.MaxCacheEntries;
        dst.UnknownPolicy = src.UnknownPolicy;
        dst.ObservedFailureWindow = TimeSpan.FromMinutes(src.ObservedFailureWindowMinutes);
        dst.ObservedFailureRetention = TimeSpan.FromMinutes(src.ObservedFailureRetentionMinutes);
        dst.CapRetryRecheckInterval = TimeSpan.FromSeconds(src.CapRetryIntervalSeconds);
        dst.ColdStartFitInWindow = src.ColdStartFitInWindow;
        dst.DrainAggressiveness = src.DrainAggressiveness;
        dst.DispatchReservationEstimatePct = src.DispatchReservationEstimatePct;
        dst.DispatchReservationEstimatePctByAgent = new Dictionary<string, double>(src.DispatchReservationEstimatePctByAgent, StringComparer.OrdinalIgnoreCase);
        dst.DispatchReservationMinPct = src.DispatchReservationMinPct;
        dst.DispatchReservationMaxPct = src.DispatchReservationMaxPct;
        if (src.QuotaReservationMaxAgeSeconds > 0)
            dst.QuotaReservationMaxAge = TimeSpan.FromSeconds(src.QuotaReservationMaxAgeSeconds);
        dst.ExpectedResets = BuildExpectedResetOverrides(src.ExpectedResets);
        dst.IntraKindRoutingPolicy = src.IntraKindRoutingPolicy;
    }

    private static PausedQuotaMapping BuildPausedQuotaOptions(QuotaRouterConfig qr)
    {
        if (qr.PausedQuotaCacheTtlSeconds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(qr),
                qr.PausedQuotaCacheTtlSeconds,
                "CodeyBox:QuotaRouter:PausedQuotaCacheTtlSeconds must be positive.");

        if (qr.PausedProbeMaxStalenessSeconds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(qr),
                qr.PausedProbeMaxStalenessSeconds,
                "CodeyBox:QuotaRouter:PausedProbeMaxStalenessSeconds must be positive.");

        if (qr.PausedProbeMaxStalenessSeconds < qr.PausedQuotaCacheTtlSeconds)
            throw new ArgumentException(
                "CodeyBox:QuotaRouter:PausedProbeMaxStalenessSeconds must be greater than or equal to PausedQuotaCacheTtlSeconds.",
                nameof(qr));

        if (qr.PausedQuotaMaxCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(qr),
                qr.PausedQuotaMaxCacheEntries,
                "CodeyBox:QuotaRouter:PausedQuotaMaxCacheEntries must be positive.");

        return new PausedQuotaMapping(
            TimeSpan.FromSeconds(qr.PausedQuotaCacheTtlSeconds),
            TimeSpan.FromSeconds(qr.PausedProbeMaxStalenessSeconds),
            qr.PausedQuotaMaxCacheEntries);
    }

    private static Dictionary<string, TimeSpan> BuildRampWindowOverrides(IDictionary<string, int>? src)
    {
        var dst = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (kv.Value <= 0) continue;
            dst[kv.Key] = TimeSpan.FromSeconds(kv.Value);
        }
        return dst;
    }

    private static TimeSpan BuildPositiveDuration(int seconds, TimeSpan fallback) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : fallback;

    private static int BuildPositiveLimit(int limit, int fallback) =>
        limit > 0 ? limit : fallback;

    private static Dictionary<string, QuotaFloorOverrideOptions> BuildFloorOverrides(
        IDictionary<string, QuotaRouterFloorConfig>? src)
    {
        var dst = new Dictionary<string, QuotaFloorOverrideOptions>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var entry = new QuotaFloorOverrideOptions
            {
                MinQuotaPct = NonNegative(kv.Value.MinQuotaPct),
                StartFloorPct = NonNegative(kv.Value.StartFloorPct),
                EndFloorPct = NonNegative(kv.Value.EndFloorPct),
                RampWindow = kv.Value.RampWindowSeconds is { } seconds && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : null,
            };
            if (entry.MinQuotaPct is null
                && entry.StartFloorPct is null
                && entry.EndFloorPct is null
                && entry.RampWindow is null)
            {
                continue;
            }
            dst[kv.Key] = entry;
        }
        return dst;
    }

    private static Dictionary<string, double> BuildWindowFloorOverrides(IDictionary<string, double>? src)
    {
        var dst = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (kv.Value < 0) continue;
            dst[kv.Key] = kv.Value;
        }
        return dst;
    }

    private static double? NonNegative(double? value) =>
        value is { } v && v >= 0 ? v : null;

    private static Dictionary<string, QuotaPoolOptions> BuildPoolOptions(
        IDictionary<string, QuotaPoolConfig>? src)
    {
        var dst = new Dictionary<string, QuotaPoolOptions>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var name = kv.Key.Trim();
            var kind = ParsePoolKind(name, kv.Value.Kind);
            double? estimate = null;
            if (kv.Value.ReservationEstimate is { } raw)
            {
                if (!(raw > 0) || !double.IsFinite(raw))
                    throw new InvalidOperationException(
                        $"Quota pool '{name}': ReservationEstimate must be a positive finite number.");
                estimate = raw;
            }
            dst[name] = new QuotaPoolOptions
            {
                Name = name,
                Kind = kind,
                BalanceUnit = string.IsNullOrWhiteSpace(kv.Value.BalanceUnit) ? null : kv.Value.BalanceUnit.Trim(),
                ReservationEstimate = estimate,
            };
        }
        return dst;
    }

    private static QuotaPoolKind ParsePoolKind(string poolName, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)
            || string.Equals(kind.Trim(), nameof(QuotaPoolKind.ResettingWindow), StringComparison.OrdinalIgnoreCase))
            return QuotaPoolKind.ResettingWindow;
        if (string.Equals(kind.Trim(), nameof(QuotaPoolKind.DepletingBalance), StringComparison.OrdinalIgnoreCase))
            return QuotaPoolKind.DepletingBalance;
        throw new InvalidOperationException(
            $"Quota pool '{poolName}': unknown replenishment kind '{kind}'. " +
            $"Expected '{nameof(QuotaPoolKind.ResettingWindow)}' or '{nameof(QuotaPoolKind.DepletingBalance)}'.");
    }

    private static Dictionary<string, QuotaPoolFloorOptions> BuildPoolFloorOverrides(
        IDictionary<string, QuotaPoolFloorConfig>? src,
        IDictionary<string, QuotaPoolConfig>? pools)
    {
        var dst = new Dictionary<string, QuotaPoolFloorOptions>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var name = kv.Key.Trim();
            var poolKind = pools is not null && pools.TryGetValue(name, out var poolConfig) && poolConfig is not null
                ? ParsePoolKind(name, poolConfig.Kind)
                : (QuotaPoolKind?)null;
            if (poolKind is null)
                throw new InvalidOperationException(
                    $"Quota floor entry '{name}' names no configured quota pool; " +
                    $"declare the pool under CodeyBox:QuotaRouter:Pools or remove the floor entry.");
            var entry = new QuotaPoolFloorOptions
            {
                MinQuotaPct = NonNegative(kv.Value.MinQuotaPct),
                StartFloorPct = NonNegative(kv.Value.StartFloorPct),
                EndFloorPct = NonNegative(kv.Value.EndFloorPct),
                RampWindow = kv.Value.RampWindowSeconds is { } seconds && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : null,
                MinBalance = kv.Value.MinBalance,
            };
            if (poolKind == QuotaPoolKind.DepletingBalance)
            {
                if (entry.MinQuotaPct is not null
                    || entry.StartFloorPct is not null
                    || entry.EndFloorPct is not null
                    || entry.RampWindow is not null)
                    throw new InvalidOperationException(
                        $"Quota pool '{name}' is a depleting-balance pool; express its floor " +
                        $"in absolute balance units via MinBalance, not in percent " +
                        $"(MinQuotaPct/StartFloorPct/EndFloorPct/RampWindowSeconds).");
                if (entry.MinBalance is { } min && (!(min >= 0) || !double.IsFinite(min)))
                    throw new InvalidOperationException(
                        $"Quota pool '{name}' is a depleting-balance pool; MinBalance must be " +
                        $"a non-negative finite absolute balance value.");
            }
            else
            {
                if (entry.MinBalance is not null)
                    throw new InvalidOperationException(
                        $"Quota pool '{name}' is a resetting-window pool; express its floor " +
                        $"in percent via MinQuotaPct/StartFloorPct/EndFloorPct, not absolute MinBalance.");
            }
            if (entry.MinQuotaPct is null
                && entry.StartFloorPct is null
                && entry.EndFloorPct is null
                && entry.RampWindow is null
                && entry.MinBalance is null)
            {
                continue;
            }
            dst[name] = entry;
        }
        return dst;
    }

    private static Dictionary<string, ExpectedQuotaResetOptions> BuildExpectedResetOverrides(
        IDictionary<string, QuotaRouterExpectedResetConfig>? src)
    {
        var dst = new Dictionary<string, ExpectedQuotaResetOptions>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dst;
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var timestamps = kv.Value.Timestamps
                .Select(t => t.ToUniversalTime())
                .Distinct()
                .OrderBy(t => t)
                .ToArray();
            var cadence = kv.Value.CadenceSeconds is { } seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
            var anchor = kv.Value.CadenceAnchor?.ToUniversalTime();
            if (timestamps.Length == 0 && (cadence is null || anchor is null))
                continue;

            dst[kv.Key] = new ExpectedQuotaResetOptions
            {
                Timestamps = timestamps,
                Cadence = cadence,
                CadenceAnchor = cadence is not null ? anchor : null,
            };
        }
        return dst;
    }

    private sealed record PausedQuotaMapping(
        TimeSpan CacheTtl,
        TimeSpan MaxStaleness,
        int MaxCacheEntries);
}
