using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class QuotaRouterConfigMapper
{
    public static QuotaRouterOptions ToOptions(QuotaRouterConfig qr) => new()
    {
        MinQuotaPct = qr.MinQuotaPct,
        MinQuotaPctByWindow = BuildWindowFloorOverrides(qr.MinQuotaPctByWindow),
        StartFloorPct = qr.StartFloorPct,
        EndFloorPct = qr.EndFloorPct,
        FloorByAgent = BuildFloorOverrides(qr.FloorByAgent),
        RampWindow = TimeSpan.FromSeconds(qr.RampWindowSeconds),
        RampWindowByAgent = BuildRampWindowOverrides(qr.RampWindowByAgentSeconds),
        QuotaRecheckInterval = TimeSpan.FromSeconds(qr.QuotaRecheckIntervalSeconds),
        QuotaCacheTtl = TimeSpan.FromSeconds(qr.QuotaCacheTtlSeconds),
        UnknownPolicy = qr.UnknownPolicy,
        ObservedFailureWindow = TimeSpan.FromMinutes(qr.ObservedFailureWindowMinutes),
        ObservedFailureRetention = TimeSpan.FromMinutes(qr.ObservedFailureRetentionMinutes),
        CapRetryRecheckInterval = TimeSpan.FromSeconds(qr.CapRetryIntervalSeconds),
        ColdStartFitInWindow = qr.ColdStartFitInWindow,
        IntraKindRoutingPolicy = qr.IntraKindRoutingPolicy,
    };

    public static void ApplyHotReload(QuotaRouterOptions dst, QuotaRouterConfig src)
    {
        dst.MinQuotaPct = src.MinQuotaPct;
        dst.MinQuotaPctByWindow = BuildWindowFloorOverrides(src.MinQuotaPctByWindow);
        dst.StartFloorPct = src.StartFloorPct;
        dst.EndFloorPct = src.EndFloorPct;
        dst.FloorByAgent = BuildFloorOverrides(src.FloorByAgent);
        if (src.RampWindowSeconds > 0)
            dst.RampWindow = TimeSpan.FromSeconds(src.RampWindowSeconds);
        dst.RampWindowByAgent = BuildRampWindowOverrides(src.RampWindowByAgentSeconds);
        dst.QuotaRecheckInterval = TimeSpan.FromSeconds(src.QuotaRecheckIntervalSeconds);
        dst.UnknownPolicy = src.UnknownPolicy;
        dst.ObservedFailureWindow = TimeSpan.FromMinutes(src.ObservedFailureWindowMinutes);
        dst.ObservedFailureRetention = TimeSpan.FromMinutes(src.ObservedFailureRetentionMinutes);
        dst.CapRetryRecheckInterval = TimeSpan.FromSeconds(src.CapRetryIntervalSeconds);
        dst.ColdStartFitInWindow = src.ColdStartFitInWindow;
        dst.IntraKindRoutingPolicy = src.IntraKindRoutingPolicy;
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

        static double? NonNegative(double? value) =>
            value is { } v && v >= 0 ? v : null;
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
}
