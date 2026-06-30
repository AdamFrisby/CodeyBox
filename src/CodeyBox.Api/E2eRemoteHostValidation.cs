using System.Net;
using System.Net.NetworkInformation;

namespace CodeyBox.Api;

internal static class E2eRemoteHostValidation
{
    public static bool TryResolveSshTargetIdentity(
        MultipassRemoteSandboxConfig config,
        out SshTargetIdentity identity,
        out string error)
    {
        identity = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(config.SshTarget))
        {
            error = "SshTarget is required";
            return false;
        }

        var configuredHost = SshHostIdentity(config.SshTarget);
        var effectiveHost = ResolveEffectiveHostName(config) ?? configuredHost;
        if (string.IsNullOrWhiteSpace(effectiveHost))
        {
            error = $"could not determine host identity for SSH target '{config.SshTarget}'";
            return false;
        }

        if (!TryResolveHostAddresses(effectiveHost, out var addresses, out var resolveError))
        {
            error = $"could not resolve SSH host '{effectiveHost}' for target '{config.SshTarget}': {resolveError}";
            return false;
        }

        identity = new SshTargetIdentity(
            NormalizeHostIdentity(effectiveHost),
            config.SshPort ?? ResolveConfiguredPort(config) ?? 22,
            addresses.Select(NormalizeAddress).Distinct().ToArray());
        return true;
    }

    public static bool IsLocalSshTarget(MultipassRemoteSandboxConfig config, out string? error)
    {
        error = null;
        if (!TryResolveSshTargetIdentity(config, out var identity, out var resolveError))
        {
            error = resolveError;
            return true;
        }

        return IsLocalHostIdentity(identity.Host)
            || identity.Addresses.Any(IsLocalAddress);
    }

    public static bool IsSameRemoteHost(MultipassRemoteSandboxConfig left, MultipassRemoteSandboxConfig right, out string? error)
    {
        error = null;
        var leftTextHost = SshHostIdentity(left.SshTarget ?? string.Empty);
        var rightTextHost = SshHostIdentity(right.SshTarget ?? string.Empty);
        var leftTextPort = left.SshPort ?? ResolveConfiguredPort(left) ?? 22;
        var rightTextPort = right.SshPort ?? ResolveConfiguredPort(right) ?? 22;
        if (leftTextPort == rightTextPort
            && !string.IsNullOrWhiteSpace(leftTextHost)
            && string.Equals(leftTextHost, rightTextHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryResolveSshTargetIdentity(left, out var leftIdentity, out var leftError))
        {
            error = leftError;
            return false;
        }

        if (!TryResolveSshTargetIdentity(right, out var rightIdentity, out var rightError))
        {
            error = rightError;
            return false;
        }

        if (leftIdentity.Port != rightIdentity.Port)
            return false;

        if (string.Equals(leftIdentity.Host, rightIdentity.Host, StringComparison.OrdinalIgnoreCase))
            return true;

        return leftIdentity.Addresses.Intersect(rightIdentity.Addresses).Any();
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

    private static string? ResolveEffectiveHostName(MultipassRemoteSandboxConfig config)
    {
        var hostNameOption = LastSshOption(config.ExtraSshOptions, "HostName");
        if (!string.IsNullOrWhiteSpace(hostNameOption))
            return hostNameOption;

        return TryResolveOpenSshConfigHost(config);
    }

    private static int? ResolveConfiguredPort(MultipassRemoteSandboxConfig config)
    {
        var portOption = LastSshOption(config.ExtraSshOptions, "Port");
        return int.TryParse(portOption, out var port) && port > 0 ? port : null;
    }

    private static string? LastSshOption(IEnumerable<string>? options, string key)
    {
        string? value = null;
        foreach (var option in options ?? [])
        {
            var eq = option.IndexOf('=');
            if (eq <= 0 || eq == option.Length - 1)
                continue;
            if (string.Equals(option[..eq], key, StringComparison.OrdinalIgnoreCase))
                value = option[(eq + 1)..];
        }
        return value;
    }

    private static string? TryResolveOpenSshConfigHost(MultipassRemoteSandboxConfig config)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = string.IsNullOrWhiteSpace(config.SshBinary) ? "ssh" : config.SshBinary!;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.ArgumentList.Add("-G");
            if (config.SshPort is { } port)
            {
                process.StartInfo.ArgumentList.Add("-p");
                process.StartInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var extra in config.ExtraSshOptions ?? [])
            {
                if (string.IsNullOrWhiteSpace(extra))
                    continue;
                process.StartInfo.ArgumentList.Add("-o");
                process.StartInfo.ArgumentList.Add(extra);
            }
            process.StartInfo.ArgumentList.Add(config.SshTarget!);

            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var output = outputTask.GetAwaiter().GetResult();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = line.IndexOf(' ');
                if (split <= 0 || split == line.Length - 1)
                    continue;
                if (string.Equals(line[..split], "hostname", StringComparison.OrdinalIgnoreCase))
                    return line[(split + 1)..].Trim();
            }
        }
        catch
        {
            return null;
        }

        return null;
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
        var addresses = new List<IPAddress>();
        try
        {
            addresses.AddRange(Dns.GetHostAddresses(Dns.GetHostName()));
        }
        catch
        {
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    addresses.Add(address.Address);
            }
        }
        catch
        {
        }

        return addresses;
    }

    private static void AddLocalName(HashSet<string> names, string? value)
    {
        var normalized = NormalizeHostIdentity(value);
        if (!string.IsNullOrEmpty(normalized))
            names.Add(normalized);
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryResolveHostAddresses(string host, out IReadOnlyList<IPAddress> addresses, out string error)
    {
        error = string.Empty;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [NormalizeAddress(literal)];
            return true;
        }

        try
        {
            addresses = Dns.GetHostAddresses(host)
                .Select(NormalizeAddress)
                .Distinct()
                .ToArray();
            if (addresses.Count > 0)
                return true;
            error = "resolver returned no addresses";
            return false;
        }
        catch (Exception ex)
        {
            addresses = [];
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizeHostIdentity(string? host) =>
        (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    public readonly record struct SshTargetIdentity(string Host, int Port, IReadOnlyList<IPAddress> Addresses);
}
