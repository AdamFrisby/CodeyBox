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
    /// </summary>
    public static bool IsEnforced(string providerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        return HostPlatformSupport.GetEgressEnforcement(providerKind)
            != EgressEnforcementLocation.NotEnforced;
    }

    /// <summary>
    /// Refuses a <see cref="EgressEnforcementLocation.NotEnforced"/> provider where
    /// <paramref name="requiredNetworkProfile"/> demands enforced egress. No-op when
    /// no profile is required or when the kind is classified enforced. The exception
    /// names the kind and states where such a provider may be used instead.
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
            _ =>
                $"provider '{kind}': egress NOT enforced — no host network isolation; " +
                $"may only serve sandboxes with no named network profile and must never be described as isolation",
        };
    }
}
