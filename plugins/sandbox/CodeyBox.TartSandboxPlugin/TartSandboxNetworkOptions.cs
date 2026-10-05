using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Hot-reloadable guest-network knobs for the Tart provider, bound from
/// <c>CodeyBox:Plugins:codeybox.tart-sandbox:Network</c>. <c>Mode=nat</c> is
/// today's behaviour; <c>Mode=softnet</c> attaches Softnet with a block-all
/// default plus the acquisition allowlist resolved to IPv4 <c>/32</c>s.
/// </summary>
public sealed record TartSandboxNetworkOptions
{
    /// <summary>Default vmnet gateway CIDR (the guest's DHCP/DNS path) when unconfigured.</summary>
    public const string DefaultGatewayCidr = "192.168.64.1/32";

    /// <summary>Default cap on resolved allowlist CIDRs (gateway excluded).</summary>
    public const int DefaultMaxAllowCidrs = 64;

    /// <summary>Default per-host DNS bound in seconds.</summary>
    public const int DefaultDnsTimeoutSeconds = 5;

    /// <summary>Default Softnet helper binary.</summary>
    public const string DefaultSoftnetBinaryPath = "softnet";

    /// <summary>Guest-network backend. Default NAT preserves today's behaviour.</summary>
    public TartNetworkMode Mode { get; init; } = TartNetworkMode.Nat;

    /// <summary>
    /// vmnet gateway CIDR the guest needs for DHCP/DNS. Always present in the
    /// Softnet allowlist — even for an empty acquisition allowlist — and the
    /// only host-side address allowed besides resolved <c>/32</c>s. Never the
    /// <c>@host</c> keyword or the Mac's LAN. Override to match the Softnet
    /// vmnet subnet on this Mac.
    /// </summary>
    public string GatewayCidr { get; init; } = DefaultGatewayCidr;

    /// <summary>Cap on resolved allowlist CIDRs (1–4096); the gateway is extra. Overflows fail the create.</summary>
    public int MaxAllowCidrs { get; init; } = DefaultMaxAllowCidrs;

    /// <summary>Per-host DNS timeout in seconds (1–30) bounding allowlist resolution.</summary>
    public int DnsTimeoutSeconds { get; init; } = DefaultDnsTimeoutSeconds;

    /// <summary>Softnet helper binary probed at preflight (absolute path recommended).</summary>
    public string SoftnetBinaryPath { get; init; } = DefaultSoftnetBinaryPath;

    /// <summary>
    /// Binds the <c>Network</c> subsection. An unknown <c>Mode</c> throws
    /// instead of falling back to NAT: silently dropping a requested packet
    /// filter is a downgrade, so the misconfiguration fails the create with
    /// a message naming the valid values.
    /// </summary>
    public static TartSandboxNetworkOptions FromConfiguration(IConfigurationSection? section)
    {
        var defaults = new TartSandboxNetworkOptions();
        if (section is null || !section.Exists())
            return defaults;

        var rawMode = (section["Mode"] ?? string.Empty).Trim();
        TartNetworkMode mode;
        if (rawMode.Length == 0)
            mode = defaults.Mode;
        else if (string.Equals(rawMode, "nat", StringComparison.OrdinalIgnoreCase))
            mode = TartNetworkMode.Nat;
        else if (string.Equals(rawMode, "softnet", StringComparison.OrdinalIgnoreCase))
            mode = TartNetworkMode.Softnet;
        else
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:Network:Mode must be 'nat' or 'softnet' (got '{rawMode}').");

        var gateway = (section["GatewayCidr"] ?? string.Empty).Trim();
        if (gateway.Length == 0)
            gateway = defaults.GatewayCidr;
        if (!IsIPv4Cidr(gateway))
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:Network:GatewayCidr must be an IPv4 CIDR (got '{gateway}').");

        return new TartSandboxNetworkOptions
        {
            Mode = mode,
            GatewayCidr = gateway,
            MaxAllowCidrs = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "MaxAllowCidrs", defaults.MaxAllowCidrs), 1, 4096),
            DnsTimeoutSeconds = Math.Clamp(
                PluginConfigReaders.ReadInt(section, "DnsTimeoutSeconds", defaults.DnsTimeoutSeconds), 1, 30),
            SoftnetBinaryPath = PluginConfigReaders.ReadNonEmpty(section, "SoftnetBinaryPath", defaults.SoftnetBinaryPath),
        };
    }

    internal static bool IsIPv4Cidr(string value)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
            return false;
        if (!System.Net.IPAddress.TryParse(value[..slash].Trim(), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        return int.TryParse(value[(slash + 1)..].Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            && prefix is >= 0 and <= 32;
    }
}
