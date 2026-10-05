using System.Net;
using Microsoft.Extensions.Logging;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Pure Softnet policy core: argv shapes and allowlist assembly. All DNS and
/// process I/O lives in the provider; this type only transforms resolved
/// addresses into the exact <c>tart run</c> flags, so tests assert values the
/// production path really emits.
/// </summary>
public static class TartSoftnetPolicy
{
    /// <summary>Flag attaching the Softnet packet filter to <c>tart run</c>.</summary>
    public const string NetSoftnetFlag = "--net-softnet";

    /// <summary>Block-all default installed in Softnet mode.</summary>
    public const string BlockAllCidr = "0.0.0.0/0";

    /// <summary>Prefix of the block flag (<c>--net-softnet-block=&lt;cidr&gt;</c>).</summary>
    public const string BlockFlagPrefix = "--net-softnet-block=";

    /// <summary>Prefix of the allow flag (<c>--net-softnet-allow=&lt;cidr,...&gt;</c>).</summary>
    public const string AllowFlagPrefix = "--net-softnet-allow=";

    /// <summary>
    /// Builds the <c>tart run</c> Softnet flags for one sandbox: attach the
    /// filter, block all IPv4, then allow exactly the gateway (DHCP/DNS) plus
    /// the resolved <c>/32</c>s. One argv element per flag — never a shell
    /// string — and never the <c>@host</c> keyword or the Mac's LAN.
    /// </summary>
    public static IReadOnlyList<string> BuildRunFlags(IReadOnlyList<string> allowCidrs)
    {
        ArgumentNullException.ThrowIfNull(allowCidrs);
        if (allowCidrs.Count == 0)
            throw new ArgumentException("Softnet allowlist must at least carry the vmnet gateway CIDR.", nameof(allowCidrs));
        return [NetSoftnetFlag, BlockFlagPrefix + BlockAllCidr, AllowFlagPrefix + string.Join(",", allowCidrs)];
    }

    /// <summary>
    /// Assembles the allowlist: the vmnet gateway CIDR first (the guest's
    /// DHCP/DNS path — an empty acquisition allowlist still resolves to just
    /// this), then the resolved <c>/32</c>s sorted ordinally for a
    /// deterministic argv. Enforces the configured CIDR bound fail-closed.
    /// Only global-unicast IPv4 addresses are admitted: loopback, private
    /// (RFC1918), link-local/cloud-metadata, CGNAT, multicast, reserved and
    /// unspecified addresses are dropped here as defense-in-depth (the
    /// provider also logs-and-skips them per host). The gateway CIDR is the
    /// sole exception — it is the vmnet network's own address by design.
    /// </summary>
    public static IReadOnlyList<string> BuildAllowCidrs(
        IReadOnlyList<IPAddress> resolvedIPv4,
        string gatewayCidr,
        int maxAllowCidrs)
    {
        ArgumentNullException.ThrowIfNull(resolvedIPv4);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayCidr);
        if (maxAllowCidrs < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAllowCidrs));
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ip in resolvedIPv4)
        {
            if (ip is null)
                continue;
            if (!IsGlobalUnicastIPv4(ip))
                continue;
            distinct.Add(ip.ToString() + "/32");
        }
        if (distinct.Count > maxAllowCidrs)
            throw new InvalidOperationException(
                $"Softnet allowlist resolves to {distinct.Count} CIDRs, over the {maxAllowCidrs}-CIDR bound " +
                $"(CodeyBox:Plugins:{TartSandboxOptions.PluginId}:Network:MaxAllowCidrs). " +
                $"Reduce the acquisition's AllowedHosts or raise the bound.");
        var allow = new List<string>(distinct.Count + 1) { gatewayCidr.Trim() };
        allow.AddRange(distinct);
        return allow;
    }

    /// <summary>
    /// Logs and skips one unresolvable acquisition host. The allowlist stays
    /// fail-closed per host (skip, never widen) while the create proceeds.
    /// </summary>
    public static void LogSkippedHost(ILogger log, string host, string reason)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.LogWarning(
            "Tart Softnet: skipping unresolvable AllowedHosts entry '{Host}': {Reason}. " +
            "The sandbox keeps its block-all default for this host.",
            host, reason);
    }

    /// <summary>
    /// True only for globally-routable IPv4 unicast addresses — the only
    /// addresses DNS answers may contribute to the Softnet allowlist. Drops
    /// loopback (127/8), RFC1918 private (10/8, 172.16/12, 192.168/16),
    /// link-local and cloud-metadata (169.254/16), CGNAT (100.64/10),
    /// multicast (224/4), reserved (240/4, including broadcast), unspecified
    /// (0/8), IETF/test documentation ranges and benchmarking space. Mirrors
    /// the Linux host's nftables drop list (scripts/setup-host-networks.sh)
    /// while the vmnet gateway stays allowed via the GatewayCidr knob.
    /// A poisoned or hijacked DNS answer must never plant an allow for
    /// host-internal targets, and a resolved Mac-LAN address must never
    /// re-open the LAN access Softnet mode forbids blanket-allowing.
    /// </summary>
    public static bool IsGlobalUnicastIPv4(IPAddress ip)
    {
        if (ip is null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        if (b.Length != 4)
            return false;
        if (b[0] == 0)
            return false;
        if (b[0] == 10)
            return false;
        if (b[0] == 127)
            return false;
        if (b[0] == 169 && b[1] == 254)
            return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return false;
        if (b[0] == 192 && b[1] == 168)
            return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            return false;
        if (b[0] >= 224)
            return false;
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
            return false;
        if (b[0] == 198 && ((b[1] == 18) || (b[1] == 51 && b[2] == 100)))
            return false;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113)
            return false;
        return true;
    }
}
