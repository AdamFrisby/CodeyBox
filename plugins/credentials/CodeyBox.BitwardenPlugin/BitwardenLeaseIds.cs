namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Lease-handle encoding for Bitwarden-issued leases. Handles are
/// self-describing — <c>bitwarden.s.{sandboxVar}.{tail}</c> — so renew and
/// revoke keep working after an orchestrator restart, when the provider's
/// in-memory issue registry is gone: the sandbox variable re-resolves the
/// current mapping, and the tail is an opaque random handle (never a token
/// or value — access tokens live only in host memory, machine-account
/// secrets only in the host credential chain, never in handles, logs, or
/// the lease store). Names only, never values, so handles are safe to log,
/// persist, and reconcile.
/// </summary>
internal static class BitwardenLeaseIds
{
    internal const string Prefix = "bitwarden";

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        /// <summary>Secret read from Secrets Manager with a machine-account access token.</summary>
        Static = 1,
    }

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar, string Tail);

    internal static string BuildStatic(string sandboxEnvVar)
        => $"{Prefix}.s.{sandboxEnvVar}.{Guid.NewGuid():N}";

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
            "s" => LeaseKind.Static,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, parts[2], parts[3]);
        return true;
    }
}
