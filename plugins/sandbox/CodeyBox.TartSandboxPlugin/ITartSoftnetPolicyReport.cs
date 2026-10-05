using CodeyBox.Core;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Optional sandbox capability reporting the effective Softnet egress policy
/// for one Tart VM: the network mode plus the exact CIDR allowlist installed
/// on its <c>tart run</c> command line. The host verifier (and operators via
/// logs) read this through <c>SandboxCapability.Find</c>; it never changes
/// the host's <c>NotEnforced</c> classification.
/// </summary>
public interface ITartSoftnetPolicyReport : ISandbox
{
    /// <summary>Guest-network backend the VM was launched with.</summary>
    TartNetworkMode NetworkMode { get; }

    /// <summary>
    /// Exact CIDR allowlist passed via <c>--net-softnet-allow</c> (gateway
    /// first, then resolved <c>/32</c>s), or empty in NAT mode.
    /// </summary>
    IReadOnlyList<string> EffectiveAllowCidrs { get; }
}
