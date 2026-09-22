namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// Lease-handle encoding for 1Password-issued leases. Handles are
/// self-describing — <c>onepassword.{c|o}.{sandboxVar}.{tail}</c> — so renew
/// and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone: the sandbox variable
/// re-resolves the current mapping, the kind letter re-selects the
/// retrieval transport, and the tail is an opaque random handle (never a
/// token or value — tokens live only in host memory or the host credential
/// chain, never in handles, logs, or the lease store). Names only, never
/// values, so handles are safe to log, persist, and reconcile.
/// </summary>
internal static class OnePasswordLeaseIds
{
    internal const string Prefix = "onepassword";

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        /// <summary>Secret read from the self-hosted Connect server with a Connect token.</summary>
        Connect = 1,
        /// <summary>Secret read with the <c>op</c> CLI under a service-account token.</summary>
        ServiceAccount = 2,
    }

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar, string Tail);

    internal static string BuildConnect(string sandboxEnvVar)
        => $"{Prefix}.c.{sandboxEnvVar}.{Guid.NewGuid():N}";

    internal static string BuildServiceAccount(string sandboxEnvVar)
        => $"{Prefix}.o.{sandboxEnvVar}.{Guid.NewGuid():N}";

    internal static bool IsOurs(string? leaseId)
        => leaseId is not null
            && leaseId.StartsWith(Prefix + ".", StringComparison.Ordinal);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(leaseId))
            return false;
        var parts = leaseId.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3]))
            return false;
        var kind = parts[1] switch
        {
            "c" => LeaseKind.Connect,
            "o" => LeaseKind.ServiceAccount,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, parts[2], parts[3]);
        return true;
    }
}
