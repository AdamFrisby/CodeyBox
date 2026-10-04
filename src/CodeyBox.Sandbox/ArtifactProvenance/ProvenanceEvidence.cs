using System.Text.Json;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>
/// Admission outcome. Cryptographic validity, identity-policy match, missing
/// evidence, and infrastructure failure are distinct values: a failure of one
/// kind is never reported — or handled — as another, and only
/// <see cref="Admitted"/> permits execution.
/// </summary>
public enum ProvenanceOutcome
{
    /// <summary>Cryptography valid and every policy constraint matched.</summary>
    Admitted,

    /// <summary>Signature verification failed or staged bytes differ from signed bytes.</summary>
    CryptographicallyInvalid,

    /// <summary>Signature valid but publisher/issuer differs from policy (arbitrary identity).</summary>
    IdentityMismatch,

    /// <summary>Staged digest differs from the policy-pinned subject digest.</summary>
    SubjectDigestMismatch,

    /// <summary>Repository, workflow, source-ref, or predicate constraint mismatch.</summary>
    ProvenanceMismatch,

    /// <summary>No usable evidence: no sidecar, unknown kind, or malformed envelope.</summary>
    MissingEvidence,

    /// <summary>Verifier binary missing, network discovery disabled, or version unusable.</summary>
    VerifierUnavailable,

    /// <summary>Timeout, cancellation, oversize input, I/O fault, or verifier crash.</summary>
    InfrastructureFailure,
}

/// <summary>
/// Bounded, secret-free provenance evidence persisted for one admission
/// decision. Carries the artifact digest, verifier and version,
/// identity/issuer, predicate/source/workflow constraints, verification time,
/// policy digest, and outcome. Public-key fingerprints stand in for key
/// material; private keys, tokens, and full PEMs never appear here or in logs.
/// </summary>
public sealed record ArtifactProvenanceEvidence
{
    /// <summary>Maximum characters kept in a free-text detail.</summary>
    public const int MaxDetailChars = 512;

    /// <summary>Operator artifact label from the matched policy entry.</summary>
    public string ArtifactId { get; init; } = string.Empty;

    /// <summary>Subject digest of the admitted staged bytes (<c>sha256:</c> + hex).</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Bundle digest when dependencies joined the trust boundary.</summary>
    public string? BundleDigest { get; init; }

    /// <summary>Verifier that produced the verdict (<c>platform-ecdsa</c>, <c>cosign</c>, <c>gh</c>).</summary>
    public string Verifier { get; init; } = string.Empty;

    /// <summary>Verifier version string (bounded, redacted).</summary>
    public string VerifierVersion { get; init; } = string.Empty;

    /// <summary>Publisher identity observed in verified evidence.</summary>
    public string Identity { get; init; } = string.Empty;

    /// <summary>Issuer observed in verified evidence.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Repository constraint observed in verified evidence.</summary>
    public string? Repository { get; init; }

    /// <summary>Workflow constraint observed in verified evidence.</summary>
    public string? Workflow { get; init; }

    /// <summary>Source-ref constraint observed in verified evidence.</summary>
    public string? SourceRef { get; init; }

    /// <summary>Predicate/build-type constraint observed in verified evidence.</summary>
    public string? PredicateType { get; init; }

    /// <summary>UTC time the verdict was produced.</summary>
    public DateTimeOffset VerifiedAtUtc { get; init; }

    /// <summary>Digest of the trust policy that produced the verdict.</summary>
    public string PolicyDigest { get; init; } = string.Empty;

    /// <summary>Admission outcome. Only <see cref="ProvenanceOutcome.Admitted"/> permits execution.</summary>
    public ProvenanceOutcome Outcome { get; init; }

    /// <summary>Bounded operator-facing detail. Never carries key material or secrets.</summary>
    public string? Detail { get; init; }

    /// <summary>True only for <see cref="ProvenanceOutcome.Admitted"/>.</summary>
    public bool IsAdmitted => Outcome == ProvenanceOutcome.Admitted;

    /// <summary>
    /// Short provenance identity for baseline cache fingerprints:
    /// the admitted digest plus the verified publisher/issuer and policy digest.
    /// </summary>
    public string ToFingerprintIdentity() =>
        $"{Digest}\u001f{Identity}\u001f{Issuer}\u001f{PolicyDigest}";

    /// <summary>Serializes the evidence to redacted JSON for persistence.</summary>
    public string ToRedactedJson() =>
        JsonSerializer.Serialize(new
        {
            ArtifactId,
            Digest,
            BundleDigest,
            Verifier,
            VerifierVersion,
            Identity,
            Issuer,
            Repository,
            Workflow,
            SourceRef,
            PredicateType,
            VerifiedAtUtc,
            PolicyDigest,
            Outcome = Outcome.ToString(),
            Detail = Detail is null ? null : Detail.Length > MaxDetailChars ? Detail[..MaxDetailChars] : Detail,
        });

    internal static string BoundDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;
        var oneLine = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length > MaxDetailChars ? oneLine[..MaxDetailChars] : oneLine;
    }
}

/// <summary>
/// Typed enforcement failure raised when an artifact is refused admission to
/// an executable boundary. Carries the machine-readable outcome and the
/// secret-free evidence so providers surface a visible blocked/unavailable
/// result instead of silently loading the artifact.
/// </summary>
public sealed class ArtifactBlockedException : InvalidOperationException
{
    /// <summary>Machine-readable refusal reason.</summary>
    public ProvenanceOutcome Outcome { get; }

    /// <summary>Secret-free evidence for the refusal, when produced.</summary>
    public ArtifactProvenanceEvidence? Evidence { get; }

    public ArtifactBlockedException(string message, ProvenanceOutcome outcome, ArtifactProvenanceEvidence? evidence = null)
        : base(message)
    {
        Outcome = outcome;
        Evidence = evidence;
    }
}
