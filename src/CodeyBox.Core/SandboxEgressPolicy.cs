namespace CodeyBox.Core;

/// <summary>
/// Host-owned consequences of the egress-enforcement classification in
/// <see cref="HostPlatformSupport.GetEgressEnforcement"/>.
///
/// <para>A provider classified <see cref="EgressEnforcementLocation.NotEnforced"/>
/// — every plugin-contributed kind, plus the built-in <c>bubblewrap</c> and
/// <c>process</c> runners — performs no host-kernel egress filtering. Such a
/// provider may serve sandboxes that carry no enforced-egress guarantee (no named
/// network profile: the default denied/loopback posture), and must be refused
/// wherever a named network profile's allowlist is required, because that
/// allowlist only exists as host-side nftables rules the provider never attaches
/// to. The refusal is explicit and names the kind; a <c>NotEnforced</c> provider
/// is never quietly used where enforcement was required.</para>
///
/// <para>The classification itself stays host-owned: <see cref="SandboxEgressPolicy"/>
/// only reads <see cref="HostPlatformSupport.GetEgressEnforcement"/>, whose switch
/// names exactly the reviewed in-tree kinds. Nothing a plugin returns or configures
/// can promote it to an enforced classification.</para>
///
/// <para>One host-owned exception exists: a <c>NotEnforced</c> kind the operator
/// opted in through <c>CodeyBox:EgressVerification:Kinds</c> may serve profiled
/// work, but only per sandbox and only after the host's canary verification
/// passes for that sandbox (<see cref="EgressEnforcementLocation.EnforcedOnProviderHostVerified"/>).
/// That promotion is decided here and in placement — never by the plugin — and
/// the verified value is deliberately never stronger than
/// <see cref="EgressEnforcementLocation.EnforcedOnOrchestratorHost"/> (see
/// <see cref="EnforcementRank"/>).</para>
/// </summary>
public static class SandboxEgressPolicy
{
    /// <summary>
    /// True when serving <paramref name="requiredNetworkProfile"/> carries an
    /// enforced-egress guarantee: any named (non-blank) profile maps to a host
    /// bridge whose allowlist is enforced by host nftables. A blank/missing
    /// profile is the default posture and requires no enforcement.
    /// </summary>
    public static bool RequiresEnforcedEgress(string? requiredNetworkProfile) =>
        !string.IsNullOrWhiteSpace(requiredNetworkProfile);

    /// <summary>
    /// True when the host classifies <paramref name="providerKind"/> as enforcing
    /// egress (on the orchestrator host or on a remote executor host).
    /// Comparison is exact on the normalised kind; unknown and plugin kinds report
    /// <see cref="EgressEnforcementLocation.NotEnforced"/> and return false.
    /// This is the static classification only: it never reflects per-sandbox
    /// canary verification (see <see cref="IsEffectivelyEnforced"/>).
    /// </summary>
    public static bool IsEnforced(string providerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        return HostPlatformSupport.GetEgressEnforcement(providerKind)
            != EgressEnforcementLocation.NotEnforced;
    }

    /// <summary>
    /// True when <paramref name="providerKind"/> is operator-opted-in for
    /// per-sandbox canary verification AND not inside a failure cool-down.
    /// A null gate means verification is unavailable and returns false.
    /// Opt-in alone is not enforcement — the sandbox still needs its own
    /// passing canary before it serves profiled work.
    /// </summary>
    public static bool IsVerificationEligible(string providerKind, EgressVerificationGate? gate)
    {
        if (string.IsNullOrWhiteSpace(providerKind) || gate is null)
            return false;
        return gate.IsKindEligible(providerKind);
    }

    /// <summary>
    /// Effective enforcement for one placement decision: the static
    /// classification, except a statically-<c>NotEnforced</c> kind that is
    /// verification-eligible resolves to
    /// <see cref="EgressEnforcementLocation.EnforcedOnProviderHostVerified"/>
    /// (still gated on that sandbox's canary before handoff). A null gate
    /// returns the static classification unchanged.
    /// </summary>
    public static EgressEnforcementLocation EffectiveEnforcement(string providerKind, EgressVerificationGate? gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        var location = HostPlatformSupport.GetEgressEnforcement(providerKind);
        if (location != EgressEnforcementLocation.NotEnforced)
            return location;
        return IsVerificationEligible(providerKind, gate)
            ? EgressEnforcementLocation.EnforcedOnProviderHostVerified
            : EgressEnforcementLocation.NotEnforced;
    }

    /// <summary>
    /// True when <paramref name="providerKind"/> may serve profiled work in
    /// this placement decision: statically enforced, or verification-eligible
    /// (whose sandbox still needs its canary before handoff).
    /// </summary>
    public static bool IsEffectivelyEnforced(string providerKind, EgressVerificationGate? gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        return EffectiveEnforcement(providerKind, gate) != EgressEnforcementLocation.NotEnforced;
    }

    /// <summary>
    /// Placement rank for an enforcement location, lowest first. Both static
    /// host-enforced values outrank the per-sandbox verified value — a
    /// verified provider-host filter is never preferred over, and never
    /// described as stronger than, orchestrator-host nftables enforcement —
    /// and <c>NotEnforced</c> ranks last. Workload-trust routing must use
    /// this order (verified never above orchestrator-host).
    /// </summary>
    public static int EnforcementRank(EgressEnforcementLocation location) => location switch
    {
        EgressEnforcementLocation.EnforcedOnOrchestratorHost => 0,
        EgressEnforcementLocation.EnforcedOnRemoteExecutorHost => 0,
        EgressEnforcementLocation.EnforcedOnProviderHostVerified => 1,
        EgressEnforcementLocation.NotEnforced => 2,
        _ => 2,
    };

    /// <summary>
    /// Refuses a <see cref="EgressEnforcementLocation.NotEnforced"/> provider where
    /// <paramref name="requiredNetworkProfile"/> demands enforced egress. No-op when
    /// no profile is required or when the kind is classified enforced. The exception
    /// names the kind and states where such a provider may be used instead.
    /// This is the static sink guard: it cannot see per-sandbox canary state,
    /// so it still refuses opted-in kinds here. The per-sandbox verified path
    /// lives in placement (<c>SandboxPlacementAcquirer</c>), which translates
    /// the profile and runs the canary before handoff.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a named network profile is required but
    /// <paramref name="providerKind"/> is classified <c>NotEnforced</c>.
    /// </exception>
    public static void EnsureEnforcedEgressForProfile(string providerKind, string? requiredNetworkProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        if (!RequiresEnforcedEgress(requiredNetworkProfile))
            return;
        if (IsEnforced(providerKind))
            return;
        var kind = providerKind.Trim().ToLowerInvariant();
        var profile = requiredNetworkProfile!.Trim();
        throw new InvalidOperationException(
            $"Sandbox provider kind '{kind}' cannot serve network profile '{profile}': " +
            $"the kind is classified '{EgressEnforcementLocation.NotEnforced}' (no host-enforced egress filtering), " +
            $"but profile '{profile}' requires enforced egress. " +
            $"A '{EgressEnforcementLocation.NotEnforced}' provider may only serve sandboxes with no named network profile. " +
            $"Use a provider with enforced egress (incus, multipass, multipass-remote, sprites) for profiled work, " +
            $"or remove the network-profile requirement.");
    }

    /// <summary>
    /// One-line, operator-readable description of where egress enforcement lives for
    /// <paramref name="providerKind"/>, for startup logging. Never returns null.
    /// The static classification never yields the verified value; use
    /// <see cref="DescribeEffectiveEnforcement"/> when a verification gate applies.
    /// </summary>
    public static string DescribeEgressEnforcement(string providerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        var kind = providerKind.Trim().ToLowerInvariant();
        return HostPlatformSupport.GetEgressEnforcement(providerKind) switch
        {
            EgressEnforcementLocation.EnforcedOnOrchestratorHost =>
                $"provider '{kind}': egress enforced on the orchestrator host (Linux nftables bridges; see scripts/setup-host-networks.sh)",
            EgressEnforcementLocation.EnforcedOnRemoteExecutorHost =>
                $"provider '{kind}': egress enforced on the remote Linux executor host (the executor must have scripts/setup-host-networks.sh applied)",
            EgressEnforcementLocation.EnforcedOnProviderHostVerified =>
                $"provider '{kind}': egress enforced by a provider-host filter outside the guest, granted only per sandbox after the host's canary verification passes " +
                $"(opt in through CodeyBox:EgressVerification:Kinds; never stronger than orchestrator-host enforcement)",
            _ =>
                $"provider '{kind}': egress NOT enforced — no host network isolation; " +
                $"may only serve sandboxes with no named network profile and must never be described as isolation",
        };
    }

    /// <summary>
    /// One-line description of the effective enforcement for
    /// <paramref name="providerKind"/> given <paramref name="gate"/>, for
    /// startup logging. Never returns null.
    /// </summary>
    public static string DescribeEffectiveEnforcement(string providerKind, EgressVerificationGate? gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        var kind = providerKind.Trim().ToLowerInvariant();
        return EffectiveEnforcement(providerKind, gate) switch
        {
            EgressEnforcementLocation.EnforcedOnOrchestratorHost =>
                $"provider '{kind}': egress enforced on the orchestrator host (Linux nftables bridges; see scripts/setup-host-networks.sh)",
            EgressEnforcementLocation.EnforcedOnRemoteExecutorHost =>
                $"provider '{kind}': egress enforced on the remote Linux executor host (the executor must have scripts/setup-host-networks.sh applied)",
            EgressEnforcementLocation.EnforcedOnProviderHostVerified =>
                $"provider '{kind}': egress enforced by a provider-host filter outside the guest, granted only per sandbox after the host's canary verification passes " +
                $"(opted in through CodeyBox:EgressVerification:Kinds; never stronger than orchestrator-host enforcement)",
            _ =>
                $"provider '{kind}': egress NOT enforced — no host network isolation; " +
                $"may only serve sandboxes with no named network profile and must never be described as isolation",
        };
    }
}
