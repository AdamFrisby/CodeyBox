using System.Security.Cryptography;
using System.Text.Json;
using CodeyBox.Sandbox.ArtifactProvenance;

namespace CodeyBox.Tests;

/// <summary>
/// Guards the gh attestation boundary: the success verdict's identity must
/// come from gh's verified JSON output, never from the artifact-supplied
/// sidecar, and the local bundle must travel to gh via --bundle.
/// </summary>
public sealed class GitHubAttestationVerifierTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "codeybox-gh-tests-" + Guid.NewGuid().ToString("N"));

    public GitHubAttestationVerifierTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class ScriptedRunner : IVerifierProcessRunner
    {
        public List<IReadOnlyList<string>> SeenArguments { get; } = [];
        public string VerifyStdout { get; set; } = string.Empty;
        public int VerifyExitCode { get; set; }

        public ProcessResult Run(ProcessSpec spec, CancellationToken ct)
        {
            SeenArguments.Add(spec.Arguments.ToArray());
            if (spec.Arguments.Count == 1 && spec.Arguments[0] == "--version")
            {
                return new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "gh version 2.66.0",
                    StandardError = string.Empty,
                    TimedOut = false,
                };
            }
            return new ProcessResult
            {
                ExitCode = VerifyExitCode,
                StandardOutput = VerifyStdout,
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

    private (string stagedPath, StagedArtifact staged) WriteStaged(string name, string content, string sidecarPublisher)
    {
        var sourceDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, name);
        File.WriteAllText(source, content);
        var digest = Sha256Of(source);
        var statement = JsonSerializer.Serialize(new
        {
            digest,
            publisher = sidecarPublisher,
            issuer = "sidecar-issuer",
            repository = "sidecar/repo",
            workflow = "sidecar-workflow",
            sourceRef = "sidecar-ref",
            predicateType = "sidecar-predicate",
        });
        File.WriteAllText(source + ".provenance.json", statement);
        File.WriteAllText(source + ".attestation.json", "{}");
        var stagedPath = Path.Combine(sourceDir, name + ".staged");
        File.Copy(source, stagedPath);
        var staged = new StagedArtifact
        {
            StagedPrimaryPath = stagedPath,
            PrimaryDigestHex = digest,
            Dependencies = [],
            SourceDirectory = sourceDir,
            PrimaryFileName = name,
        };
        return (stagedPath, staged);
    }

    private static TrustedArtifactPolicy GhEntry(
        string digest,
        string publisher = "https://github.com/owner/repo/.github/workflows/build.yml@refs/heads/main",
        string issuer = "https://token.actions.githubusercontent.com") => new()
        {
            ArtifactId = "gh-tool",
            Sha256 = digest,
            Publisher = publisher,
            Issuer = issuer,
            Repository = "owner/repo",
            Workflow = "build.yml",
            SourceRef = "refs/heads/main",
            PredicateType = "https://slsa.dev/provenance/v1",
            Evidence = ArtifactEvidenceKinds.GitHubAttestation,
        };

    private static string GhStdout(
        string digest,
        string publisher = "https://github.com/owner/repo/.github/workflows/build.yml@refs/heads/main",
        string issuer = "https://token.actions.githubusercontent.com",
        string repository = "owner/repo",
        string workflow = "build.yml",
        string sourceRef = "refs/heads/main",
        string predicateType = "https://slsa.dev/provenance/v1") =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                verificationResult = new
                {
                    signature = new
                    {
                        certificate = new
                        {
                            subjectAlternativeName = publisher,
                            issuer,
                            sourceRepository = repository,
                            signerWorkflow = workflow,
                            sourceRef,
                        },
                    },
                    statement = new
                    {
                        subject = new[] { new { name = "tool", digest = new { sha256 = digest } } },
                        predicateType,
                    },
                },
            },
        });

    private static ArtifactTrustOptions GhOptions(ScriptedRunner runner, TrustedArtifactPolicy entry)
    {
        var options = new ArtifactTrustOptions
        {
            Enabled = true,
            TrustedArtifacts = [entry],
        };
        Assert.Empty(options.Validate());
        return options;
    }

    [Fact]
    public void Verify_SuccessUsesGhOutputNotSidecar()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "forged-publisher");
        var runner = new ScriptedRunner();
        var entry = GhEntry(staged.PrimaryDigestHex);
        runner.VerifyStdout = GhStdout(staged.PrimaryDigestHex);
        var verifier = new GitHubAttestationVerifier(runner);

        var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        Assert.True(verdict.CryptographicallyValid);
        Assert.Equal(entry.Publisher, verdict.ObservedPublisher);
        Assert.Equal(entry.Issuer, verdict.ObservedIssuer);
        Assert.Equal("owner/repo", verdict.ObservedRepository);
        Assert.NotEqual("forged-publisher", verdict.ObservedPublisher);
    }

    [Fact]
    public void Verify_ForgedSidecarPublisherCannotSatisfyPolicy()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "https://github.com/owner/repo/.github/workflows/build.yml@refs/heads/main");
        var runner = new ScriptedRunner();
        var entry = GhEntry(staged.PrimaryDigestHex);
        runner.VerifyStdout = GhStdout(staged.PrimaryDigestHex, publisher: "https://github.com/attacker/evil/.github/workflows/x.yml@refs/heads/main");
        var verifier = new GitHubAttestationVerifier(runner);

        var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
    }

    [Fact]
    public void Verify_WrongRepositoryWorkflowRefPredicate_AreRejected()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "x");
        var entry = GhEntry(staged.PrimaryDigestHex);
        var verifierCases = new[]
        {
            GhStdout(staged.PrimaryDigestHex, repository: "owner/other"),
            GhStdout(staged.PrimaryDigestHex, workflow: "other.yml"),
            GhStdout(staged.PrimaryDigestHex, sourceRef: "refs/heads/other"),
            GhStdout(staged.PrimaryDigestHex, predicateType: "https://example.invalid/other"),
        };
        foreach (var stdout in verifierCases)
        {
            var runner = new ScriptedRunner { VerifyStdout = stdout };
            var verifier = new GitHubAttestationVerifier(runner);
            var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);
            Assert.False(verdict.CryptographicallyValid);
        }
    }

    [Fact]
    public void Verify_WrongSubjectDigest_IsRejected()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "x");
        var runner = new ScriptedRunner
        {
            VerifyStdout = GhStdout(new string('0', 64)),
        };
        var entry = GhEntry(staged.PrimaryDigestHex);
        var verifier = new GitHubAttestationVerifier(runner);

        var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
    }

    [Fact]
    public void Verify_MalformedGhOutput_IsRejected()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "x");
        var runner = new ScriptedRunner { VerifyStdout = "not json" };
        var entry = GhEntry(staged.PrimaryDigestHex);
        var verifier = new GitHubAttestationVerifier(runner);

        var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
    }

    [Fact]
    public void Verify_GhRejection_IsRejected()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "x");
        var runner = new ScriptedRunner { VerifyStdout = string.Empty, VerifyExitCode = 1 };
        var entry = GhEntry(staged.PrimaryDigestHex);
        var verifier = new GitHubAttestationVerifier(runner);

        var verdict = verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        Assert.False(verdict.CryptographicallyValid);
    }

    [Fact]
    public void Verify_LocalBundleTravelsAsBundleFlagWithIdentityFlags()
    {
        var (_, staged) = WriteStaged("tool.bin", "bytes", sidecarPublisher: "x");
        var runner = new ScriptedRunner();
        var entry = GhEntry(staged.PrimaryDigestHex);
        runner.VerifyStdout = GhStdout(staged.PrimaryDigestHex);
        var verifier = new GitHubAttestationVerifier(runner);

        verifier.Verify(staged, entry, GhOptions(runner, entry), CancellationToken.None);

        var verifyArgs = Assert.Single(runner.SeenArguments, a => a.Contains("attestation"));
        var args = verifyArgs.ToArray();
        var bundleIndex = Array.IndexOf(args, "--bundle");
        Assert.True(bundleIndex >= 0);
        Assert.EndsWith(".attestation.json", args[bundleIndex + 1], StringComparison.Ordinal);
        Assert.Contains("--cert-identity", args);
        Assert.Contains("--cert-oidc-issuer", args);
        Assert.Contains("--source-ref", args);
        Assert.Equal(entry.Publisher, args[Array.IndexOf(args, "--cert-identity") + 1]);
        Assert.Equal(entry.Issuer, args[Array.IndexOf(args, "--cert-oidc-issuer") + 1]);
    }

    [Fact]
    public void Verify_MissingBundleWithoutDiscovery_IsUnavailable()
    {
        var sourceDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "tool.bin");
        File.WriteAllText(source, "bytes");
        var digest = Sha256Of(source);
        File.WriteAllText(source + ".provenance.json", JsonSerializer.Serialize(new
        {
            digest,
            publisher = "x",
            issuer = "y",
        }));
        var staged = new StagedArtifact
        {
            StagedPrimaryPath = source,
            PrimaryDigestHex = digest,
            Dependencies = [],
            SourceDirectory = sourceDir,
            PrimaryFileName = "tool.bin",
        };
        var runner = new ScriptedRunner();
        var entry = GhEntry(digest);
        var options = GhOptions(runner, entry);
        options.AllowNetworkDiscovery = false;
        var verifier = new GitHubAttestationVerifier(runner);

        var ex = Assert.Throws<ArtifactBlockedException>(() => verifier.Verify(staged, entry, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.VerifierUnavailable, ex.Outcome);
    }
}
