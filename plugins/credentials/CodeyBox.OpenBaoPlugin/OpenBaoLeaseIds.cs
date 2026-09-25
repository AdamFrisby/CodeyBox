using System.Security.Cryptography;
using System.Text;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>
/// Lease-handle encoding for OpenBao-issued leases. Handles are
/// self-describing — <c>openbao.{s|d}.{sandboxVar}.{tail}</c> — so renew
/// and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone: the sandbox variable
/// re-resolves the current mapping, and the tail carries what the
/// operation needs. Names only, never values or tokens, so handles are
/// safe to log, persist, and reconcile.
/// <para>For a <em>dynamic</em> lease the tail is
/// <c>{issuer-fingerprint}~{server lease id}</c>: the verbatim server
/// lease id (e.g. <c>database/creds/readonly/…</c>) behind a short
/// fingerprint of the address that minted it. Renewal and revocation are
/// bound to the issuing endpoint — a re-pointed or removed
/// <c>Address</c> cannot silently re-target a live lease at a different
/// cluster (where a 404 would otherwise be misread as 'already revoked'),
/// and the provider token is never POSTed anywhere but the issuer. A
/// static lease's tail is a random handle; it has no server-side identity
/// to bind.</para>
/// </summary>
internal static class OpenBaoLeaseIds
{
    internal const string Prefix = "openbao";

    /// <summary>
    /// Separates the issuer fingerprint from the server lease id inside a
    /// dynamic handle's tail segment. Not <c>.</c> (the segment delimiter)
    /// and refused inside server lease ids, so a tail always splits into
    /// exactly its two halves.
    /// </summary>
    private const char IssuerSeparator = '~';

    internal enum LeaseKind : byte
    {
        Unknown = 0,
        /// <summary>Static KV secret read with the provider token.</summary>
        Static = 1,
        /// <summary>Dynamic credential issued under a real server-side lease.</summary>
        Dynamic = 2,
    }

    /// <summary>
    /// A parsed handle. <see cref="Tail"/> is the server lease id for a
    /// dynamic lease (the fingerprint is stripped) or the random tail for
    /// a static one; <see cref="IssuerFingerprint"/> is the issuing
    /// address's fingerprint for a dynamic lease, empty for a static one.
    /// </summary>
    internal sealed record ParsedLeaseId(
        LeaseKind Kind, string SandboxEnvVar, string IssuerFingerprint, string Tail);

    internal static string BuildStatic(string sandboxEnvVar)
        => LeaseHandles.Build(Prefix, "s", sandboxEnvVar);

    /// <summary>
    /// Short fingerprint of the endpoint a lease was issued against —
    /// not a secret (the address is logged and configured in the clear) —
    /// carried in the handle so renew/revoke can prove the configured
    /// <c>Address</c> still names the issuing cluster before a token is
    /// sent anywhere. A fingerprint, not the address itself: the handle
    /// is store content, so it must never become an outbound request
    /// target.
    /// </summary>
    internal static string IssuerFingerprint(string address)
    {
        var normalized = (address ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 8);
    }

    internal static string BuildDynamic(string sandboxEnvVar, string issuerAddress, string serverLeaseId)
    {
        // The fingerprint and the server lease id share one tail segment:
        // a lease id carrying the separator could never round-trip, so the
        // failure surfaces here at issue — before a lease that can never
        // be renewed or revoked is minted.
        if (serverLeaseId is null || serverLeaseId.IndexOf(IssuerSeparator) >= 0)
            throw new ArgumentException(
                "Server lease id cannot contain the issuer separator.", nameof(serverLeaseId));
        return LeaseHandles.Build(
            Prefix, "d", sandboxEnvVar, $"{IssuerFingerprint(issuerAddress)}{IssuerSeparator}{serverLeaseId}");
    }

    internal static bool TryParse(string? leaseId, out ParsedLeaseId parsed)
    {
        parsed = new ParsedLeaseId(LeaseKind.Unknown, string.Empty, string.Empty, string.Empty);
        if (!LeaseHandles.TryParse(leaseId, Prefix, out var kindLetter, out var sandboxVar, out var tail))
            return false;
        switch (kindLetter)
        {
            case "s":
                parsed = new ParsedLeaseId(LeaseKind.Static, sandboxVar, string.Empty, tail);
                return true;
            case "d":
                // A dynamic handle must carry both halves of its tail —
                // issuer fingerprint and server lease id. Anything else is
                // a foreign or corrupted record, refused rather than
                // silently treated as unbound.
                var cut = tail.IndexOf(IssuerSeparator);
                if (cut <= 0 || cut == tail.Length - 1)
                    return false;
                parsed = new ParsedLeaseId(
                    LeaseKind.Dynamic, sandboxVar, tail[..cut], tail[(cut + 1)..]);
                return true;
            default:
                return false;
        }
    }
}
