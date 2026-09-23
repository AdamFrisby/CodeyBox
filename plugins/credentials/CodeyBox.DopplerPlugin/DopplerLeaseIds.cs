using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// Lease-handle encoding for Doppler-issued leases. Handles are
/// self-describing — <c>doppler.{s|i}.{sandboxVar}.{tail}</c> — so renew
/// and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone: the sandbox variable
/// re-resolves the current mapping, and the tail is an opaque random
/// handle (never a token — tokens live only in host memory, never in
/// handles, logs, or the lease store). Names only, never values, so
/// handles are safe to log, persist, and reconcile.
/// </summary>
internal static class DopplerLeaseIds
{
    internal const string Prefix = "doppler";

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        /// <summary>Static secret fetched with a restricted service token.</summary>
        Static = 1,
        /// <summary>Secret fetched with a short-lived service-account identity token.</summary>
        Identity = 2,
    }

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar);

    internal static string BuildStatic(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "s", sandboxEnvVar);

    internal static string BuildIdentity(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "i", sandboxEnvVar);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out _))
            return false;
        var kind = kindLetter switch
        {
            "s" => LeaseKind.Static,
            "i" => LeaseKind.Identity,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, sandboxVar);
        return true;
    }
}
