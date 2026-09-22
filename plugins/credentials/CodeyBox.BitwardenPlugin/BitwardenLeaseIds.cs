using CodeyBox.PluginSdk.Credentials;

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
        => LeaseHandles.Build(Prefix, "s", sandboxEnvVar);

    internal static bool IsOurs(string? leaseId)
        => LeaseHandles.IsOurs(leaseId, Prefix);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out var tail))
            return false;
        var kind = kindLetter switch
        {
            "s" => LeaseKind.Static,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, sandboxVar, tail);
        return true;
    }
}
