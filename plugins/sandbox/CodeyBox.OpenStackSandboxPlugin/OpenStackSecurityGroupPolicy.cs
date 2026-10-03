using System.Net;
using System.Net.Sockets;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Pure planner for the per-sandbox Neutron security group. Defence in depth
/// only: the <c>openstack</c> kind is classified NotEnforced by the host
/// (exactly like <c>daytona</c>), so these rules never promote the sandbox to
/// enforced egress — they only narrow what a compromised guest can reach.
/// Placement already refuses acquisitions that name a network profile; the
/// provider refuses again if one ever reaches it.
/// </summary>
public static class OpenStackSecurityGroupPolicy
{
    /// <summary>Well-known DNS port opened (TCP+UDP) toward the configured DNS IPs only.</summary>
    public const int DnsPort = 53;

    /// <summary>Well-known NTP port opened (UDP) toward the configured NTP IPs only.</summary>
    public const int NtpPort = 123;

    /// <summary>Guest SSH port opened (TCP) from the orchestrator CIDRs only.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// Plans the full rule set for one sandbox security group: SSH ingress
    /// from the orchestrator CIDRs, DNS/NTP egress to the configured server
    /// IPs, and full TCP+UDP egress to each IP the allowlist resolved to at
    /// create time. An empty allowlist (and empty DNS/NTP lists) means no
    /// egress beyond what is listed — nothing is opened by default.
    /// </summary>
    /// <exception cref="ArgumentException">A CIDR or address is malformed.</exception>
    /// <exception cref="InvalidOperationException">The plan exceeds <paramref name="maxRules"/>.</exception>
    public static IReadOnlyList<OpenStackSecurityGroupRuleSpec> BuildRules(
        string securityGroupId,
        IReadOnlyList<string> orchestratorSshCidrs,
        IReadOnlyList<IPAddress> allowedEgressIps,
        IReadOnlyList<string> dnsServerIps,
        IReadOnlyList<string> ntpServerIps,
        int maxRules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityGroupId);
        ArgumentNullException.ThrowIfNull(orchestratorSshCidrs);
        ArgumentNullException.ThrowIfNull(allowedEgressIps);
        ArgumentNullException.ThrowIfNull(dnsServerIps);
        ArgumentNullException.ThrowIfNull(ntpServerIps);
        if (maxRules <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRules), "Max rules must be positive.");

        var rules = new List<OpenStackSecurityGroupRuleSpec>();
        foreach (var cidr in orchestratorSshCidrs)
        {
            var prefix = NormalizeCidr(cidr, nameof(orchestratorSshCidrs));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "ingress", EthertypeFor(prefix), "tcp", SshPort, SshPort, prefix));
        }
        foreach (var server in dnsServerIps)
        {
            var prefix = NormalizeAddress(server, nameof(dnsServerIps));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", EthertypeFor(prefix), "udp", DnsPort, DnsPort, prefix));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", EthertypeFor(prefix), "tcp", DnsPort, DnsPort, prefix));
        }
        foreach (var server in ntpServerIps)
        {
            var prefix = NormalizeAddress(server, nameof(ntpServerIps));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", EthertypeFor(prefix), "udp", NtpPort, NtpPort, prefix));
        }
        var egressIps = allowedEgressIps
            .Select(ip => SingletonPrefix(ip))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToList();
        foreach (var prefix in egressIps)
        {
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", EthertypeFor(prefix), "tcp", null, null, prefix));
            rules.Add(new OpenStackSecurityGroupRuleSpec(
                securityGroupId, "egress", EthertypeFor(prefix), "udp", null, null, prefix));
        }

        foreach (var rule in rules)
            rule.Validate();

        var distinct = rules.Distinct().ToList();
        if (distinct.Count > maxRules)
        {
            throw new InvalidOperationException(
                $"Security-group plan needs {distinct.Count} rules but MaxEgressRules={maxRules}: " +
                "narrow AllowedHosts or raise the bound.");
        }
        return distinct;
    }

    /// <summary>Normalizes a CIDR string (bare IPs become /32 or /128 singletons).</summary>
    /// <exception cref="ArgumentException">The value is not a CIDR or bare IP.</exception>
    public static string NormalizeCidr(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("CIDR must not be blank.", parameterName);
        var cidr = value.Trim();
        if (!cidr.Contains('/'))
        {
            if (!IPAddress.TryParse(cidr, out var bare))
                throw new ArgumentException($"'{cidr}' is not an IP address or CIDR.", parameterName);
            return SingletonPrefix(bare);
        }
        var slash = cidr.IndexOf('/');
        var addressText = cidr[..slash];
        var bitsText = cidr[(slash + 1)..];
        if (!IPAddress.TryParse(addressText, out var address)
            || !int.TryParse(bitsText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var bits))
            throw new ArgumentException($"'{cidr}' is not a CIDR (address/bits).", parameterName);
        var maxBits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (bits < 0 || bits > maxBits)
            throw new ArgumentException($"'{cidr}' prefix length is out of range.", parameterName);
        return address + "/" + bits;
    }

    private static string NormalizeAddress(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var address))
            throw new ArgumentException($"'{value}' is not an IP address.", parameterName);
        return SingletonPrefix(address);
    }

    internal static string SingletonPrefix(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? address + "/128"
            : address + "/32";

    private static string EthertypeFor(string cidrPrefix) =>
        cidrPrefix.Contains(':') ? "IPv6" : "IPv4";
}
