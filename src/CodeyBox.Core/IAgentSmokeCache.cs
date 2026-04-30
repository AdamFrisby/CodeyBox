using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// In-process cache for credential smoke results, keyed by
/// <c>(AgentKind, credential-fingerprint)</c>. Thread-safe.
/// Not persisted — cleared on orchestrator restart.
/// </summary>
public interface IAgentSmokeCache
{
    /// <summary>Returns a cached result if still within TTL, or null if expired or absent.</summary>
    AgentSmokeResult? TryGet(AgentKind kind, string credentialFingerprint);

    /// <summary>Stores a smoke result with the configured TTL.</summary>
    void Set(AgentKind kind, string credentialFingerprint, AgentSmokeResult result);
}

/// <summary>
/// Computes a stable, opaque fingerprint for a credential bundle, used as a
/// cache key. The fingerprint is a SHA-256 hex digest of all credential
/// values — this deduplicates identical credentials without retaining the
/// token itself.
/// </summary>
public static class SmokeCredentialFingerprint
{
    public static string Compute(AgentCredential credential)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in credential.EnvironmentVariables.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append('\0').Append(v).Append('\0');
        foreach (var (k, v) in credential.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append('\0').Append(v).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
