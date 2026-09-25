using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>
/// Lease-handle encoding for OpenBao-issued leases. Handles are
/// self-describing — <c>openbao.{s|d}.{sandboxVar}.{tail}</c> — so renew
/// and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone: the sandbox variable
/// re-resolves the current mapping, and the tail carries the server lease
/// id verbatim (dynamic — e.g. <c>database/creds/readonly/…</c>) or a
/// random handle (static). Names only, never values or tokens, so handles
/// are safe to log, persist, and reconcile.
/// </summary>
internal static class OpenBaoLeaseIds
{
    internal const string Prefix = "openbao";

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        /// <summary>Static KV secret read with the provider token.</summary>
        Static = 1,
        /// <summary>Dynamic credential issued under a real server-side lease.</summary>
        Dynamic = 2,
    }

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar, string Tail);

    internal static string BuildStatic(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "s", sandboxEnvVar);

    internal static string BuildDynamic(string sandboxEnvVar, string serverLeaseId)
        => LeaseHandles.Build(Prefix, "d", sandboxEnvVar, serverLeaseId);

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out var tail))
            return false;
        var kind = kindLetter switch
        {
            "s" => LeaseKind.Static,
            "d" => LeaseKind.Dynamic,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, sandboxVar, tail);
        return true;
    }
}
