using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Outcome of resolving which subscription quota probe serves one member.
/// <see cref="Probe"/> is set for an unambiguous match; <see cref="Conflict"/>
/// is set when two or more equally specific probes claim the same member (callers
/// must fail closed for that key); both are null when no probe claims the member
/// (callers fall back to the <c>NullQuotaProbe</c> unknown path, as before).
/// </summary>
internal sealed record QuotaProbeResolution(IAgentQuotaProbe? Probe, QuotaProbeConflict? Conflict);

/// <summary>
/// Two or more equally specific probes claimed the same member. Carries the
/// contested key and the tied probe identities for the Error log and the
/// fail-closed unknown snapshot.
/// </summary>
internal sealed record QuotaProbeConflict(
    AgentQuotaMemberKey Key,
    IReadOnlyList<string> ProbeNames);

internal static class AgentQuotaProbeCatalog
{
    /// <summary>
    /// Sentinel model id used only to rank claim specificity: no real routing
    /// member carries it, so a probe that claims every model also claims this
    /// one, while a probe narrowed to specific models rejects it.
    /// </summary>
    internal const string SpecificitySentinelModelId = "\u0000__unclaimed__";

    public static IReadOnlyList<IAgentQuotaProbe> BuildSubscriptionProbes(IEnumerable<IAgentQuotaProbe> probes)
    {
        return probes
            .Where(UsesSubscriptionProbeKindLookup)
            .ToList();
    }

    private static bool UsesSubscriptionProbeKindLookup(IAgentQuotaProbe probe) =>
        probe is not PayPerApiQuotaProbe and not NullQuotaProbe;

    /// <summary>
    /// Resolves the subscription probe serving <paramref name="member"/> by
    /// member key: the probe whose <see cref="IAgentQuotaProbe.Handles"/> returns
    /// true is selected, and a probe returning false is never consulted. When no
    /// probe claims the member the resolution is empty and the caller falls back
    /// to the per-kind miss path (unknown), exactly as before.
    ///
    /// <para>
    /// When several probes claim the same member the most specific claim wins
    /// (a probe narrowed to specific models beats one serving the whole kind);
    /// equally specific competing claims are a configuration error — an Error is
    /// logged naming the probes and the contested key, and the resolution
    /// carries a <see cref="QuotaProbeConflict"/> so the caller fails closed
    /// (unknown) instead of picking an arbitrary winner. Resolution never
    /// depends on probe registration order.
    /// </para>
    /// </summary>
    public static QuotaProbeResolution ResolveSubscriptionProbe(
        IReadOnlyList<IAgentQuotaProbe> probes,
        AgentMembership member,
        ILogger log)
        => ResolveSubscriptionProbe(probes, AgentQuotaMemberKey.From(member), log);

    /// <inheritdoc cref="ResolveSubscriptionProbe(IReadOnlyList{IAgentQuotaProbe}, AgentMembership, ILogger)"/>
    public static QuotaProbeResolution ResolveSubscriptionProbe(
        IReadOnlyList<IAgentQuotaProbe> probes,
        AgentQuotaMemberKey key,
        ILogger log)
    {
        List<IAgentQuotaProbe>? claimants = null;
        foreach (var probe in probes)
        {
            bool handles;
            try
            {
                handles = probe.Handles(key);
            }
            catch (Exception ex)
            {
                // Handles must be pure and cheap; a throwing probe opts itself
                // out loudly rather than breaking resolution for every member.
                log.LogWarning(
                    ex,
                    "Quota probe {Probe} threw from Handles for {RouteKey}; treating as not handling",
                    DescribeProbe(probe),
                    DescribeKey(key));
                handles = false;
            }

            if (handles)
                (claimants ??= []).Add(probe);
        }

        if (claimants is null || claimants.Count == 0)
            return new QuotaProbeResolution(null, null);

        if (claimants.Count == 1)
            return new QuotaProbeResolution(claimants[0], null);

        var bestSpecificity = -1;
        IAgentQuotaProbe? winner = null;
        var tied = false;
        List<string>? tiedNames = null;
        foreach (var claimant in claimants)
        {
            var specificity = ClaimSpecificity(claimant, key, log);
            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                winner = claimant;
                tied = false;
                tiedNames = null;
            }
            else if (specificity == bestSpecificity)
            {
                tied = true;
                tiedNames ??= [DescribeProbe(winner!)];
                tiedNames.Add(DescribeProbe(claimant));
            }
        }

        if (!tied)
            return new QuotaProbeResolution(winner, null);

        tiedNames!.Sort(StringComparer.Ordinal);
        var conflict = new QuotaProbeConflict(key, tiedNames);
        log.LogError(
            "Conflicting quota probes {Probes} both claim quota member {Member}; failing closed (unknown) rather than picking an arbitrary winner",
            string.Join(", ", tiedNames),
            DescribeKey(key));
        return new QuotaProbeResolution(null, conflict);
    }

    /// <summary>
    /// Builds the fail-closed unknown snapshot for a probe conflict. Permanent
    /// (not transient): a configuration error means prior readings can no longer
    /// be trusted, so the last-known-good layer discards rather than retains.
    /// </summary>
    public static AgentQuotaSnapshot ConflictUnknownSnapshot(QuotaProbeConflict conflict) =>
        AgentQuotaSnapshot.UnknownSnapshot(
            QuotaUnknownReason.Permanent,
            $"conflicting quota probes claim {DescribeKey(conflict.Key)}: {string.Join(", ", conflict.ProbeNames)}");

    /// <summary>
    /// Ranks how narrowly <paramref name="probe"/> claims <paramref name="key"/>:
    /// 1 when the probe serves this member but rejects a sibling model of the
    /// same agent (narrowed to specific models), 0 when it serves the whole
    /// kind. Lets a model-specific claim deterministically beat a kind-wide one
    /// without depending on registration order.
    /// </summary>
    private static int ClaimSpecificity(IAgentQuotaProbe probe, AgentQuotaMemberKey key, ILogger log)
    {
        foreach (var siblingModel in SiblingModels(key.ModelId))
        {
            var sibling = new AgentQuotaMemberKey(key.RouteKey, key.Agent, siblingModel);
            bool handlesSibling;
            try
            {
                handlesSibling = probe.Handles(sibling);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Quota probe {Probe} threw from Handles for {RouteKey}; treating as not handling",
                    DescribeProbe(probe),
                    DescribeKey(sibling));
                handlesSibling = false;
            }

            if (!handlesSibling)
                return 1;
        }

        return 0;
    }

    private static IEnumerable<string> SiblingModels(string modelId)
    {
        if (!string.Equals(modelId, string.Empty, StringComparison.Ordinal))
            yield return string.Empty;
        if (!string.Equals(modelId, SpecificitySentinelModelId, StringComparison.Ordinal))
            yield return SpecificitySentinelModelId;
    }

    internal static string DescribeProbe(IAgentQuotaProbe probe) =>
        $"{probe.GetType().FullName} (kind {probe.Kind.Value})";

    internal static string DescribeKey(AgentQuotaMemberKey key)
    {
        var model = string.IsNullOrEmpty(key.ModelId) ? "(default)" : key.ModelId;
        return $"'{key.RouteKey}' (agent '{key.Agent.Value}', model '{model}')";
    }
}
