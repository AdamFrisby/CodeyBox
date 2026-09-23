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

    internal static string BuildStatic(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "s", sandboxEnvVar);

    /// <summary>
    /// Parses a handle into the sandbox variable it was issued for — the
    /// only datum renew/revoke need. Bitwarden mints a single kind ('s');
    /// the tail is an opaque random handle nothing reads back.
    /// </summary>
    internal static bool TryParse(string? leaseId, out string sandboxEnvVar)
    {
        sandboxEnvVar = string.Empty;
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out _))
            return false;
        if (kindLetter != "s")
            return false;
        sandboxEnvVar = sandboxVar;
        return true;
    }
}
