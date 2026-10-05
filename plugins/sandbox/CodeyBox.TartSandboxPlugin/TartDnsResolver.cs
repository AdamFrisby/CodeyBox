using System.Net;
using System.Net.Sockets;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Resolves one <c>SandboxNetworkPolicy.AllowedHosts</c> entry to its IPv4
/// addresses. Injection seam for tests: production does bounded system DNS,
/// tests supply a deterministic table. IPv6 is out of scope — Softnet's
/// policy documents IPv4 only — so implementations return IPv4 only.
/// </summary>
public interface ITartDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host, CancellationToken ct);
}

/// <summary>
/// System DNS resolver for Softnet allowlist builds. Returns IPv4 literals
/// directly, drops IPv6 literals (Softnet is IPv4-only), and filters A
/// records from system DNS. The caller applies the per-host timeout from
/// <c>Network:DnsTimeoutSeconds</c>; this method only honours cancellation.
/// </summary>
public sealed class SystemTartDnsResolver : ITartDnsResolver
{
    public static readonly SystemTartDnsResolver Instance = new();

    public async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host, CancellationToken ct)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > 253)
            return [];
        if (IPAddress.TryParse(name, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetwork ? [literal] : [];
        var addresses = await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false);
        return addresses.Where(static ip => ip.AddressFamily == AddressFamily.InterNetwork).ToArray();
    }
}
