using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pickup-time policy for a work item whose <see cref="WorkItem.BaselineImageRef"/>
/// pin was baked from different agent executable/toolchain inputs than the live
/// configuration resolves now. The toolchain fingerprint is part of the pin
/// itself (scoped pins carry <c>tc-&lt;hash&gt;</c>; see <see cref="BaselinePin"/>),
/// so staleness is decidable by pure string comparison — no provider call.
///
/// <para>Policy (default <see cref="BaselineStalePinMode.MigrateOnToolchainAndModelChange"/>):
/// a pin whose toolchain differs from live is cleared at the next pickup so the
/// item recomputes onto the current baseline (identical semantics to
/// <c>POST /baselines/migrate</c>), but only when the dispatch model also
/// differs from the model the pin was resolved for. A CLI-only upgrade that
/// leaves the model untouched keeps its pin: the old CLI still serves the old
/// model, and forcing a re-bake would churn every in-flight item for no
/// correctness gain. A model upgrade (the incident that motivated this: a new
/// model the old CLI does not list) always migrates, because running a model
/// the baked CLI does not know fails the dispatch. Pins that predate model
/// attribution are treated as model-changed (fail safe toward migrating).</para>
///
/// <para>Set <see cref="BaselineStalePinOptions.Mode"/> to
/// <see cref="BaselineStalePinMode.MigrateOnToolchainChange"/> to migrate on any
/// toolchain drift regardless of model, or to
/// <see cref="BaselineStalePinMode.Disabled"/> to restore the legacy behaviour
/// (pins survive until a re-route or a manual migrate). Hot-reloadable.</para>
/// </summary>
public enum BaselineStalePinMode
{
    /// <summary>Legacy behaviour: pickup never clears a pin for toolchain drift.</summary>
    Disabled = 0,

    /// <summary>Clear a toolchain-stale pin at pickup regardless of model.</summary>
    MigrateOnToolchainChange = 1,

    /// <summary>
    /// Clear a toolchain-stale pin at pickup only when the dispatch model
    /// differs from the pinned model (or the pin predates model attribution).
    /// Default: migrates exactly the dangerous CLI/model combination.
    /// </summary>
    MigrateOnToolchainAndModelChange = 2,
}

/// <summary>
/// Hot-reloadable knobs for the pickup-time stale-baseline-pin policy
/// (<see cref="BaselineStalePinEvaluator"/>). Bound from
/// <c>CodeyBox:BaselineStalePin</c>; defaults migrate the dangerous
/// combination (toolchain drift plus model change) and leave everything else
/// untouched.
/// </summary>
public sealed class BaselineStalePinOptions
{
    public BaselineStalePinMode Mode { get; set; } = BaselineStalePinMode.MigrateOnToolchainAndModelChange;
}

/// <summary>
/// Pure outcome of <see cref="BaselineStalePinEvaluator.Evaluate"/>.
/// <see cref="ShouldMigrate"/> means the pin must be dropped and re-resolved
/// from live config; <see cref="RefreshModelAttribution"/> means the pin
/// itself is still valid but its recorded model is out of date and should be
/// restamped (same toolchain, different dispatch model — the re-resolve would
/// produce the identical ref, so no clearing is needed). <see cref="Reason"/>
/// is a short machine-stable token for logs and audit events.
/// </summary>
public sealed record BaselineStalePinDecision(bool ShouldMigrate, bool RefreshModelAttribution, string Reason);

/// <summary>
/// Pure decision core for the pickup-time stale-pin policy. Decides whether a
/// persisted baseline pin is still safe to dispatch on, given the live-config
/// ref and the model this pickup will dispatch with. Kept pure (no store,
/// clock, or provider) so the policy is unit-testable without infrastructure.
/// </summary>
public static class BaselineStalePinEvaluator
{
    public const string ReasonNoPin = "no-pin";
    public const string ReasonCurrent = "current";
    public const string ReasonDisabled = "policy-disabled";
    public const string ReasonAgentChanged = "agent-changed";
    public const string ReasonUnattributed = "unattributed-pin";
    public const string ReasonNoLiveBaseline = "no-live-baseline";
    public const string ReasonToolchainChanged = "toolchain-changed";
    public const string ReasonToolchainChangedModelKept = "toolchain-changed-model-kept";
    public const string ReasonLegacyRefChanged = "legacy-ref-changed";
    public const string ReasonLegacyRefChangedModelKept = "legacy-ref-changed-model-kept";

    /// <summary>
    /// Evaluates <paramref name="pinnedRef"/> against <paramref name="liveRef"/>
    /// (what the provider resolves now for this item's work/headless target;
    /// null means no baseline applies). <paramref name="dispatchModelId"/> is
    /// the model this pickup will pass to the CLI; <paramref name="pinnedModelId"/>
    /// is the model recorded when the pin was stamped (null for legacy rows).
    /// Never throws: unparseable refs degrade to legacy full-string comparison.
    /// </summary>
    public static BaselineStalePinDecision Evaluate(
        string? pinnedRef,
        AgentKind? pinnedAgent,
        string? pinnedModelId,
        string? liveRef,
        AgentKind? dispatchAgent,
        string? dispatchModelId,
        BaselineStalePinMode mode)
    {
        if (string.IsNullOrEmpty(pinnedRef))
            return new BaselineStalePinDecision(false, false, ReasonNoPin);
        if (mode == BaselineStalePinMode.Disabled)
            return new BaselineStalePinDecision(false, false, ReasonDisabled);

        if (dispatchAgent is not null && pinnedAgent != dispatchAgent)
        {
            // Mirrors ReconcileBaselinePinForDispatchAgent: a pin is bound to
            // the agent kind it was resolved under, and an unattributed legacy
            // pin cannot be matched to any dispatch agent, so neither is
            // trusted across a (re-)route.
            var reason = pinnedAgent is null ? ReasonUnattributed : ReasonAgentChanged;
            return new BaselineStalePinDecision(true, false, reason);
        }

        if (liveRef is null)
        {
            // Live config resolves no baseline (images disabled, no profile):
            // any surviving pin names a world that no longer applies.
            return new BaselineStalePinDecision(true, false, ReasonNoLiveBaseline);
        }

        if (string.Equals(pinnedRef, liveRef, StringComparison.Ordinal))
        {
            var refresh = ModelChanged(pinnedModelId, dispatchModelId);
            return new BaselineStalePinDecision(false, refresh, ReasonCurrent);
        }

        var pinnedHash = BaselinePin.TryExtractToolchainHash(pinnedRef);
        var liveHash = BaselinePin.TryExtractToolchainHash(liveRef);
        var scoped = pinnedHash is not null && liveHash is not null;

        if (scoped && string.Equals(pinnedHash, liveHash, StringComparison.Ordinal))
            return new BaselineStalePinDecision(false, ModelChanged(pinnedModelId, dispatchModelId), ReasonCurrent);

        // Refs differ and (for scoped pins) the toolchain hash differs too —
        // or at least one side is a legacy pin whose toolchain cannot be
        // proven equal, which is treated as drift (fail safe toward
        // migrating: a legacy pin's provenance is unverifiable).

        var modelGatePasses = mode == BaselineStalePinMode.MigrateOnToolchainChange
            || ModelChanged(pinnedModelId, dispatchModelId);
        if (modelGatePasses)
            return new BaselineStalePinDecision(true, false, scoped ? ReasonToolchainChanged : ReasonLegacyRefChanged);

        return new BaselineStalePinDecision(
            false,
            false,
            scoped ? ReasonToolchainChangedModelKept : ReasonLegacyRefChangedModelKept);
    }

    /// <summary>
    /// True when the dispatch model differs from the pinned model. A null
    /// pinned model (rows predating attribution) counts as changed — an
    /// unverifiable pin must not anchor the dangerous combination.
    /// Comparison is exact ordinal: model ids are opaque CLI tokens.
    /// </summary>
    public static bool ModelChanged(string? pinnedModelId, string? dispatchModelId)
    {
        if (pinnedModelId is null)
            return true;
        return !string.Equals(pinnedModelId, dispatchModelId, StringComparison.Ordinal);
    }
}
