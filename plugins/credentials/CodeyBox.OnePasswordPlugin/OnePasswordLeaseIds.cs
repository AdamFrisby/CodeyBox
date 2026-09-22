using CodeyBox.PluginSdk.Credentials;

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
        => LeaseHandles.Build(Prefix, "c", sandboxEnvVar);

    internal static string BuildServiceAccount(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "o", sandboxEnvVar);

    internal static bool IsOurs(string? leaseId)
        => LeaseHandles.IsOurs(leaseId, Prefix);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out var tail))
            return false;
        var kind = kindLetter switch
        {
            "c" => LeaseKind.Connect,
            "o" => LeaseKind.ServiceAccount,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, sandboxVar, tail);
        return true;
    }
}
