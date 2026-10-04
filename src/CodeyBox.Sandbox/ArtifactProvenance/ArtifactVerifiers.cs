using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>
/// Artifact-supplied provenance statement (<c>&lt;artifact&gt;.provenance.json</c>).
/// Untrusted input: it identifies the claimed evidence but never defines the
/// trusted publisher. Every field is compared by exact equality against the
/// operator policy after the cryptographic check binds the statement (or the
/// subject) to the staged bytes.
/// </summary>
public sealed record ProvenanceStatement
{
    /// <summary>SHA-256 hex of the primary artifact bytes claimed by the publisher.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Claimed publisher identity.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>Claimed issuer.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Claimed repository.</summary>
    public string? Repository { get; init; }

    /// <summary>Claimed workflow.</summary>
    public string? Workflow { get; init; }

    /// <summary>Claimed source ref.</summary>
    public string? SourceRef { get; init; }

    /// <summary>Claimed predicate/build type.</summary>
    public string? PredicateType { get; init; }

    /// <summary>Claimed operator artifact label.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>Parses and bounds a provenance document. Null when malformed.</summary>
    public static ProvenanceStatement? TryParse(byte[] document)
    {
        if (document is null || document.Length == 0)
            return null;
        try
        {
            using var parsed = JsonDocument.Parse(document);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string? Get(string name)
            {
                if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
                    return null;
                var value = element.GetString();
                if (value is null || value.Length == 0 || value.Length > TrustedArtifactPolicy.MaxIdentityLength || value.Any(char.IsControl))
                    return null;
                return value;
            }
            var digest = Get("digest");
            var publisher = Get("publisher");
            var issuer = Get("issuer");
            if (digest is null || publisher is null || issuer is null)
                return null;
            if (digest.Length != 64 || !TrustedArtifactPolicy.IsLowerHex64(digest))
                return null;
            return new ProvenanceStatement
            {
                Digest = digest,
                Publisher = publisher,
                Issuer = issuer,
                Repository = Get("repository"),
                Workflow = Get("workflow"),
                SourceRef = Get("sourceRef"),
                PredicateType = Get("predicateType"),
                ArtifactId = Get("artifactId"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Staged, immutable admission inputs: the private bytes under verification.</summary>
public sealed record StagedArtifact
{
    /// <summary>Private staged copy of the primary artifact (the bytes later consumed).</summary>
    public required string StagedPrimaryPath { get; init; }

    /// <summary>Hex digest of the staged primary bytes.</summary>
    public required string PrimaryDigestHex { get; init; }

    /// <summary>Bundle digest including executable dependencies, when computed.</summary>
    public string? BundleDigestHex { get; init; }

    /// <summary>Executable dependencies staged alongside the primary (name + digest).</summary>
    public required IReadOnlyList<StagedDependency> Dependencies { get; init; }

    /// <summary>Directory holding the source artifact (sidecar lookup scope).</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>Primary file name (sidecar basename).</summary>
    public required string PrimaryFileName { get; init; }
}

/// <summary>One executable dependency inside the bundle trust boundary.</summary>
public sealed record StagedDependency(string FileName, string DigestHex);

/// <summary>Cryptographic verdict from one verifier. Observed identity fields are
/// artifact-supplied until the admission service compares them exactly to policy.</summary>
public sealed record VerifierVerdict
{
    /// <summary>True when the cryptographic check passed for the staged bytes.</summary>
    public required bool CryptographicallyValid { get; init; }

    /// <summary>Observed publisher (from verified or digest-bound evidence).</summary>
    public required string ObservedPublisher { get; init; }

    /// <summary>Observed issuer.</summary>
    public required string ObservedIssuer { get; init; }

    /// <summary>Observed repository.</summary>
    public string? ObservedRepository { get; init; }

    /// <summary>Observed workflow.</summary>
    public string? ObservedWorkflow { get; init; }

    /// <summary>Observed source ref.</summary>
    public string? ObservedSourceRef { get; init; }

    /// <summary>Observed predicate type.</summary>
    public string? ObservedPredicateType { get; init; }

    /// <summary>Verifier name (<c>platform-ecdsa</c>, <c>cosign</c>, <c>gh</c>).</summary>
    public required string Verifier { get; init; }

    /// <summary>Bounded verifier version string.</summary>
    public required string VerifierVersion { get; init; }

    /// <summary>Bounded, secret-free detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>One verification mechanism behind the admission boundary.</summary>
public interface IArtifactVerifier
{
    /// <summary>Verifier name recorded in evidence.</summary>
    string Name { get; }

    /// <summary>Evidence kind this verifier handles.</summary>
    string EvidenceKind { get; }

    /// <summary>Verifies staged bytes against policy-derived trust inputs.</summary>
    VerifierVerdict Verify(
        StagedArtifact staged,
        TrustedArtifactPolicy policy,
        ArtifactTrustOptions options,
        CancellationToken ct);
}

/// <summary>
/// Local-bundle verifier: ECDSA P-256 / SHA-256 over the provenance statement
/// using the platform cryptography provider (<c>ECDsa</c>) and the
/// operator-pinned public key from policy. The signature binds publisher,
/// issuer, and subject digest together; the admission service additionally
/// requires the staged digest to equal the policy digest. Fixtures are signed
/// with the real OpenSSL CLI (<c>openssl dgst -sha256 -sign</c>), so the
/// integration is genuinely cryptographic end to end.
/// </summary>
public sealed class PlatformEcdsaVerifier : IArtifactVerifier
{
    /// <inheritdoc/>
    public string Name => "platform-ecdsa";

    /// <inheritdoc/>
    public string EvidenceKind => ArtifactEvidenceKinds.OpensslLocal;

    /// <inheritdoc/>
    public VerifierVerdict Verify(
        StagedArtifact staged,
        TrustedArtifactPolicy policy,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var statementBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.ProvenanceSuffix),
            options.MaxProvenanceDocumentBytes);
        var signature = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.SignatureSuffix),
            options.MaxSignatureBytes);
        ProvenanceStatement? statement = null;
        if (statementBytes is null || signature is null || signature.Length == 0)
        {
            return Fail("provenance statement or signature sidecar is missing.");
        }

        statement = ProvenanceStatement.TryParse(statementBytes);
        if (statement is null)
            return Fail("provenance statement is malformed.");

        if (!string.Equals(statement.Digest, staged.PrimaryDigestHex, StringComparison.Ordinal))
            return Fail("provenance statement digest does not match the staged artifact bytes.");

        bool valid;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(policy.PublicKeyPem);
            if (ecdsa.KeySize != 256)
                return Fail("policy public key is not P-256.");
            valid = ecdsa.VerifyData(statementBytes, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return Fail("policy public key is unusable or the signature envelope is malformed.");
        }

        if (!valid)
            return Fail("ECDSA signature verification failed.");
        return new VerifierVerdict
        {
            CryptographicallyValid = true,
            ObservedPublisher = statement.Publisher,
            ObservedIssuer = statement.Issuer,
            ObservedRepository = statement.Repository,
            ObservedWorkflow = statement.Workflow,
            ObservedSourceRef = statement.SourceRef,
            ObservedPredicateType = statement.PredicateType,
            Verifier = Name,
            VerifierVersion = VerifierVersions.Platform(),
        };

        VerifierVerdict Fail(string detail) => new()
        {
            CryptographicallyValid = false,
            ObservedPublisher = statement?.Publisher ?? string.Empty,
            ObservedIssuer = statement?.Issuer ?? string.Empty,
            ObservedRepository = statement?.Repository,
            ObservedWorkflow = statement?.Workflow,
            ObservedSourceRef = statement?.SourceRef,
            ObservedPredicateType = statement?.PredicateType,
            Verifier = Name,
            VerifierVersion = VerifierVersions.Platform(),
            Detail = detail,
        };
    }
}

/// <summary>
/// Sigstore verifier through the official cosign contract
/// (<c>cosign verify-blob --bundle … --certificate-identity … --certificate-issuer … -- FILE</c>).
/// Publisher and issuer travel as exact flag values from policy (never
/// patterns, never artifact-supplied); the subject digest is re-checked
/// locally by the admission service. Minimum supported behavior: cosign 2.2.
/// </summary>
public sealed class CosignVerifier : IArtifactVerifier
{
    private readonly IVerifierProcessRunner _runner;

    /// <summary>Minimum cosign major version accepted.</summary>
    public const int MinimumMajorVersion = 2;

    public CosignVerifier(IVerifierProcessRunner? runner = null) => _runner = runner ?? new BoundedProcessRunner();

    /// <inheritdoc/>
    public string Name => "cosign";

    /// <inheritdoc/>
    public string EvidenceKind => ArtifactEvidenceKinds.SigstoreBundle;

    /// <inheritdoc/>
    public VerifierVerdict Verify(
        StagedArtifact staged,
        TrustedArtifactPolicy policy,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);

        var bundleBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.SigstoreSuffix),
            options.MaxSignatureBytes);
        var statementBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.ProvenanceSuffix),
            options.MaxProvenanceDocumentBytes);
        var statement = statementBytes is null ? null : ProvenanceStatement.TryParse(statementBytes);
        if (bundleBytes is null || statement is null)
        {
            return Unverified(statement,
                "sigstore bundle or provenance statement sidecar is missing or malformed.");
        }
        if (!string.Equals(statement.Digest, staged.PrimaryDigestHex, StringComparison.Ordinal))
        {
            return Unverified(statement,
                "provenance statement digest does not match the staged artifact bytes.");
        }

        var version = VerifierVersions.Probe(_runner, options.CosignBinaryPath, ["version", "--json"], options, ct);
        if (!VerifierVersions.MeetsMinimum(version, MinimumMajorVersion))
        {
            throw new ArtifactBlockedException(
                $"cosign {version ?? "unknown version"} is below the minimum supported v{MinimumMajorVersion}.",
                ProvenanceOutcome.VerifierUnavailable);
        }

        var bundleStage = StageSidecar(staged.StagedPrimaryPath, bundleBytes);
        try
        {
            // Exact-match identity flags from policy; the file operand is a
            // host-canonicalized absolute path after "--" so it can never be
            // mistaken for an option. No shell is involved at any point.
            var arguments = new List<string>
            {
                "verify-blob",
                "--bundle", bundleStage,
                "--certificate-identity", policy.Publisher,
                "--certificate-issuer", policy.Issuer,
                "--",
                staged.StagedPrimaryPath,
            };
            var result = _runner.Run(new ProcessSpec
            {
                FileName = options.CosignBinaryPath,
                Arguments = arguments,
                Timeout = TimeSpan.FromSeconds(options.VerificationTimeoutSeconds),
            }, ct);
            if (result.TimedOut)
                throw new ArtifactBlockedException("cosign verification timed out.", ProvenanceOutcome.InfrastructureFailure);
            if (result.ExitCode != 0)
            {
                return Unverified(statement,
                    "cosign verify-blob rejected the artifact.");
            }
            return new VerifierVerdict
            {
                CryptographicallyValid = true,
                ObservedPublisher = statement.Publisher,
                ObservedIssuer = statement.Issuer,
                ObservedRepository = statement.Repository,
                ObservedWorkflow = statement.Workflow,
                ObservedSourceRef = statement.SourceRef,
                ObservedPredicateType = statement.PredicateType,
                Verifier = Name,
                VerifierVersion = BoundVersion(version),
            };
        }
        finally
        {
            TryDelete(bundleStage);
        }
    }

    private static VerifierVerdict Unverified(ProvenanceStatement? statement, string detail) => new()
    {
        CryptographicallyValid = false,
        ObservedPublisher = statement?.Publisher ?? string.Empty,
        ObservedIssuer = statement?.Issuer ?? string.Empty,
        ObservedRepository = statement?.Repository,
        ObservedWorkflow = statement?.Workflow,
        ObservedSourceRef = statement?.SourceRef,
        ObservedPredicateType = statement?.PredicateType,
        Verifier = "cosign",
        VerifierVersion = string.Empty,
        Detail = detail,
    };

    private static string StageSidecar(string stagedPrimaryPath, byte[] bytes)
    {
        var path = stagedPrimaryPath + ".sigstore-bundle";
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string BoundVersion(string? version) =>
        version is null ? string.Empty : version.Length > 128 ? version[..128] : version;
}

/// <summary>
/// Cosign verifier for operator-pinned local keys through the official
/// cosign contract
/// (<c>cosign verify-blob --key … --bundle … -- FILE</c>). Fully offline:
/// the bundle and the pinned key authenticate the exact bytes. Publisher and
/// issuer are derived from the policy's own pinned key
/// (<c>key:&lt;fingerprint&gt;</c> / <c>local-key</c>), never from
/// artifact-supplied metadata; the digest-bound provenance statement
/// contributes only repository/workflow/source-ref/predicate constraints.
/// Minimum supported behavior: cosign 2.2.
/// </summary>
public sealed class CosignLocalKeyVerifier : IArtifactVerifier
{
    private readonly IVerifierProcessRunner _runner;

    /// <summary>Minimum cosign major version accepted.</summary>
    public const int MinimumMajorVersion = 2;

    public CosignLocalKeyVerifier(IVerifierProcessRunner? runner = null) => _runner = runner ?? new BoundedProcessRunner();

    /// <inheritdoc/>
    public string Name => "cosign-local-key";

    /// <inheritdoc/>
    public string EvidenceKind => ArtifactEvidenceKinds.CosignLocalKey;

    /// <inheritdoc/>
    public VerifierVerdict Verify(
        StagedArtifact staged,
        TrustedArtifactPolicy policy,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);

        var bundleBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.SigstoreSuffix),
            options.MaxSignatureBytes);
        var statementBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.ProvenanceSuffix),
            options.MaxProvenanceDocumentBytes);
        var statement = statementBytes is null ? null : ProvenanceStatement.TryParse(statementBytes);
        if (bundleBytes is null || statement is null)
        {
            return Unverified(statement,
                "sigstore bundle or provenance statement sidecar is missing or malformed.");
        }
        if (!string.Equals(statement.Digest, staged.PrimaryDigestHex, StringComparison.Ordinal))
        {
            return Unverified(statement,
                "provenance statement digest does not match the staged artifact bytes.");
        }
        if (string.IsNullOrWhiteSpace(policy.PublicKeyPem))
        {
            throw new ArtifactBlockedException(
                "cosign-local-key evidence requires a pinned public key.",
                ProvenanceOutcome.MissingEvidence);
        }

        var version = VerifierVersions.Probe(_runner, options.CosignBinaryPath, ["version", "--json"], options, ct);
        if (!VerifierVersions.MeetsMinimum(version, MinimumMajorVersion))
        {
            throw new ArtifactBlockedException(
                $"cosign {version ?? "unknown version"} is below the minimum supported v{MinimumMajorVersion}.",
                ProvenanceOutcome.VerifierUnavailable);
        }

        // The identity is the pinned key itself: both the observed publisher
        // and the expected values derive from policy configuration, so a
        // valid signature from any other key cannot satisfy the entry.
        var keyFingerprint = ArtifactTrustOptions.FingerprintPublicKey(policy.PublicKeyPem);
        var keyStage = StageSidecar(staged.StagedPrimaryPath, System.Text.Encoding.UTF8.GetBytes(policy.PublicKeyPem), ".cosign-key.pub");
        var bundleStage = StageSidecar(staged.StagedPrimaryPath, bundleBytes, ".sigstore-bundle");
        try
        {
            var arguments = new List<string>
            {
                "verify-blob",
                "--key", keyStage,
                "--bundle", bundleStage,
                "--",
                staged.StagedPrimaryPath,
            };
            var result = _runner.Run(new ProcessSpec
            {
                FileName = options.CosignBinaryPath,
                Arguments = arguments,
                Timeout = TimeSpan.FromSeconds(options.VerificationTimeoutSeconds),
            }, ct);
            if (result.TimedOut)
                throw new ArtifactBlockedException("cosign verification timed out.", ProvenanceOutcome.InfrastructureFailure);
            if (result.ExitCode != 0)
            {
                return Unverified(statement,
                    "cosign verify-blob rejected the artifact.");
            }
            return new VerifierVerdict
            {
                CryptographicallyValid = true,
                ObservedPublisher = "key:" + keyFingerprint,
                ObservedIssuer = ArtifactEvidenceKinds.LocalKeyIssuer,
                ObservedRepository = statement.Repository,
                ObservedWorkflow = statement.Workflow,
                ObservedSourceRef = statement.SourceRef,
                ObservedPredicateType = statement.PredicateType,
                Verifier = Name,
                VerifierVersion = BoundVersion(version),
            };
        }
        finally
        {
            TryDelete(bundleStage);
            TryDelete(keyStage);
        }
    }

    private static VerifierVerdict Unverified(ProvenanceStatement? statement, string detail) => new()
    {
        CryptographicallyValid = false,
        ObservedPublisher = statement?.Publisher ?? string.Empty,
        ObservedIssuer = statement?.Issuer ?? string.Empty,
        ObservedRepository = statement?.Repository,
        ObservedWorkflow = statement?.Workflow,
        ObservedSourceRef = statement?.SourceRef,
        ObservedPredicateType = statement?.PredicateType,
        Verifier = "cosign-local-key",
        VerifierVersion = string.Empty,
        Detail = detail,
    };

    private static string StageSidecar(string stagedPrimaryPath, byte[] bytes, string suffix)
    {
        var path = stagedPrimaryPath + suffix;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string BoundVersion(string? version) =>
        version is null ? string.Empty : version.Length > 128 ? version[..128] : version;
}

/// <summary>
/// GitHub attestation verifier through the official gh contract
/// (<c>gh attestation verify --repo … --signer-workflow … --predicate-type … --format json -- FILE</c>).
/// Repository, workflow, and predicate constraints travel as exact flag values
/// from policy; publisher/issuer/source-ref are enforced by exact comparison
/// against the digest-bound provenance statement. Network discovery of
/// attestations happens only inside the gh process and only when the policy
/// enables it; without a local bundle and without discovery the outcome is
/// verifier-unavailable, never success. Minimum supported behavior: gh 2.55.
/// </summary>
public sealed class GitHubAttestationVerifier : IArtifactVerifier
{
    private readonly IVerifierProcessRunner _runner;

    /// <summary>Minimum gh major version accepted.</summary>
    public const int MinimumMajorVersion = 2;

    /// <summary>Minimum gh minor version accepted.</summary>
    public const int MinimumMinorVersion = 55;

    public GitHubAttestationVerifier(IVerifierProcessRunner? runner = null) => _runner = runner ?? new BoundedProcessRunner();

    /// <inheritdoc/>
    public string Name => "gh";

    /// <inheritdoc/>
    public string EvidenceKind => ArtifactEvidenceKinds.GitHubAttestation;

    /// <inheritdoc/>
    public VerifierVerdict Verify(
        StagedArtifact staged,
        TrustedArtifactPolicy policy,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);

        var statementBytes = ArtifactSidecars.ReadBounded(
            Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.ProvenanceSuffix),
            options.MaxProvenanceDocumentBytes);
        var statement = statementBytes is null ? null : ProvenanceStatement.TryParse(statementBytes);
        if (statement is null)
        {
            return Unverified(null, "provenance statement sidecar is missing or malformed.");
        }
        if (!string.Equals(statement.Digest, staged.PrimaryDigestHex, StringComparison.Ordinal))
        {
            return Unverified(statement, "provenance statement digest does not match the staged artifact bytes.");
        }

        var localBundle = Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.AttestationSuffix);
        var hasLocalBundle = ArtifactSidecars.ExistsWithinLimit(localBundle, options.MaxSignatureBytes);
        if (!hasLocalBundle && !options.AllowNetworkDiscovery)
        {
            throw new ArtifactBlockedException(
                "no local attestation bundle and online attestation discovery is disabled.",
                ProvenanceOutcome.VerifierUnavailable);
        }

        var version = VerifierVersions.Probe(_runner, options.GhBinaryPath, ["--version"], options, ct);
        if (!VerifierVersions.MeetsMinimum(version, MinimumMajorVersion, MinimumMinorVersion))
        {
            throw new ArtifactBlockedException(
                $"gh {version ?? "unknown version"} is below the minimum supported v{MinimumMajorVersion}.{MinimumMinorVersion}.",
                ProvenanceOutcome.VerifierUnavailable);
        }

        var arguments = new List<string> { "attestation", "verify" };
        if (policy.Repository is not null)
        {
            arguments.Add("--repo");
            arguments.Add(policy.Repository);
        }
        if (policy.Workflow is not null)
        {
            arguments.Add("--signer-workflow");
            arguments.Add(policy.Workflow);
        }
        if (policy.PredicateType is not null)
        {
            arguments.Add("--predicate-type");
            arguments.Add(policy.PredicateType);
        }
        arguments.Add("--format");
        arguments.Add("json");
        arguments.Add("--");
        arguments.Add(staged.StagedPrimaryPath);

        var result = _runner.Run(new ProcessSpec
        {
            FileName = options.GhBinaryPath,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(options.VerificationTimeoutSeconds),
        }, ct);
        if (result.TimedOut)
            throw new ArtifactBlockedException("gh attestation verification timed out.", ProvenanceOutcome.InfrastructureFailure);
        if (result.ExitCode != 0)
        {
            return Unverified(statement, "gh attestation verification rejected the artifact.");
        }
        return new VerifierVerdict
        {
            CryptographicallyValid = true,
            ObservedPublisher = statement.Publisher,
            ObservedIssuer = statement.Issuer,
            ObservedRepository = statement.Repository,
            ObservedWorkflow = statement.Workflow,
            ObservedSourceRef = statement.SourceRef,
            ObservedPredicateType = statement.PredicateType,
            Verifier = Name,
            VerifierVersion = BoundVersion(version),
        };
    }

    private static VerifierVerdict Unverified(ProvenanceStatement? statement, string detail) => new()
    {
        CryptographicallyValid = false,
        ObservedPublisher = statement?.Publisher ?? string.Empty,
        ObservedIssuer = statement?.Issuer ?? string.Empty,
        ObservedRepository = statement?.Repository,
        ObservedWorkflow = statement?.Workflow,
        ObservedSourceRef = statement?.SourceRef,
        ObservedPredicateType = statement?.PredicateType,
        Verifier = "gh",
        VerifierVersion = string.Empty,
        Detail = detail,
    };

    private static string BoundVersion(string? version) =>
        version is null ? string.Empty : version.Length > 128 ? version[..128] : version;
}

/// <summary>Bounded sidecar file helpers: adjacent lookup, size caps, no-follow reads.</summary>
public static class ArtifactSidecars
{
    /// <summary>Provenance statement suffix.</summary>
    public const string ProvenanceSuffix = ".provenance.json";

    /// <summary>Raw ECDSA signature suffix for <c>openssl-local</c> evidence.</summary>
    public const string SignatureSuffix = ".provenance.sig";

    /// <summary>Sigstore bundle suffix for <c>sigstore-bundle</c> evidence.</summary>
    public const string SigstoreSuffix = ".sigstore.json";

    /// <summary>GitHub attestation bundle suffix for <c>github-attestation</c> evidence.</summary>
    public const string AttestationSuffix = ".attestation.json";

    /// <summary>Reads a sidecar file up to <paramref name="maxBytes"/>; null when absent.</summary>
    public static byte[]? ReadBounded(string path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes)
                return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = (int)info.Length;
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read == 0)
                    break;
                offset += read;
            }
            if (offset != length)
                return null;
            return buffer;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when a regular non-symlink file exists within the byte limit.</summary>
    public static bool ExistsWithinLimit(string path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            return info.Exists && info.Length >= 0 && info.Length <= maxBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Verifier version helpers.</summary>
public static class VerifierVersions
{
    /// <summary>Platform verifier version: the runtime description, bounded.</summary>
    public static string Platform()
    {
        var description = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription ?? "dotnet";
        var oneLine = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length > 128 ? oneLine[..128] : oneLine;
    }

    /// <summary>
    /// Probes a verifier binary version without a shell. Returns null when the
    /// binary is missing, unusable, or too slow; the caller maps null to
    /// verifier-unavailable, never to success.
    /// </summary>
    public static string? Probe(
        IVerifierProcessRunner runner,
        string binary,
        IReadOnlyList<string> arguments,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        try
        {
            var result = runner.Run(new ProcessSpec
            {
                FileName = binary,
                Arguments = arguments,
                Timeout = TimeSpan.FromSeconds(Math.Min(30, options.VerificationTimeoutSeconds)),
            }, ct);
            if (result.TimedOut || result.ExitCode != 0)
                return null;
            var output = (result.StandardOutput ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (output.Length == 0)
                return null;
            return output.Length > 128 ? output[..128] : output;
        }
        catch (ArtifactBlockedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when a probed version string meets a minimum major version.</summary>
    public static bool MeetsMinimum(string? version, int minimumMajor)
    {
        if (!TryParseMajorMinor(version, out var major, out _))
            return false;
        return major >= minimumMajor;
    }

    /// <summary>True when a probed version string meets a minimum major.minor version.</summary>
    public static bool MeetsMinimum(string? version, int minimumMajor, int minimumMinor)
    {
        if (!TryParseMajorMinor(version, out var major, out var minor))
            return false;
        return major > minimumMajor || (major == minimumMajor && minor >= minimumMinor);
    }

    private static bool TryParseMajorMinor(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version))
            return false;
        // First ASCII digit run shaped like major[.minor]; never a substring
        // match on identity strings — versions only.
        var index = 0;
        while (index < version.Length && !char.IsAsciiDigit(version[index]))
            index++;
        var start = index;
        while (index < version.Length && char.IsAsciiDigit(version[index]))
            index++;
        if (index == start || !int.TryParse(version[start..index], out major))
            return false;
        if (index < version.Length && version[index] == '.' && index + 1 < version.Length && char.IsAsciiDigit(version[index + 1]))
        {
            var minorStart = index + 1;
            var minorEnd = minorStart;
            while (minorEnd < version.Length && char.IsAsciiDigit(version[minorEnd]))
                minorEnd++;
            if (!int.TryParse(version[minorStart..minorEnd], out minor))
                return false;
        }
        return true;
    }
}
