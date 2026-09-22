using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// Lease-handle encoding for Infisical-issued leases. Handles are
/// self-describing — <c>infisical.{s|d}.{sandboxVar}.{tail}</c> — so renew
/// and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone: the sandbox variable
/// re-resolves the current mapping, and the tail carries the server lease
/// id (dynamic) or a random handle (static). Names only, never values, so
/// handles are safe to log, persist, and reconcile.
/// </summary>
internal static class InfisicalLeaseIds
{
    internal const string Prefix = "infisical";

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        Static = 1,
        Dynamic = 2,
    }

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar, string Tail, bool Brokered);

    internal static string BuildStatic(string sandboxEnvVar, bool brokered)
        => LeaseHandles.Build(Prefix, brokered ? "S" : "s", sandboxEnvVar);

    internal static string BuildDynamic(string sandboxEnvVar, string serverLeaseId, bool brokered)
        => LeaseHandles.Build(Prefix, brokered ? "D" : "d", sandboxEnvVar, serverLeaseId);

    internal static bool IsOurs(string? leaseId)
        => LeaseHandles.IsOurs(leaseId, Prefix);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty, false);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out var tail))
            return false;
        var (kind, brokered) = kindLetter switch
        {
            "s" => (LeaseKind.Static, false),
            "S" => (LeaseKind.Static, true),
            "d" => (LeaseKind.Dynamic, false),
            "D" => (LeaseKind.Dynamic, true),
            _ => (LeaseKind.Unknown, false),
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, sandboxVar, tail, brokered);
        return true;
    }
}
