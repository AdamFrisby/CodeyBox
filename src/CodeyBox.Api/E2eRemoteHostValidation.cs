using System.Net;

namespace CodeyBox.Api;

internal static class E2eRemoteHostValidation
{
    public static bool IsLocalSshTarget(MultipassRemoteSandboxConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SshTarget))
            return false;

        return IsLocalHostIdentity(SshHostIdentity(config.SshTarget));
    }

    public static string SshHostIdentity(string sshTarget)
    {
        var target = sshTarget.Trim();
        var at = target.LastIndexOf('@');
        var host = at >= 0 && at + 1 < target.Length ? target[(at + 1)..] : target;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];
        return NormalizeHostIdentity(host);
    }

    private static bool IsLocalHostIdentity(string host)
    {
        var normalized = NormalizeHostIdentity(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (IPAddress.TryParse(normalized, out var ip))
            return IsLocalAddress(ip);

        return LocalHostNames().Contains(normalized);
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        var normalized = NormalizeAddress(address);
        if (IPAddress.IsLoopback(normalized)
            || normalized.Equals(IPAddress.Any)
            || normalized.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        foreach (var local in LocalHostAddresses())
        {
            if (NormalizeAddress(local).Equals(normalized))
                return true;
        }

        return false;
    }

    private static IReadOnlySet<string> LocalHostNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLocalName(names, "localhost");
        AddLocalName(names, "localhost.localdomain");
        AddLocalName(names, Environment.MachineName);

        try
        {
            var hostName = Dns.GetHostName();
            AddLocalName(names, hostName);
            var entry = Dns.GetHostEntry(hostName);
            AddLocalName(names, entry.HostName);
            foreach (var alias in entry.Aliases)
                AddLocalName(names, alias);
        }
        catch
        {
            // Best-effort local identity detection. Loopback literals and the
            // process hostname above remain covered when DNS is unavailable.
        }

        return names;
    }

    private static IReadOnlyList<IPAddress> LocalHostAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName());
        }
        catch
        {
            return [];
        }
    }

    private static void AddLocalName(HashSet<string> names, string? value)
    {
        var normalized = NormalizeHostIdentity(value);
        if (!string.IsNullOrEmpty(normalized))
            names.Add(normalized);
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string NormalizeHostIdentity(string? host) =>
        (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}
