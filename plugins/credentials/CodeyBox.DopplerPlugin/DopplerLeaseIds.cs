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

    internal sealed record ParsedLeaseId(LeaseKind Kind, string SandboxEnvVar, string Tail);

    internal static string BuildStatic(string sandboxEnvVar)
        => $"{Prefix}.s.{sandboxEnvVar}.{Guid.NewGuid():N}";

    internal static string BuildIdentity(string sandboxEnvVar)
        => $"{Prefix}.i.{sandboxEnvVar}.{Guid.NewGuid():N}";

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
            "i" => LeaseKind.Identity,
            _ => LeaseKind.Unknown,
        };
        if (kind == LeaseKind.Unknown)
            return false;
        parsed = new ParsedLeaseId(kind, parts[2], parts[3]);
        return true;
    }
}
