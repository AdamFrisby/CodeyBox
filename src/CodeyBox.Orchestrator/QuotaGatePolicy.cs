using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared quota gate for work routing, audit routing, retry reset scheduling,
/// and status surfaces. It owns the floor/ramp/window policy so callers cannot
/// drift on per-agent overrides.
/// </summary>
public sealed class QuotaGatePolicy
{
    private const string AutoModelSentinel = "auto";
    private readonly QuotaRouterOptions _options;

    public QuotaGatePolicy(QuotaRouterOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// The unknown-quota policy the gate applies. Exposed so callers that wrap
    /// the policy (e.g. <see cref="QuotaGateAvailability"/>) can mirror the
    /// router's dispatch semantics for consulting <see cref="IQuotaFailureStore"/>:
    /// dispatch only checks observed failures when quota is unknown and the
    /// policy is <see cref="QuotaUnknownPolicy.UseObservedFailures"/>.
    /// </summary>
    public QuotaUnknownPolicy UnknownPolicy => _options.UnknownPolicy;

    public QuotaGateDecision Evaluate(
        AgentMembership member,
        EffectiveQuota quota,
        DateTimeOffset nowUtc,
        bool recentObservedFailure = false,
        string? observedFailureReason = null) =>
        Evaluate(_options, member, quota, nowUtc, recentObservedFailure, observedFailureReason);

    public double ComputeEffectiveFloorPct(
        AgentKind agent,
        DateTimeOffset? resetAt,
        DateTimeOffset nowUtc) =>
        ComputeEffectiveFloorPct(_options, agent, resetAt, nowUtc);

    public double ComputeEffectiveFloorPct(
        AgentKind agent,
        EffectiveQuota quota,
        DateTimeOffset nowUtc) =>
        ComputeEffectiveFloorPct(_options, agent, quota, nowUtc);

    public double ResolveWindowFloorPct(AgentKind agent, string windowName) =>
        ResolveWindowFloorPct(_options, agent, windowName);

    public static QuotaGateDecision Evaluate(
        QuotaRouterOptions options,
        AgentMembership member,
        EffectiveQuota quota,
        DateTimeOffset nowUtc,
        bool recentObservedFailure = false,
        string? observedFailureReason = null)
    {
        if (recentObservedFailure)
        {
            return new QuotaGateDecision(
                false,
                observedFailureReason ?? "recent observed quota failure");
        }

        var floor = ComputeFloorPct(options, member, quota, nowUtc);
        var availablePct = quota.AvailablePct;
        if (availablePct >= floor)
        {
            if (member.Billing == AgentBilling.Subscription
                && quota.Windows is { Count: > 0 } windows)
            {
                foreach (var window in windows)
                {
                    if (window.AvailablePct < 0) continue;
                    var windowFloor = ResolveWindowFloorPct(options, member.Agent, window.Name);
                    if (window.AvailablePct < windowFloor)
                    {
                        return new QuotaGateDecision(
                            false,
                            $"quota below window floor ({window.Name}: {window.AvailablePct:F1}% < {windowFloor:F1}%)",
                            windowFloor,
                            window.Name);
                    }
                }
            }

            return new QuotaGateDecision(true, "quota available", floor);
        }

        if (availablePct >= 0)
            return new QuotaGateDecision(false, $"quota below floor ({availablePct:F1}% < {floor:F1}%)", floor);

        return options.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => new QuotaGateDecision(true, "quota unknown; fail-open", floor),
            QuotaUnknownPolicy.FailCautious => new QuotaGateDecision(false, "quota unknown; fail-cautious", floor),
            _ => new QuotaGateDecision(true, "quota unknown; no recent observed failure", floor),
        };
    }

    public static double ComputeEffectiveFloorPct(
        QuotaRouterOptions options,
        AgentKind agent,
        DateTimeOffset? resetAt,
        DateTimeOffset nowUtc) =>
        ComputeRampedFloor(ResolveFloorSettings(options, agent), resetAt, nowUtc);

    /// <summary>
    /// Window-aware overload: when <paramref name="quota"/> surfaces per-window
    /// readings, the ramp is keyed off the window whose reset is the latest
    /// (the long/weekly window), not the overall binding reset. Without this
    /// selection an agent whose binding window is shorter than
    /// <see cref="QuotaRouterOptions.RampWindow"/> (e.g. Claude's 5h cap
    /// against the global 7d ramp) would always see <c>untilReset &lt;&lt;
    /// RampWindow</c>, collapsing the linear ramp to <see cref="QuotaRouterOptions.EndFloorPct"/>
    /// every cycle and defeating the early-week oversight reservation
    /// <see cref="QuotaRouterOptions.StartFloorPct"/> exists for.
    /// </summary>
    public static double ComputeEffectiveFloorPct(
        QuotaRouterOptions options,
        AgentKind agent,
        EffectiveQuota? quota,
        DateTimeOffset nowUtc)
    {
        var settings = ResolveFloorSettings(options, agent);
        var rampReset = SelectRampResetAt(quota);
        return ComputeRampedFloor(settings, rampReset, nowUtc);
    }

    private static double ComputeRampedFloor(
        AgentFloorSettings settings,
        DateTimeOffset? resetAt,
        DateTimeOffset nowUtc)
    {
        if (resetAt is not { } reset) return settings.MinQuotaPct;
        if (settings.RampWindow <= TimeSpan.Zero) return settings.MinQuotaPct;

        var untilReset = reset - nowUtc;
        var fractionElapsed = 1.0 - untilReset.TotalSeconds / settings.RampWindow.TotalSeconds;
        if (double.IsNaN(fractionElapsed) || double.IsInfinity(fractionElapsed))
            return settings.MinQuotaPct;
        fractionElapsed = Math.Clamp(fractionElapsed, 0.0, 1.0);

        var floor = settings.StartFloorPct + (settings.EndFloorPct - settings.StartFloorPct) * fractionElapsed;
        var lo = Math.Min(settings.StartFloorPct, settings.EndFloorPct);
        var hi = Math.Max(settings.StartFloorPct, settings.EndFloorPct);
        return Math.Clamp(floor, lo, hi);
    }

    /// <summary>
    /// Pick the <see cref="WindowQuota.ResetAt"/> that the ramp should key
    /// off. When the snapshot surfaces multiple windows (e.g. Claude's
    /// <c>five_hour</c> + <c>seven_day</c>), the latest reset is the long
    /// window — which is the one whose nominal length corresponds to
    /// <see cref="QuotaRouterOptions.RampWindow"/>. Falls back to the
    /// overall <see cref="EffectiveQuota.ResetAt"/> when no per-window
    /// readings are available (preserves codex-shape behaviour, where the
    /// binding window already matches the ramp horizon and overall ResetAt
    /// is already the weekly).
    /// </summary>
    private static DateTimeOffset? SelectRampResetAt(EffectiveQuota? quota)
    {
        if (quota is null) return null;
        if (quota.Windows is not { Count: > 0 } windows) return quota.ResetAt;

        DateTimeOffset? latest = null;
        foreach (var window in windows)
        {
            if (window.ResetAt is not { } reset) continue;
            if (latest is null || reset > latest.Value) latest = reset;
        }
        return latest ?? quota.ResetAt;
    }

    public static double ResolveWindowFloorPct(
        QuotaRouterOptions options,
        AgentKind agent,
        string windowName)
    {
        var settings = ResolveFloorSettings(options, agent);
        if (TryGetFloorOverride(options, agent, out var perAgent)
            && perAgent?.MinQuotaPct is { } agentMin)
            return agentMin;

        if (string.IsNullOrEmpty(windowName)) return settings.MinQuotaPct;
        if (options.MinQuotaPctByWindow is { } overrides
            && overrides.TryGetValue(windowName, out var perWindow))
            return perWindow;
        return settings.MinQuotaPct;
    }

    public static DateTimeOffset? ResolveResetHint(EffectiveQuota quota, QuotaGateDecision decision)
    {
        if (!string.IsNullOrEmpty(decision.WindowName)
            && quota.Windows is { Count: > 0 } windows)
        {
            foreach (var window in windows)
            {
                if (string.Equals(window.Name, decision.WindowName, StringComparison.OrdinalIgnoreCase))
                    return window.ResetAt ?? quota.ResetAt;
            }
        }

        return quota.ResetAt;
    }

    public static EffectiveQuota ResolveMemberQuota(AgentQuotaSnapshot snapshot, AgentMembership member)
    {
        if (string.IsNullOrWhiteSpace(member.ModelId))
            return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null, snapshot.Windows, snapshot.Unknown);

        if (snapshot.PerModel.TryGetValue(member.ModelId, out var modelQuota))
            return new EffectiveQuota(
                modelQuota.AvailablePct, modelQuota.ResetAt, modelQuota.Window,
                modelQuota.Windows.Count > 0 ? modelQuota.Windows : snapshot.Windows);

        if (string.Equals(member.ModelId, AutoModelSentinel, StringComparison.OrdinalIgnoreCase)
            && snapshot.PerModel.Count > 0)
        {
            ModelQuota? best = null;
            foreach (var quota in snapshot.PerModel.Values)
            {
                if (best is null || quota.AvailablePct > best.AvailablePct)
                    best = quota;
            }

            DateTimeOffset? earliestReset = null;
            foreach (var quota in snapshot.PerModel.Values)
            {
                if (quota.ResetAt is { } resetAt && (earliestReset is null || resetAt < earliestReset))
                    earliestReset = resetAt;
            }

            return new EffectiveQuota(best!.AvailablePct, earliestReset, best.Window, snapshot.Windows);
        }

        // Unknown model id on a probe that DOES provide per-model data — the operator
        // configured a model the probe has no signal for. Fail safe: surface as
        // unknown so QuotaUnknownPolicy gates it, rather than silently falling back
        // to the overall account percentage.
        if (snapshot.PerModel.Count > 0)
            return new EffectiveQuota(-1, null, null, Unknown: QuotaUnknownReason.Permanent);

        return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null, snapshot.Windows, snapshot.Unknown);
    }

    private static double ComputeFloorPct(
        QuotaRouterOptions options,
        AgentMembership member,
        EffectiveQuota quota,
        DateTimeOffset nowUtc) =>
        member.Billing == AgentBilling.Subscription
            ? ComputeEffectiveFloorPct(options, member.Agent, quota, nowUtc)
            : options.MinQuotaPct;

    private static AgentFloorSettings ResolveFloorSettings(QuotaRouterOptions options, AgentKind agent)
    {
        var perAgent = TryGetFloorOverride(options, agent, out var overrideOptions)
            ? overrideOptions
            : null;
        return new AgentFloorSettings(
            MinQuotaPct: perAgent?.MinQuotaPct ?? options.MinQuotaPct,
            StartFloorPct: perAgent?.StartFloorPct ?? options.StartFloorPct,
            EndFloorPct: perAgent?.EndFloorPct ?? options.EndFloorPct,
            RampWindow: ResolveRampWindow(options, agent, perAgent));
    }

    private static TimeSpan ResolveRampWindow(
        QuotaRouterOptions options,
        AgentKind agent,
        QuotaFloorOverrideOptions? perAgent)
    {
        if (perAgent?.RampWindow is { } rampWindow)
            return rampWindow;
        return GetRampWindow(options, agent);
    }

    private static bool TryGetFloorOverride(
        QuotaRouterOptions options,
        AgentKind agent,
        out QuotaFloorOverrideOptions? overrideOptions)
    {
        overrideOptions = null;
        if (string.IsNullOrEmpty(agent.Value)) return false;
        return options.FloorByAgent is { } overrides
            && overrides.TryGetValue(agent.Value, out overrideOptions);
    }

    private static TimeSpan GetRampWindow(QuotaRouterOptions options, AgentKind agent)
    {
        if (!string.IsNullOrEmpty(agent.Value)
            && options.RampWindowByAgent is { } overrides
            && overrides.TryGetValue(agent.Value, out var perAgent)
            && perAgent > TimeSpan.Zero)
            return perAgent;
        return options.RampWindow;
    }

    private readonly record struct AgentFloorSettings(
        double MinQuotaPct,
        double StartFloorPct,
        double EndFloorPct,
        TimeSpan RampWindow);
}

public sealed class QuotaGateAvailability : IAgentQuotaGate
{
    private readonly QuotaGatePolicy _policy;
    private readonly IQuotaFailureStore? _failureStore;
    private readonly TimeSpan _observedFailureWindow;

    public QuotaGateAvailability(QuotaGatePolicy policy)
        : this(policy, failureStore: null, observedFailureWindow: TimeSpan.Zero) { }

    public QuotaGateAvailability(
        QuotaGatePolicy policy,
        IQuotaFailureStore? failureStore,
        TimeSpan observedFailureWindow)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _failureStore = failureStore;
        _observedFailureWindow = observedFailureWindow;
    }

    public bool Allows(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        bool recentObservedFailure = false,
        string? observedFailureReason = null)
    {
        var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
        return _policy.Evaluate(
            member,
            quota,
            nowUtc,
            recentObservedFailure,
            observedFailureReason).Allow;
    }

    public async Task<bool> AllowsAsync(
        AgentMembership member,
        AgentQuotaSnapshot snapshot,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var recentObservedFailure = false;
        string? observedFailureReason = null;

        // Mirror AgentClassRouter.EvaluateGateAsync: observed failures only
        // gate when quota is unknown AND the policy is UseObservedFailures.
        // Without this branch, a stale recent failure would deny a member that
        // a fresh probe shows as healthy — diverging from the dispatch path.
        var quota = QuotaGatePolicy.ResolveMemberQuota(snapshot, member);
        if (_failureStore is not null
            && _observedFailureWindow > TimeSpan.Zero
            && !quota.IsKnown
            && _policy.UnknownPolicy == QuotaUnknownPolicy.UseObservedFailures)
        {
            var observedAt = await _failureStore.GetMostRecentAsync(
                member.Agent, member.ModelId, _observedFailureWindow, nowUtc, ct);
            if (observedAt is { } seenAt)
            {
                recentObservedFailure = true;
                observedFailureReason = $"quota unknown; observed quota failure at {seenAt:O}";
            }
        }

        return _policy.Evaluate(member, quota, nowUtc, recentObservedFailure, observedFailureReason).Allow;
    }
}

public sealed record QuotaGateDecision(
    bool Allow,
    string Reason,
    double? FloorPct = null,
    string? WindowName = null);
