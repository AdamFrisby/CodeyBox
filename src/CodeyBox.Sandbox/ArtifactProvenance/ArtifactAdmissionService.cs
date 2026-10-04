using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>
/// Admitted plugin bundle: the private staged primary path (the exact bytes
/// the loader must load) plus the secret-free evidence for the decision.
/// </summary>
public sealed record PluginBundleAdmission
{
    /// <summary>Private staged copy of the primary assembly. Load this, not the source.</summary>
    public required string StagedPrimaryPath { get; init; }

    /// <summary>Admission evidence.</summary>
    public required ArtifactProvenanceEvidence Evidence { get; init; }
}

/// <summary>
/// Admitted tool executable: the private staged path (the exact bytes the
/// provider must provision) plus the secret-free evidence for the decision.
/// </summary>
public sealed record ToolExecutableAdmission
{
    /// <summary>Private staged copy of the tool executable. Provision this, not the source.</summary>
    public required string StagedPath { get; init; }

    /// <summary>Admission evidence.</summary>
    public required ArtifactProvenanceEvidence Evidence { get; init; }
}

/// <summary>
/// Enforces operator-owned artifact provenance before executable admission.
/// Verification and consumption operate on the same immutable staged bytes:
/// sources are copied once into a private directory (no symlinks followed,
/// caps enforced during the copy), hashed while copying, verified, and
/// re-hashed before the result is returned. Verdicts are cached by
/// digest + policy + verifier inputs, so a replaced file or an edited policy
/// never reuses a stale result.
/// </summary>
public sealed class ArtifactAdmissionService : IDisposable
{
    private readonly IVerifierProcessRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IArtifactVerifier>? _verifierFactory;
    private readonly string _stageRoot;
    private readonly ConcurrentDictionary<string, ArtifactProvenanceEvidence> _verdicts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _probedVersions = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Reserved prefix for private staging roots (destructive recovery can key on it).</summary>
    public const string StageRootPrefix = ".codeybox-provenance-";

    /// <summary>Maximum dependency files accepted in one plugin bundle.</summary>
    public const int MaxBundleFiles = 256;

    public ArtifactAdmissionService(IVerifierProcessRunner? runner = null, TimeProvider? timeProvider = null)
        : this(verifierFactory: null, runner: runner, timeProvider: timeProvider)
    {
    }

    /// <summary>
    /// Test seam: fixed verifier resolution (e.g. a controlled fake) with the
    /// production admission, staging, caching, and policy-matching logic.
    /// Production code uses the default constructor.
    /// </summary>
    internal ArtifactAdmissionService(
        Func<string, IArtifactVerifier>? verifierFactory,
        IVerifierProcessRunner? runner = null,
        TimeProvider? timeProvider = null)
    {
        _runner = runner ?? new BoundedProcessRunner();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stageRoot = ArtifactStaging.CreatePrivateRoot(StageRootPrefix);
        _verifierFactory = verifierFactory;
    }

    /// <summary>True when the snapshot enforces verification.</summary>
    public static bool IsEnforcementEnabled(ArtifactTrustOptions? options) => options is not null && options.Enabled;

    /// <summary>
    /// Stages a plugin bundle (primary assembly plus co-located executable
    /// dependencies) and verifies it against the trust policy. Returns the
    /// staged primary path for loading. Throws <see cref="ArtifactBlockedException"/>
    /// on any refusal; never returns an admitted path for unverified bytes.
    /// </summary>
    public PluginBundleAdmission AdmitPluginBundle(
        string assemblyPath,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var policyDigest = options.ComputePolicyDigest();
        var staged = ArtifactStaging.StagePluginBundle(assemblyPath, _stageRoot, options.MaxArtifactBytes, ct);
        var entry = FindEntry(options, staged.PrimaryDigestHex, staged.BundleDigestHex);
        if (entry is null)
        {
            var artifact = staged.ToStagedArtifact();
            throw new ArtifactBlockedException(
                $"Plugin bundle '{Truncate(assemblyPath)}' digest sha256:{staged.PrimaryDigestHex[..16]}… is not in the artifact trust policy; refusing to load.",
                staged.Dependencies.Count == 0 ? ProvenanceOutcome.MissingEvidence : ProvenanceOutcome.SubjectDigestMismatch,
                DeniedEvidence(artifact, policyDigest, staged.Dependencies.Count == 0 ? ProvenanceOutcome.MissingEvidence : ProvenanceOutcome.SubjectDigestMismatch, "no policy entry pins this digest"));
        }

        var evidence = VerifyWithCache(
            kind: "plugin-bundle",
            staged: staged.ToStagedArtifact(),
            entry: entry,
            options: options,
            policyDigest: policyDigest,
            ct: ct);
        return new PluginBundleAdmission { StagedPrimaryPath = staged.StagedPrimaryPath, Evidence = evidence };
    }

    /// <summary>
    /// Stages a single tool executable and verifies it against the trust
    /// policy. Returns the private staged path (the exact bytes the provider
    /// must provision) plus the evidence. Throws
    /// <see cref="ArtifactBlockedException"/> on any refusal.
    /// </summary>
    public ToolExecutableAdmission AdmitToolExecutable(
        string sourcePath,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var policyDigest = options.ComputePolicyDigest();
        var staged = ArtifactStaging.StageSingleFile(sourcePath, _stageRoot, options.MaxArtifactBytes, ct);
        var entry = FindEntry(options, staged.PrimaryDigestHex, bundleDigestHex: null);
        if (entry is null)
        {
            throw new ArtifactBlockedException(
                $"Tool artifact '{Truncate(staged.PrimaryFileName)}' digest sha256:{staged.PrimaryDigestHex[..16]}… is not in the artifact trust policy; refusing to provision.",
                ProvenanceOutcome.MissingEvidence,
                DeniedEvidence(staged, policyDigest, ProvenanceOutcome.MissingEvidence, "no policy entry pins this digest"));
        }

        var evidence = VerifyWithCache("tool-executable", staged, entry, options, policyDigest, ct);
        return new ToolExecutableAdmission { StagedPath = staged.StagedPrimaryPath, Evidence = evidence };
    }

    /// <summary>
    /// Verifies provider-staged executable bytes in place (the same file the
    /// provider later pushes to the guest) against the trust policy. The
    /// staged file is re-hashed here; a digest change since the provider's
    /// staging fails closed. Throws <see cref="ArtifactBlockedException"/> on refusal.
    /// </summary>
    public ArtifactProvenanceEvidence VerifyStagedExecutable(
        string stagedPath,
        string stagedDigestHex,
        string sourceDirectory,
        string sourceFileName,
        ArtifactTrustOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDigestHex);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var policyDigest = options.ComputePolicyDigest();
        var actual = ArtifactStaging.HashStagedFile(stagedPath, options.MaxArtifactBytes, ct);
        if (!string.Equals(actual, stagedDigestHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactBlockedException(
                "Staged executable bytes changed between staging and verification; refusing to provision.",
                ProvenanceOutcome.CryptographicallyInvalid);
        }
        var normalized = actual.ToLowerInvariant();
        var entry = FindEntry(options, normalized, bundleDigestHex: null);
        if (entry is null)
        {
            throw new ArtifactBlockedException(
                $"Tool artifact '{Truncate(sourceFileName)}' digest sha256:{normalized[..16]}… is not in the artifact trust policy; refusing to provision.",
                ProvenanceOutcome.MissingEvidence,
                DeniedEvidenceForTool(sourceFileName, normalized, policyDigest, ProvenanceOutcome.MissingEvidence, "no policy entry pins this digest"));
        }
        if (entry.BundleSha256 is not null)
        {
            throw new ArtifactBlockedException(
                $"Tool artifact '{Truncate(sourceFileName)}' matched a bundle policy entry; single-file tools cannot satisfy bundle scope.",
                ProvenanceOutcome.SubjectDigestMismatch,
                DeniedEvidenceForTool(sourceFileName, normalized, policyDigest, ProvenanceOutcome.SubjectDigestMismatch, "bundle-scoped entry cannot admit a single file"));
        }

        var staged = new StagedArtifact
        {
            StagedPrimaryPath = Path.GetFullPath(stagedPath),
            PrimaryDigestHex = normalized,
            BundleDigestHex = null,
            Dependencies = [],
            SourceDirectory = sourceDirectory,
            PrimaryFileName = sourceFileName,
        };
        return VerifyWithCache("tool-executable", staged, entry, options, policyDigest, ct);
    }

    private ArtifactProvenanceEvidence VerifyWithCache(
        string kind,
        StagedArtifact staged,
        TrustedArtifactPolicy entry,
        ArtifactTrustOptions options,
        string policyDigest,
        CancellationToken ct)
    {
        var cacheKey = ComputeCacheKey(kind, staged, entry, options, policyDigest);
        if (_verdicts.TryGetValue(cacheKey, out var cached))
        {
            // Substitution guard: the bytes under this call must still carry
            // the cached digest; a replaced file never reuses the verdict.
            var fresh = ArtifactStaging.HashStagedFile(staged.StagedPrimaryPath, options.MaxArtifactBytes, ct);
            if (!string.Equals(fresh, staged.PrimaryDigestHex, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(staged.PrimaryDigestHex, ExtractDigest(cached.Digest), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArtifactBlockedException(
                    "Staged artifact bytes changed; cached verification does not apply.",
                    ProvenanceOutcome.CryptographicallyInvalid);
            }
            return cached;
        }

        var evidence = VerifyUncached(kind, staged, entry, options, policyDigest, ct);
        if (_verdicts.Count < options.MaxCachedVerdicts)
            _verdicts[cacheKey] = evidence;
        return evidence;
    }

    private ArtifactProvenanceEvidence VerifyUncached(
        string kind,
        StagedArtifact staged,
        TrustedArtifactPolicy entry,
        ArtifactTrustOptions options,
        string policyDigest,
        CancellationToken ct)
    {
        RequireSidecarPresence(staged, entry);
        var verifier = CreateVerifier(entry.Evidence);
        VerifierVerdict verdict;
        try
        {
            verdict = verifier.Verify(staged, entry, options, ct);
        }
        catch (ArtifactBlockedException ex) when (ex.Outcome is ProvenanceOutcome.VerifierUnavailable or ProvenanceOutcome.InfrastructureFailure)
        {
            throw new ArtifactBlockedException(
                $"Artifact '{Truncate(entry.ArtifactId)}' cannot be verified ({ex.Outcome}): {ArtifactProvenanceEvidence.BoundDetail(ex.Message)}",
                ex.Outcome,
                DeniedEvidence(staged, policyDigest, ex.Outcome, ex.Message));
        }
        if (!verdict.CryptographicallyValid)
        {
            throw new ArtifactBlockedException(
                $"Artifact '{Truncate(entry.ArtifactId)}' failed cryptographic verification; refusing to execute.",
                ProvenanceOutcome.CryptographicallyInvalid,
                DeniedEvidence(staged, policyDigest, ProvenanceOutcome.CryptographicallyInvalid, verdict.Detail));
        }

        var outcome = MatchPolicy(entry, verdict, staged);
        if (outcome != ProvenanceOutcome.Admitted)
        {
            var reason = outcome switch
            {
                ProvenanceOutcome.IdentityMismatch => "publisher or issuer does not match policy",
                ProvenanceOutcome.SubjectDigestMismatch => "subject digest does not match policy",
                _ => "provenance constraints do not match policy",
            };
            throw new ArtifactBlockedException(
                $"Artifact '{Truncate(entry.ArtifactId)}' {reason}; refusing to execute.",
                outcome,
                DeniedEvidence(staged, policyDigest, outcome, reason));
        }

        // Final consumption guard: re-hash the exact staged bytes and require
        // the pinned digests before handing them out.
        var rehash = ArtifactStaging.HashStagedFile(staged.StagedPrimaryPath, options.MaxArtifactBytes, ct);
        if (!string.Equals(rehash, staged.PrimaryDigestHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactBlockedException(
                "Staged artifact bytes changed during verification; refusing to execute.",
                ProvenanceOutcome.CryptographicallyInvalid);
        }

        return new ArtifactProvenanceEvidence
        {
            ArtifactId = entry.ArtifactId,
            Digest = "sha256:" + staged.PrimaryDigestHex,
            BundleDigest = staged.BundleDigestHex is null ? null : "sha256:" + staged.BundleDigestHex,
            Verifier = verdict.Verifier,
            VerifierVersion = verdict.VerifierVersion,
            Identity = verdict.ObservedPublisher,
            Issuer = verdict.ObservedIssuer,
            Repository = verdict.ObservedRepository,
            Workflow = verdict.ObservedWorkflow,
            SourceRef = verdict.ObservedSourceRef,
            PredicateType = verdict.ObservedPredicateType,
            VerifiedAtUtc = _timeProvider.GetUtcNow(),
            PolicyDigest = policyDigest,
            Outcome = ProvenanceOutcome.Admitted,
            Detail = $"{kind} admitted; bundle files: {1 + staged.Dependencies.Count}",
        };
    }

    private static ProvenanceOutcome MatchPolicy(TrustedArtifactPolicy entry, VerifierVerdict verdict, StagedArtifact staged)
    {
        if (!string.Equals(verdict.ObservedPublisher, entry.Publisher, StringComparison.Ordinal)
            || !string.Equals(verdict.ObservedIssuer, entry.Issuer, StringComparison.Ordinal))
            return ProvenanceOutcome.IdentityMismatch;
        if (!string.Equals(staged.PrimaryDigestHex, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            return ProvenanceOutcome.SubjectDigestMismatch;
        if (entry.BundleSha256 is not null && !string.Equals(staged.BundleDigestHex, entry.BundleSha256, StringComparison.OrdinalIgnoreCase))
            return ProvenanceOutcome.SubjectDigestMismatch;
        if (!MatchesOptional(entry.Repository, verdict.ObservedRepository)
            || !MatchesOptional(entry.Workflow, verdict.ObservedWorkflow)
            || !MatchesOptional(entry.SourceRef, verdict.ObservedSourceRef)
            || !MatchesOptional(entry.PredicateType, verdict.ObservedPredicateType))
            return ProvenanceOutcome.ProvenanceMismatch;
        return ProvenanceOutcome.Admitted;
    }

    private static bool MatchesOptional(string? expected, string? observed) =>
        expected is null || string.Equals(expected, observed, StringComparison.Ordinal);

    private static void RequireSidecarPresence(StagedArtifact staged, TrustedArtifactPolicy entry)
    {
        var provenance = Path.Combine(staged.SourceDirectory, staged.PrimaryFileName + ArtifactSidecars.ProvenanceSuffix);
        if (!ArtifactSidecars.ExistsWithinLimit(provenance, long.MaxValue))
        {
            throw new ArtifactBlockedException(
                $"Artifact '{Truncate(entry.ArtifactId)}' has no provenance statement; refusing to execute.",
                ProvenanceOutcome.MissingEvidence,
                new ArtifactProvenanceEvidence
                {
                    ArtifactId = entry.ArtifactId,
                    Digest = "sha256:" + staged.PrimaryDigestHex,
                    Verifier = entry.Evidence,
                    Outcome = ProvenanceOutcome.MissingEvidence,
                    Detail = "missing provenance statement sidecar",
                });
        }
        var kindSidecar = entry.Evidence switch
        {
            ArtifactEvidenceKinds.OpensslLocal => staged.PrimaryFileName + ArtifactSidecars.SignatureSuffix,
            ArtifactEvidenceKinds.SigstoreBundle => staged.PrimaryFileName + ArtifactSidecars.SigstoreSuffix,
            ArtifactEvidenceKinds.CosignLocalKey => staged.PrimaryFileName + ArtifactSidecars.SigstoreSuffix,
            _ => null,
        };
        if (kindSidecar is not null
            && !ArtifactSidecars.ExistsWithinLimit(Path.Combine(staged.SourceDirectory, kindSidecar), long.MaxValue))
        {
            throw new ArtifactBlockedException(
                $"Artifact '{Truncate(entry.ArtifactId)}' is missing '{kindSidecar}'; refusing to execute.",
                ProvenanceOutcome.MissingEvidence);
        }
    }

    private IArtifactVerifier CreateVerifier(string evidenceKind)
    {
        if (_verifierFactory?.Invoke(evidenceKind) is { } testVerifier)
            return testVerifier;
        return evidenceKind switch
        {
            ArtifactEvidenceKinds.OpensslLocal => new PlatformEcdsaVerifier(),
            ArtifactEvidenceKinds.SigstoreBundle => new CosignVerifier(_runner),
            ArtifactEvidenceKinds.CosignLocalKey => new CosignLocalKeyVerifier(_runner),
            ArtifactEvidenceKinds.GitHubAttestation => new GitHubAttestationVerifier(_runner),
            _ => throw new ArtifactBlockedException(
                $"Evidence kind '{evidenceKind}' cannot admit executables.",
                ProvenanceOutcome.MissingEvidence),
        };
    }

    private static TrustedArtifactPolicy? FindEntry(ArtifactTrustOptions options, string primaryDigestHex, string? bundleDigestHex)
    {
        foreach (var entry in options.TrustedArtifacts)
        {
            if (entry is null)
                continue;
            if (!string.Equals(entry.Sha256, primaryDigestHex, StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.BundleSha256 is null)
            {
                if (bundleDigestHex is not null)
                    continue;
                return entry;
            }
            if (string.Equals(entry.BundleSha256, bundleDigestHex, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private string ComputeCacheKey(
        string kind,
        StagedArtifact staged,
        TrustedArtifactPolicy entry,
        ArtifactTrustOptions options,
        string policyDigest)
    {
        // Cache inputs: subject digests, trust policy digest, verifier
        // identity (name + probed version + binary path), timeout, and the
        // pinned-key fingerprint. A replaced file or an edited policy,
        // binary, or timeout never reuses a stale verdict.
        var version = entry.Evidence switch
        {
            ArtifactEvidenceKinds.OpensslLocal => "platform-ecdsa:" + VerifierVersions.Platform(),
            ArtifactEvidenceKinds.SigstoreBundle => "cosign:" + ProbedVersion(options.CosignBinaryPath, ["version", "--json"], options)
                + ":" + options.CosignBinaryPath,
            ArtifactEvidenceKinds.CosignLocalKey => "cosign-local-key:" + ProbedVersion(options.CosignBinaryPath, ["version", "--json"], options)
                + ":" + options.CosignBinaryPath,
            _ => "gh:" + ProbedVersion(options.GhBinaryPath, ["--version"], options)
                + ":" + options.GhBinaryPath,
        };
        var input = string.Join("\u001f",
            kind,
            staged.PrimaryDigestHex.ToLowerInvariant(),
            staged.BundleDigestHex?.ToLowerInvariant() ?? string.Empty,
            entry.Evidence,
            policyDigest,
            version,
            options.VerificationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ArtifactTrustOptions.FingerprintPublicKey(entry.PublicKeyPem));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private string ProbedVersion(string binary, IReadOnlyList<string> arguments, ArtifactTrustOptions options)
    {
        var key = binary + "\u001f" + string.Join("\u001f", arguments);
        if (_probedVersions.TryGetValue(key, out var cached))
            return cached ?? string.Empty;
        string? probed = null;
        try
        {
            probed = VerifierVersions.Probe(_runner, binary, arguments, options, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            probed = null;
        }
        if (_probedVersions.Count < 16)
            _probedVersions[key] = probed;
        return probed ?? string.Empty;
    }

    private ArtifactProvenanceEvidence DeniedEvidence(
        StagedArtifact staged,
        string policyDigest,
        ProvenanceOutcome outcome,
        string? detail) => new()
        {
            ArtifactId = string.Empty,
            Digest = "sha256:" + staged.PrimaryDigestHex,
            BundleDigest = staged.BundleDigestHex is null ? null : "sha256:" + staged.BundleDigestHex,
            Verifier = string.Empty,
            VerifierVersion = string.Empty,
            Identity = string.Empty,
            Issuer = string.Empty,
            VerifiedAtUtc = _timeProvider.GetUtcNow(),
            PolicyDigest = policyDigest,
            Outcome = outcome,
            Detail = ArtifactProvenanceEvidence.BoundDetail(detail),
        };

    private ArtifactProvenanceEvidence DeniedEvidenceForTool(
        string fileName,
        string digestHex,
        string policyDigest,
        ProvenanceOutcome outcome,
        string? detail) => new()
        {
            ArtifactId = fileName,
            Digest = "sha256:" + digestHex,
            Verifier = string.Empty,
            VerifierVersion = string.Empty,
            Identity = string.Empty,
            Issuer = string.Empty,
            VerifiedAtUtc = _timeProvider.GetUtcNow(),
            PolicyDigest = policyDigest,
            Outcome = outcome,
            Detail = ArtifactProvenanceEvidence.BoundDetail(detail),
        };

    private static string ExtractDigest(string fingerprint) =>
        fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ? fingerprint["sha256:".Length..] : fingerprint;

    private static string Truncate(string? value, int max = 64)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ArtifactAdmissionService));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { Directory.Delete(_stageRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
