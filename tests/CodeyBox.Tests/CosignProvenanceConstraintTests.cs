using System.Security.Cryptography;
using System.Text.Json;
using CodeyBox.Sandbox.ArtifactProvenance;

namespace CodeyBox.Tests;

/// <summary>
/// Regression guard for the cosign forged-sidecar hole: cosign verify-blob
/// attests only publisher/issuer, so repository/workflow/source-ref/predicate
/// constraints must fail closed and the success verdict must carry
/// verifier-backed identity (policy values / pinned key), never
/// artifact-supplied sidecar fields.
/// </summary>
public sealed class CosignProvenanceConstraintTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "codeybox-cosign-tests-" + Guid.NewGuid().ToString("N"));

    public CosignProvenanceConstraintTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class ScriptedRunner : IVerifierProcessRunner
    {
        public int VerifyExitCode { get; set; }
        public List<IReadOnlyList<string>> SeenArguments { get; } = [];

        public ProcessResult Run(ProcessSpec spec, CancellationToken ct)
        {
            SeenArguments.Add(spec.Arguments.ToArray());
            if (spec.Arguments is ["version", "--json"])
            {
                return new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "v2.4.1",
                    StandardError = string.Empty,
                    TimedOut = false,
                };
            }
            return new ProcessResult
            {
                ExitCode = VerifyExitCode,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                TimedOut = false,
            };
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static TrustedArtifactPolicy SigstoreEntry(string publisher, string issuer)
    {
        var entry = new TrustedArtifactPolicy
        {
            ArtifactId = "sigstore-fixture",
            Sha256 = new string('a', 64),
            Publisher = publisher,
            Issuer = issuer,
            Evidence = ArtifactEvidenceKinds.SigstoreBundle,
        };
        entry.Repository = "https://example.invalid/org/repo";
        return entry;
    }

    [Fact]
    public void CosignVerifier_ForgedSidecarRepo_FailsClosedDespiteValidSignature()
    {
        var runner = new ScriptedRunner { VerifyExitCode = 0 };
        var verifier = new CosignVerifier(runner);
        var policy = SigstoreEntry("test-publisher", "https://issuer.example");
        StagedArtifact staged;
        {
            var sourceDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceDir);
            var source = Path.Combine(sourceDir, "tool.sh");
            File.WriteAllText(source, "bytes");
            var artifactDigest = Sha256Of(source);
            var statement = new
            {
                digest = artifactDigest,
                publisher = "test-publisher",
                issuer = "https://issuer.example",
                repository = "https://example.invalid/org/repo",
                workflow = "release.yml",
                sourceRef = "refs/tags/v1",
                predicateType = "https://slsa.dev/provenance/v1",
            };
            File.WriteAllText(source + ".provenance.json", JsonSerializer.Serialize(statement));
            File.WriteAllBytes(source + ".sigstore.json", new byte[] { 1, 2, 3 });
            var stagedPath = Path.Combine(sourceDir, "tool.sh.staged");
            File.Copy(source, stagedPath);
            staged = new StagedArtifact
            {
                StagedPrimaryPath = stagedPath,
                PrimaryDigestHex = artifactDigest,
                SourceDirectory = sourceDir,
                PrimaryFileName = "tool.sh",
                Dependencies = [],
            };
        }

        var verdict = verifier.Verify(staged, policy, new ArtifactTrustOptions(), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
        Assert.Contains("repository", verdict.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CosignVerifier_SuccessVerdict_UsesPolicyIdentityNotSidecar()
    {
        var runner = new ScriptedRunner { VerifyExitCode = 0 };
        var verifier = new CosignVerifier(runner);
        var policy = new TrustedArtifactPolicy
        {
            ArtifactId = "sigstore-fixture",
            Sha256 = new string('a', 64),
            Publisher = "test-publisher",
            Issuer = "https://issuer.example",
            Evidence = ArtifactEvidenceKinds.SigstoreBundle,
        };
        var sourceDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "tool.sh");
        File.WriteAllText(source, "bytes");
        var digest = Sha256Of(source);
        File.WriteAllText(source + ".provenance.json", JsonSerializer.Serialize(new
        {
            digest,
            publisher = "forged-publisher",
            issuer = "forged-issuer",
            repository = "https://example.invalid/forged",
        }));
        File.WriteAllBytes(source + ".sigstore.json", new byte[] { 1, 2, 3 });
        var stagedPath = Path.Combine(sourceDir, "tool.sh.staged");
        File.Copy(source, stagedPath);
        var staged = new StagedArtifact
        {
            StagedPrimaryPath = stagedPath,
            PrimaryDigestHex = digest,
            SourceDirectory = sourceDir,
            PrimaryFileName = "tool.sh",
            Dependencies = [],
        };

        var verdict = verifier.Verify(staged, policy, new ArtifactTrustOptions(), CancellationToken.None);

        Assert.True(verdict.CryptographicallyValid);
        Assert.Equal("test-publisher", verdict.ObservedPublisher);
        Assert.Equal("https://issuer.example", verdict.ObservedIssuer);
        Assert.Null(verdict.ObservedRepository);
        Assert.Null(verdict.ObservedWorkflow);
        Assert.Null(verdict.ObservedSourceRef);
        Assert.Null(verdict.ObservedPredicateType);
    }

    [Fact]
    public void CosignLocalKeyVerifier_ForgedSidecarRepo_FailsClosedDespiteValidSignature()
    {
        const string pem = "-----BEGIN PUBLIC KEY-----\ntest\n-----END PUBLIC KEY-----\n";
        var runner = new ScriptedRunner { VerifyExitCode = 0 };
        var verifier = new CosignLocalKeyVerifier(runner);
        var sourceDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "tool.sh");
        File.WriteAllText(source, "bytes");
        var digest = Sha256Of(source);
        File.WriteAllText(source + ".provenance.json", JsonSerializer.Serialize(new
        {
            digest,
            publisher = "anything",
            issuer = "anything",
            repository = "https://example.invalid/org/repo",
        }));
        File.WriteAllBytes(source + ".sigstore.json", new byte[] { 5, 6, 7 });
        var stagedPath = Path.Combine(sourceDir, "tool.sh.staged");
        File.Copy(source, stagedPath);
        var staged = new StagedArtifact
        {
            StagedPrimaryPath = stagedPath,
            PrimaryDigestHex = digest,
            SourceDirectory = sourceDir,
            PrimaryFileName = "tool.sh",
            Dependencies = [],
        };
        var policy = new TrustedArtifactPolicy
        {
            ArtifactId = "local-fixture",
            Sha256 = new string('a', 64),
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(pem),
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Repository = "https://example.invalid/org/repo",
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pem,
        };

        var verdict = verifier.Verify(staged, policy, new ArtifactTrustOptions(), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
        Assert.Contains("repository", verdict.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
