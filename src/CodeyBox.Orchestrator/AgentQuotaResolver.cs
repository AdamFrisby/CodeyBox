using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public static class AgentQuotaResolver
{
    /// <summary>Sentinel ModelId meaning "any model in the bucket list is acceptable".</summary>
    internal const string AutoModelSentinel = "auto";

    public static EffectiveQuota ResolveMemberQuota(AgentQuotaSnapshot snapshot, AgentMembership member)
    {
        if (string.IsNullOrWhiteSpace(member.ModelId))
            return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null);

        if (snapshot.PerModel.TryGetValue(member.ModelId, out var modelQuota))
            return new EffectiveQuota(modelQuota.AvailablePct, modelQuota.ResetAt, modelQuota.Window);

        // ModelId is set but not in PerModel.
        //
        // For the "auto" sentinel (gemini ModelRouterService picks per-turn from the
        // available pool), best-of-fleet across the bucket list is the right reading:
        // any single model with quota is enough for auto-routing to succeed.
        if (string.Equals(member.ModelId, AutoModelSentinel, StringComparison.OrdinalIgnoreCase)
            && snapshot.PerModel.Count > 0)
        {
            ModelQuota? best = null;
            foreach (var q in snapshot.PerModel.Values)
            {
                if (best is null || q.AvailablePct > best.AvailablePct)
                    best = q;
            }

            // ResetAt is the earliest reset across all bucket entries (the soonest a
            // currently-walled member will become available again).
            DateTimeOffset? earliestReset = null;
            foreach (var q in snapshot.PerModel.Values)
            {
                if (q.ResetAt is { } r && (earliestReset is null || r < earliestReset))
                    earliestReset = r;
            }
            return new EffectiveQuota(best!.AvailablePct, earliestReset, best.Window);
        }

        // Unknown model id on a probe that DOES provide per-model data: the operator
        // configured a model the probe has no signal for. Fail safe by surfacing
        // unknown so QuotaUnknownPolicy gates it.
        if (snapshot.PerModel.Count > 0)
            return new EffectiveQuota(-1, null, null);

        // Probe returned no per-model breakdown at all (e.g. NullQuotaProbe, or a
        // provider whose API has no per-model dimension). Fall back to overall.
        return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null);
    }
}
