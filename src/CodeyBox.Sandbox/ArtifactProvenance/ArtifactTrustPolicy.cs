using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>
/// Verifiable evidence mechanisms for executable artifacts. The
/// <c>os-package</c> value is a documentation-only label for the signed
/// OS-package-manager path (apt): it is never accepted as executable
/// admission evidence and never presented as Sigstore verification.
/// </summary>
public static class ArtifactEvidenceKinds
{
    /// <summary>Operator-pinned ECDSA key over a local provenance statement. Offline.</summary>
    public const string OpensslLocal = "openssl-local";

    /// <summary>Sigstore bundle verified with <c>cosign verify-blob</c>. Offline bundle.</summary>
    public const string SigstoreBundle = "sigstore-bundle";

    /// <summary>
    /// Cosign bundle verified with an operator-pinned local key
    /// (<c>cosign verify-blob --key … --bundle …</c>). Fully offline; the
    /// identity is the pinned key itself. Issuer must be
    /// <see cref="LocalKeyIssuer"/> and Publisher the key fingerprint
    /// (<c>key:…</c>), both enforced at policy validation.
    /// </summary>
    public const string CosignLocalKey = "cosign-local-key";

    /// <summary>Issuer sentinel for operator-pinned local keys (exact match, never a URL).</summary>
    public const string LocalKeyIssuer = "local-key";

    /// <summary>GitHub artifact attestation verified with <c>gh attestation verify</c>.</summary>
    public const string GitHubAttestation = "github-attestation";

    /// <summary>Signed OS package manager (apt). Not admission evidence.</summary>
    public const string OsPackage = "os-package";

    /// <summary>Evidence kinds accepted at the executable admission boundary.</summary>
    public static readonly IReadOnlyList<string> VerifiableKinds =
    [
        OpensslLocal,
        SigstoreBundle,
        CosignLocalKey,
        GitHubAttestation,
    ];
}

/// <summary>
/// One operator-owned trust entry for an exact artifact. The entry binds an
/// immutable content digest to the expected publisher identity and issuer,
/// plus optional exact repository/workflow/source-ref provenance constraints.
/// A valid signature from any other identity is insufficient: verification
/// uses only the key material or flags derived from this entry, never from
/// artifact-supplied metadata.
/// </summary>
public sealed class TrustedArtifactPolicy
{
    /// <summary>Maximum characters for an operator artifact label.</summary>
    public const int MaxArtifactIdLength = 128;

    /// <summary>Maximum characters for an identity, issuer, repository, workflow, ref, or predicate value.</summary>
    public const int MaxIdentityLength = 512;

    /// <summary>Maximum PEM characters pinned for one <c>openssl-local</c> entry.</summary>
    public const int MaxPublicKeyPemChars = 8192;

    /// <summary>Operator label, e.g. a plugin id or tool binary name. Exact match only.</summary>
    public string ArtifactId { get; set; } = string.Empty;

    /// <summary>Immutable SHA-256 hex digest of the primary artifact bytes (64 lowercase hex).</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Optional bundle digest covering the primary artifact plus its executable
    /// dependencies. When set, the bundle must match exactly; sibling
    /// executables outside the bundle fail closed.
    /// </summary>
    public string? BundleSha256 { get; set; }

    /// <summary>Expected publisher identity. Exact equality, never a pattern.</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>Expected issuer. Exact equality, never a pattern.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Optional exact repository constraint (e.g. <c>owner/repo</c>).</summary>
    public string? Repository { get; set; }

    /// <summary>Optional exact workflow constraint.</summary>
    public string? Workflow { get; set; }

    /// <summary>Optional exact source-ref constraint (branch, tag, or commit).</summary>
    public string? SourceRef { get; set; }

    /// <summary>Optional exact predicate/build type constraint.</summary>
    public string? PredicateType { get; set; }

    /// <summary>One of <see cref="ArtifactEvidenceKinds"/> verifiable kinds.</summary>
    public string Evidence { get; set; } = ArtifactEvidenceKinds.OpensslLocal;

    /// <summary>
    /// PEM-encoded public key pinned for <c>openssl-local</c> and
    /// <c>cosign-local-key</c> evidence. This is public key material, not a
    /// secret, but it is still never written to logs.
    /// </summary>
    public string? PublicKeyPem { get; set; }

    internal IEnumerable<string> Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(ArtifactId) || ArtifactId.Length > MaxArtifactIdLength || ArtifactId.Any(char.IsControl))
            yield return $"{path}:ArtifactId must be 1-{MaxArtifactIdLength} printable characters.";
        if (!IsLowerHex64(Sha256))
            yield return $"{path}:Sha256 must be 64 lowercase hexadecimal characters.";
        if (BundleSha256 is not null && !IsLowerHex64(BundleSha256))
            yield return $"{path}:BundleSha256 must be 64 lowercase hexadecimal characters when set.";
        if (string.IsNullOrWhiteSpace(Publisher) || Publisher.Length > MaxIdentityLength || Publisher.Any(char.IsControl))
            yield return $"{path}:Publisher must be a 1-{MaxIdentityLength} character exact identity.";
        if (string.IsNullOrWhiteSpace(Issuer) || Issuer.Length > MaxIdentityLength || Issuer.Any(char.IsControl))
            yield return $"{path}:Issuer must be a 1-{MaxIdentityLength} character exact issuer.";
        foreach (var (name, value) in new (string, string?)[]
                 {
                     ("Repository", Repository),
                     ("Workflow", Workflow),
                     ("SourceRef", SourceRef),
                     ("PredicateType", PredicateType),
                 })
        {
            if (value is not null && (value.Length == 0 || value.Length > MaxIdentityLength || value.Any(char.IsControl)))
                yield return $"{path}:{name} must be 1-{MaxIdentityLength} printable characters when set.";
        }
        if (!ArtifactEvidenceKinds.VerifiableKinds.Contains(Evidence, StringComparer.Ordinal))
            yield return $"{path}:Evidence must be one of '{string.Join("', '", ArtifactEvidenceKinds.VerifiableKinds)}'. " +
                         $"The '{ArtifactEvidenceKinds.OsPackage}' mechanism (apt) is separate and never admits executables.";
        if (Evidence == ArtifactEvidenceKinds.OpensslLocal)
        {
            if (string.IsNullOrWhiteSpace(PublicKeyPem) || PublicKeyPem.Length > MaxPublicKeyPemChars)
                yield return $"{path}:PublicKeyPem is required for openssl-local evidence (max {MaxPublicKeyPemChars} chars).";
            else if (!PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
                yield return $"{path}:PublicKeyPem must be a PEM-encoded public key.";
        }
        if (Evidence == ArtifactEvidenceKinds.CosignLocalKey)
        {
            // The identity IS the pinned key: Publisher must be the key
            // fingerprint and Issuer the local-key sentinel, both derived
            // from operator configuration — never from artifact metadata. A
            // valid signature from any other key is insufficient.
            if (string.IsNullOrWhiteSpace(PublicKeyPem) || PublicKeyPem.Length > MaxPublicKeyPemChars)
                yield return $"{path}:PublicKeyPem is required for cosign-local-key evidence (max {MaxPublicKeyPemChars} chars).";
            else if (!PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
                yield return $"{path}:PublicKeyPem must be a PEM-encoded public key.";
            else if (!string.Equals(Publisher, "key:" + ArtifactTrustOptions.FingerprintPublicKey(PublicKeyPem), StringComparison.Ordinal))
                yield return $"{path}:Publisher must be 'key:' followed by the pinned key fingerprint for cosign-local-key evidence.";
            if (!string.Equals(Issuer, ArtifactEvidenceKinds.LocalKeyIssuer, StringComparison.Ordinal))
                yield return $"{path}:Issuer must be '{ArtifactEvidenceKinds.LocalKeyIssuer}' for cosign-local-key evidence.";
        }
    }

    internal static bool IsLowerHex64(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Operator-owned, opt-in trust policy for exact plugin bundles and externally
/// staged tool artifacts. Disabled by default: existing installations keep
/// existing behavior until the operator enables this section. The policy is a
/// plain hot-reloadable options object; every admission reads the current
/// snapshot and keys cached results by the policy digest, so a changed policy
/// never reuses a stale verification result.
/// </summary>
public sealed class ArtifactTrustOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CodeyBox:ArtifactTrust";

    /// <summary>Maximum trusted artifacts accepted in one policy.</summary>
    public const int MaxTrustedArtifacts = 64;

    /// <summary>Master switch. False (default) preserves existing behavior.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When false (default), GitHub attestation discovery over the network is
    /// unavailable and <c>github-attestation</c> evidence without a local
    /// bundle fails as verifier-unavailable instead of dialing out.
    /// </summary>
    public bool AllowNetworkDiscovery { get; set; }

    /// <summary>cosign binary name or absolute path. Invoked without a shell.</summary>
    public string CosignBinaryPath { get; set; } = "cosign";

    /// <summary>gh binary name or absolute path. Invoked without a shell.</summary>
    public string GhBinaryPath { get; set; } = "gh";

    /// <summary>Verification timeout per subprocess invocation, in seconds.</summary>
    public int VerificationTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum provenance-document bytes accepted for one artifact.</summary>
    public int MaxProvenanceDocumentBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum signature/bundle bytes accepted for one artifact.</summary>
    public int MaxSignatureBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum primary-artifact bytes admitted through verification.</summary>
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Maximum cached admission verdicts retained process-wide.</summary>
    public int MaxCachedVerdicts { get; set; } = 256;

    /// <summary>Exact trusted artifacts. Lookup is by digest, never by name alone.</summary>
    public List<TrustedArtifactPolicy> TrustedArtifacts { get; set; } = [];

    /// <summary>Validates bounds and returns every operator-facing error.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (TrustedArtifacts.Count > MaxTrustedArtifacts)
            errors.Add($"{SectionName}:TrustedArtifacts cannot contain more than {MaxTrustedArtifacts} entries.");
        if (VerificationTimeoutSeconds is < 1 or > 600)
            errors.Add($"{SectionName}:VerificationTimeoutSeconds must be 1-600.");
        if (MaxProvenanceDocumentBytes is < 1024 or > 4 * 1024 * 1024)
            errors.Add($"{SectionName}:MaxProvenanceDocumentBytes must be 1024-4194304.");
        if (MaxSignatureBytes is < 512 or > 8 * 1024 * 1024)
            errors.Add($"{SectionName}:MaxSignatureBytes must be 512-8388608.");
        if (MaxArtifactBytes is < 1024 or > 4L * 1024 * 1024 * 1024)
            errors.Add($"{SectionName}:MaxArtifactBytes must be 1024-4294967296.");
        if (MaxCachedVerdicts is < 1 or > 4096)
            errors.Add($"{SectionName}:MaxCachedVerdicts must be 1-4096.");
        foreach (var (name, value) in new (string, string?)[]
                 {
                     ("CosignBinaryPath", CosignBinaryPath),
                     ("GhBinaryPath", GhBinaryPath),
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
                errors.Add($"{SectionName}:{name} must be 1-512 printable characters.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bounded = TrustedArtifacts.Take(MaxTrustedArtifacts + 1).ToList();
        for (var i = 0; i < bounded.Count; i++)
        {
            var entry = bounded[i];
            if (entry is null)
            {
                errors.Add($"{SectionName}:TrustedArtifacts[{i}] cannot be null.");
                continue;
            }
            errors.AddRange(entry.Validate($"{SectionName}:TrustedArtifacts[{i}]"));
            if (!string.IsNullOrWhiteSpace(entry.Sha256) && !seen.Add(CanonicalDigestKey(entry)))
                errors.Add($"{SectionName}:TrustedArtifacts[{i}] duplicates an earlier digest entry.");
        }
        return errors;
    }

    private static string CanonicalDigestKey(TrustedArtifactPolicy entry) =>
        entry.Sha256 + "\u001f" + (entry.BundleSha256 ?? string.Empty) + "\u001f" + entry.Evidence;

    /// <summary>
    /// Computes the policy digest: SHA-256 over the canonical (sorted,
    /// secret-free) rendering of the enforcement inputs. Cached verdicts and
    /// persisted evidence carry this digest; any policy edit changes it and
    /// invalidates prior results.
    /// </summary>
    public string ComputePolicyDigest()
    {
        var ordered = TrustedArtifacts
            .Where(static e => e is not null)
            .OrderBy(static e => e.Sha256, StringComparer.Ordinal)
            .ThenBy(static e => e.BundleSha256 ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static e => e.Evidence, StringComparer.Ordinal)
            .Select(static e => new
            {
                e.ArtifactId,
                e.Sha256,
                e.BundleSha256,
                e.Publisher,
                e.Issuer,
                e.Repository,
                e.Workflow,
                e.SourceRef,
                e.PredicateType,
                e.Evidence,
                PublicKeyFingerprint = FingerprintPublicKey(e.PublicKeyPem),
            })
            .ToList();
        var canonical = new
        {
            Enabled,
            AllowNetworkDiscovery,
            VerificationTimeoutSeconds,
            MaxProvenanceDocumentBytes,
            MaxSignatureBytes,
            MaxArtifactBytes,
            Entries = ordered.ToArray(),
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    internal static string FingerprintPublicKey(string? pem)
    {
        if (string.IsNullOrEmpty(pem))
            return string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pem)));
    }
}
