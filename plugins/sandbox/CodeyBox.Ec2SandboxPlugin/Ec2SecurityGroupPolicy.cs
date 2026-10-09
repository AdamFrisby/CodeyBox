using System.Net;
using System.Net.Sockets;

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Pure planner for the per-sandbox EC2 security group. Defence in depth
/// only: the <c>ec2</c> kind is classified NotEnforced by the host, so these
/// rules never promote the sandbox to enforced egress — they only narrow what
/// a compromised guest can reach. Placement already refuses acquisitions that
/// name a network profile; the provider refuses again if one ever reaches it.
///
/// <para>Vendor posture (documented, not claimed away): a fresh security
/// group carries a default allow-all egress rule. The provider revokes that
/// rule at creation and authorizes only the explicit egress below, so the
/// group never relies on implicit defaults. A dedicated guest kernel does not
/// imply host-enforced egress.</para>
/// </summary>
public static class Ec2SecurityGroupPolicy
{
    /// <summary>Well-known DNS port opened (TCP+UDP) toward the configured DNS IPs only.</summary>
    public const int DnsPort = 53;

    /// <summary>Well-known NTP port opened (UDP) toward the configured NTP IPs only.</summary>
    public const int NtpPort = 123;

    /// <summary>Guest SSH port opened (TCP) from the orchestrator CIDRs only.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// Plans the ingress permissions for one sandbox group: TCP/22 from each
    /// orchestrator CIDR, split into IPv4 and IPv6 ranges. Anything else is
    /// refused inbound.
    /// </summary>
    /// <exception cref="ArgumentException">A CIDR is malformed.</exception>
    /// <exception cref="InvalidOperationException">The plan exceeds <paramref name="maxRules"/>.</exception>
    public static IReadOnlyList<Ec2IpPermission> BuildIngress(
        IReadOnlyList<string> orchestratorSshCidrs, int maxRules)
    {
        ArgumentNullException.ThrowIfNull(orchestratorSshCidrs);
        if (maxRules <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRules), "Max rules must be positive.");

        var v4 = new List<string>();
        var v6 = new List<string>();
        foreach (var cidr in orchestratorSshCidrs)
        {
            var prefix = NormalizeCidr(cidr, nameof(orchestratorSshCidrs));
            if (prefix.Contains(':'))
                v6.Add(prefix);
            else
                v4.Add(prefix);
        }
        var rules = new List<Ec2IpPermission>
        {
            new("tcp", SshPort, SshPort,
                v4.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                v6.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList()),
        };
        if (rules[0].CidrRanges.Count + rules[0].Cidr6Ranges.Count == 0)
            throw new ArgumentException("At least one orchestrator SSH CIDR is required.", nameof(orchestratorSshCidrs));
        if (rules.Count > maxRules)
        {
            throw new InvalidOperationException(
                $"Security-group ingress plan needs {rules.Count} rules but MaxSecurityGroupRules={maxRules}.");
        }
        return rules;
    }

    /// <summary>
    /// Plans the explicit egress permissions for one sandbox group: DNS/NTP
    /// to the configured server IPs and full TCP+UDP egress to each IP the
    /// allowlist resolved to at create time. An empty allowlist (and empty
    /// DNS/NTP lists) means no egress beyond what is listed — nothing is
    /// opened by default.
    /// </summary>
    /// <exception cref="ArgumentException">An address is malformed.</exception>
    /// <exception cref="InvalidOperationException">The plan exceeds <paramref name="maxRules"/>.</exception>
    public static IReadOnlyList<Ec2IpPermission> BuildEgress(
        IReadOnlyList<IPAddress> allowedEgressIps,
        IReadOnlyList<string> dnsServerIps,
        IReadOnlyList<string> ntpServerIps,
        int maxRules)
    {
        ArgumentNullException.ThrowIfNull(allowedEgressIps);
        ArgumentNullException.ThrowIfNull(dnsServerIps);
        ArgumentNullException.ThrowIfNull(ntpServerIps);
        if (maxRules <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRules), "Max rules must be positive.");

        var rules = new List<Ec2IpPermission>();
        foreach (var server in dnsServerIps)
            AddHostPermission(rules, server, nameof(dnsServerIps), DnsPort, ["tcp", "udp"]);
        foreach (var server in ntpServerIps)
            AddHostPermission(rules, server, nameof(ntpServerIps), NtpPort, ["udp"]);
        var egressV4 = new SortedSet<string>(StringComparer.Ordinal);
        var egressV6 = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ip in allowedEgressIps)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                egressV6.Add(SingletonPrefix(ip));
            else
                egressV4.Add(SingletonPrefix(ip));
        }
        if (egressV4.Count > 0 || egressV6.Count > 0)
        {
            // A full-range opening must say so explicitly: "1-65535" opens
            // every port without pretending the rule is port-agnostic.
            rules.Add(new Ec2IpPermission("tcp", 1, 65535, [.. egressV4], [.. egressV6]));
            rules.Add(new Ec2IpPermission("udp", 1, 65535, [.. egressV4], [.. egressV6]));
        }

        foreach (var rule in rules)
            rule.Validate();

        if (rules.Count > maxRules)
        {
            throw new InvalidOperationException(
                $"Security-group egress plan needs {rules.Count} rules but MaxSecurityGroupRules={maxRules}: " +
                "narrow AllowedHosts or raise the bound.");
        }
        return rules;
    }

    private static void AddHostPermission(
        List<Ec2IpPermission> rules, string server, string parameterName, int port, IReadOnlyList<string> protocols)
    {
        var prefix = NormalizeAddress(server, parameterName);
        var v4 = prefix.Contains(':') ? [] : new List<string> { prefix };
        var v6 = prefix.Contains(':') ? new List<string> { prefix } : [];
        foreach (var protocol in protocols)
            rules.Add(new Ec2IpPermission(protocol, port, port, v4, v6));
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
}
