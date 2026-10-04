using System.Security.Cryptography;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.ArtifactProvenance;

namespace CodeyBox.Tests;

/// <summary>
/// Artifact provenance: operator-owned trust for plugin bundles and
/// externally staged tool executables. Hermetic tests (fake verifier +
/// real staging/caching/policy logic) prove every refusal path; the
/// <c>requires_cosign</c> test proves real cryptographic verification
/// through the supported cosign contract with committed fixtures.
/// </summary>
public sealed class ArtifactProvenanceTests : IDisposable
{
    private readonly List<ArtifactAdmissionService> _admissions = [];
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "codeybox-prov-tests-" + Guid.NewGuid().ToString("N"));

    public ArtifactProvenanceTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        foreach (var admission in _admissions)
            admission.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class CountingFakeVerifier : IArtifactVerifier
    {
        private readonly Func<StagedArtifact, VerifierVerdict> _verdict;
        public int Calls;
        public string Name => "fake-test-verifier";
        public string EvidenceKind => ArtifactEvidenceKinds.OpensslLocal;
        public CountingFakeVerifier(Func<StagedArtifact, VerifierVerdict> verdict) => _verdict = verdict;
        public VerifierVerdict Verify(
            StagedArtifact staged,
            TrustedArtifactPolicy policy,
            ArtifactTrustOptions options,
            CancellationToken ct) => _verdict(staged);
    }

    private static VerifierVerdict AdmitVerdict(
        string publisher, string issuer, string? repo = null, string? workflow = null, string? sourceRef = null) => new()
        {
            CryptographicallyValid = true,
            ObservedPublisher = publisher,
            ObservedIssuer = issuer,
            ObservedRepository = repo,
            ObservedWorkflow = workflow,
            ObservedSourceRef = sourceRef,
            Verifier = "fake-test-verifier",
            VerifierVersion = "test-1",
        };

    private ArtifactAdmissionService MakeAdmission(CountingFakeVerifier fake)
    {
        var admission = new ArtifactAdmissionService(
            verifierFactory: _ => { fake.Calls++; return fake; });
        _admissions.Add(admission);
        return admission;
    }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ArtifactTrustOptions PolicyWith(params TrustedArtifactPolicy[] entries) => new()
    {
        Enabled = true,
        TrustedArtifacts = [.. entries],
    };

    private static TrustedArtifactPolicy ToolEntry(
        string digestHex,
        string publisher = "test-publisher",
        string issuer = "test-issuer") => new()
        {
            ArtifactId = "test-tool",
            Sha256 = digestHex,
            Publisher = publisher,
            Issuer = issuer,
            Evidence = ArtifactEvidenceKinds.OpensslLocal,
        };

    private string WriteToolSource(string name, string content)
    {
        // openssl-local entries require provenance + signature sidecar
        // presence; the fake verifier never reads them.
        var path = WriteSource(name, content);
        File.WriteAllText(path + ".provenance.json", """{"digest":"00","publisher":"x","issuer":"y"}""");
        File.WriteAllText(path + ".provenance.sig", "dummy");
        return path;
    }

    // ── Policy validation ──────────────────────────────────────────────

    [Fact]
    public void Policy_DisabledByDefault()
    {
        var options = new ArtifactTrustOptions();
        Assert.False(options.Enabled);
        Assert.False(ArtifactAdmissionService.IsEnforcementEnabled(options));
        Assert.False(ArtifactAdmissionService.IsEnforcementEnabled(null));
        Assert.True(ArtifactAdmissionService.IsEnforcementEnabled(new ArtifactTrustOptions { Enabled = true }));
    }

    [Fact]
    public void Policy_InvalidEntries_AreRejected()
    {
        var options = PolicyWith(new TrustedArtifactPolicy
        {
            ArtifactId = "bad",
            Sha256 = "not-hex",
            Publisher = "",
            Issuer = "issuer",
        });
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void Policy_CosignLocalKey_PublisherMustBeKeyFingerprint()
    {
        const string pem = """
            -----BEGIN PUBLIC KEY-----
            MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEq6N1wT0HhF3y7y7y7y7y7y7y7y7y
            7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y7y
            -----END PUBLIC KEY-----
            """;
        var wrongPublisher = new TrustedArtifactPolicy
        {
            ArtifactId = "k",
            Sha256 = new string('a', 64),
            Publisher = "someone-else",
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pem,
        };
        Assert.Contains(PolicyWith(wrongPublisher).Validate(), e => e.Contains("Publisher"));

        var wrongIssuer = new TrustedArtifactPolicy
        {
            ArtifactId = "k",
            Sha256 = new string('a', 64),
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(pem),
            Issuer = "https://example.invalid",
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pem,
        };
        Assert.Contains(PolicyWith(wrongIssuer).Validate(), e => e.Contains("Issuer"));

        var correct = new TrustedArtifactPolicy
        {
            ArtifactId = "k",
            Sha256 = new string('a', 64),
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(pem),
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pem,
        };
        Assert.Empty(PolicyWith(correct).Validate());
    }

    // ── Admission: refusal paths ───────────────────────────────────────

    [Fact]
    public void AdmitTool_UnpinnedDigest_IsBlockedAndNeverExecuted()
    {
        var marker = Path.Combine(_tempRoot, "executed.marker");
        var source = WriteSource("unpinned.sh", $"#!/bin/sh\ntouch '{marker}'\n");
        using var admission = new ArtifactAdmissionService();
        _admissions.Add(admission);
        var options = PolicyWith();

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));

        Assert.Equal(ProvenanceOutcome.MissingEvidence, ex.Outcome);
        Assert.False(File.Exists(marker), "Blocked artifact must never execute.");
        Assert.NotNull(ex.Evidence);
        Assert.Equal(ProvenanceOutcome.MissingEvidence, ex.Evidence!.Outcome);
        Assert.DoesNotContain("BEGIN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdmitTool_TamperedBytes_MissPolicyAndAreBlocked()
    {
        var source = WriteToolSource("tool.sh", "original-bytes");
        var originalDigest = Sha256File(source);
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(originalDigest));

        var admitted = admission.AdmitToolExecutable(source, options, CancellationToken.None);
        Assert.Equal("sha256:" + originalDigest, admitted.Evidence.Digest);
        Assert.Equal(1, fake.Calls);

        File.WriteAllText(source, "tampered-bytes");
        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.MissingEvidence, ex.Outcome);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void AdmitTool_ValidSignatureWrongPublisher_IsBlocked()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("attacker.example", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.IdentityMismatch, ex.Outcome);
        Assert.NotNull(ex.Evidence);
        Assert.Equal(ProvenanceOutcome.IdentityMismatch, ex.Evidence!.Outcome);
        Assert.Contains("publisher or issuer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://codeybox.invalid/other", "fixture-workflow", "refs/tags/v1")]
    [InlineData("https://codeybox.invalid/fixture", "other-workflow", "refs/tags/v1")]
    [InlineData("https://codeybox.invalid/fixture", "fixture-workflow", "refs/heads/main")]
    public void AdmitTool_WrongProvenanceConstraints_AreBlocked(string observedRepo, string observedWorkflow, string observedRef)
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict(
            "test-publisher", "test-issuer", observedRepo, observedWorkflow, observedRef));
        var admission = MakeAdmission(fake);
        var entry = ToolEntry(Sha256File(source));
        entry.Repository = "https://codeybox.invalid/fixture";
        entry.Workflow = "fixture-workflow";
        entry.SourceRef = "refs/tags/v1";
        var options = PolicyWith(entry);

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.ProvenanceMismatch, ex.Outcome);
    }

    [Fact]
    public void AdmitTool_MissingSidecars_AreBlocked()
    {
        var source = WriteSource("nosidecar.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.MissingEvidence, ex.Outcome);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void AdmitTool_MalformedStatement_DoesNotParse()
    {
        Assert.Null(ProvenanceStatement.TryParse("not json"u8.ToArray()));
        Assert.Null(ProvenanceStatement.TryParse("""{"digest":"zz","publisher":"p","issuer":"i"}"""u8.ToArray()));
        Assert.Null(ProvenanceStatement.TryParse(new byte[0]));
        var valid = ProvenanceStatement.TryParse(
            """{"digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","publisher":"p","issuer":"i"}"""u8.ToArray());
        Assert.NotNull(valid);
    }

    [Fact]
    public void AdmitTool_OversizedArtifact_IsBlockedBeforeBuffering()
    {
        var source = WriteToolSource("big.sh", new string('x', 4096));
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));
        options.MaxArtifactBytes = 16;

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void AdmitTool_SymlinkSource_IsBlocked()
    {
        if (OperatingSystem.IsWindows())
            return;
        var real = WriteToolSource("real.sh", "bytes");
        var link = Path.Combine(_tempRoot, "link.sh");
        File.CreateSymbolicLink(link, real);
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(real)));

        Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(link, options, CancellationToken.None));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void AdmitTool_VerifierFailure_IsUnavailableNeverAdmitted()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var failing = new CountingFakeVerifier(s => throw new ArtifactBlockedException(
            "boom", ProvenanceOutcome.VerifierUnavailable));
        var admission = MakeAdmission(failing);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.VerifierUnavailable, ex.Outcome);
    }

    // ── Admission: success, caching, substitution ──────────────────────

    [Fact]
    public void AdmitTool_ConcurrentAdmissions_AllAdmitWithoutCorruption()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var results = new System.Collections.Concurrent.ConcurrentBag<ToolExecutableAdmission>();
        Parallel.For(0, 8, _ => results.Add(admission.AdmitToolExecutable(source, options, CancellationToken.None)));

        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.Equal("sha256:" + Sha256File(source), r.Evidence.Digest));
        Assert.All(results, r => Assert.Equal("test-publisher", r.Evidence.Identity));
    }

    [Fact]
    public void AdmitTool_RepeatAdmission_ReusesCachedVerdict()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var first = admission.AdmitToolExecutable(source, options, CancellationToken.None);
        var second = admission.AdmitToolExecutable(source, options, CancellationToken.None);

        Assert.Equal(1, fake.Calls);
        Assert.Equal(first.Evidence.VerifiedAtUtc, second.Evidence.VerifiedAtUtc);
        Assert.Equal(first.Evidence.Digest, second.Evidence.Digest);
    }

    [Fact]
    public void AdmitTool_PolicyChange_NeverReusesStaleVerdict()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var digest = Sha256File(source);

        var first = admission.AdmitToolExecutable(source, PolicyWith(ToolEntry(digest)), CancellationToken.None);
        // Same digest, different expected identity: the cached verdict must
        // not satisfy the new policy.
        var changed = PolicyWith(ToolEntry(digest, publisher: "other-publisher"));
        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, changed, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.IdentityMismatch, ex.Outcome);
        Assert.NotEqual(first.Evidence.PolicyDigest, ex.Evidence?.PolicyDigest);
    }

    [Fact]
    public void AdmitTool_ConsumeStagedBytes_SourceMutationCannotSwapBytes()
    {
        var source = WriteToolSource("tool.sh", "original");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var admitted = admission.AdmitToolExecutable(source, options, CancellationToken.None);
        File.WriteAllText(source, "mutated-after-admission");

        // The admitted path still carries the verified bytes.
        Assert.Equal("original", File.ReadAllText(admitted.StagedPath));

        // And a substituted staged copy fails the consumption re-hash.
        File.WriteAllText(admitted.StagedPath, "substituted");
        var confirmed = ArtifactStaging.HashStagedFile(admitted.StagedPath, options.MaxArtifactBytes, CancellationToken.None);
        Assert.NotEqual(admitted.Evidence.Digest, "sha256:" + confirmed);
    }

    [Fact]
    public void Evidence_Redaction_ContainsNoKeyMaterial()
    {
        var source = WriteToolSource("tool.sh", "bytes");
        var fake = new CountingFakeVerifier(s => AdmitVerdict("test-publisher", "test-issuer"));
        var admission = MakeAdmission(fake);
        var options = PolicyWith(ToolEntry(Sha256File(source)));

        var admitted = admission.AdmitToolExecutable(source, options, CancellationToken.None);
        var serialized = System.Text.Json.JsonSerializer.Serialize(admitted.Evidence);
        Assert.DoesNotContain("BEGIN", serialized, StringComparison.Ordinal);

        var blocked = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, PolicyWith(), CancellationToken.None));
        Assert.DoesNotContain("BEGIN", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheKey_BindsDigestPolicyAndVerifierInputs()
    {
        var empty = BaselineContentHash.ComputeProvenanceFingerprint([]);
        Assert.Equal(string.Empty, empty);
        Assert.Equal(
            "deadbeef",
            BaselineContentHash.CombineToolchainHash("deadbeef", null));
        Assert.Equal(
            "deadbeef",
            BaselineContentHash.CombineToolchainHash("deadbeef", string.Empty));

        var evidence = new ArtifactProvenanceEvidence
        {
            ArtifactId = "tool",
            Digest = "sha256:" + new string('a', 64),
            Verifier = "fake",
            Identity = "pub",
            Issuer = "iss",
            VerifiedAtUtc = DateTimeOffset.UtcNow,
            PolicyDigest = "policy-1",
            Outcome = ProvenanceOutcome.Admitted,
        };
        var first = BaselineContentHash.ComputeProvenanceFingerprint([evidence]);
        Assert.Matches("^[0-9a-f]{64}$", first);
        var changed = BaselineContentHash.ComputeProvenanceFingerprint([evidence with { PolicyDigest = "policy-2" }]);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(
            "deadbeef",
            BaselineContentHash.CombineToolchainHash("deadbeef", first));
    }

    // ── Live cosign integration (explicitly opt-in) ────────────────────

    [Fact]
    public void CosignFixtures_AreWellFormedAndDigestPinned()
    {
        // Hermetic: no cosign binary needed. Pins the committed fixtures so
        // any corruption or replacement is caught even where the live
        // cryptographic leg cannot run.
        var fixtures = FixtureDir();
        var blob = Path.Combine(fixtures, "tool-fixture.sh");
        var bundle = Path.Combine(fixtures, "tool-fixture.sh.sigstore.json");
        var statementPath = Path.Combine(fixtures, "tool-fixture.sh.provenance.json");
        var pubPath = Path.Combine(fixtures, "tool-fixture.cosign.pub");
        foreach (var file in new[] { blob, bundle, statementPath, pubPath })
            Assert.True(File.Exists(file), $"Committed cosign fixture is missing: {file}");

        var digest = Sha256File(blob);
        var statement = ProvenanceStatement.TryParse(File.ReadAllBytes(statementPath));
        Assert.NotNull(statement);
        Assert.Equal(digest, statement!.Digest);
        Assert.Equal("https://codeybox.invalid/provenance-test-fixture", statement.Repository);
        Assert.Equal("provenance-fixture.yml", statement.Workflow);
        Assert.Equal("refs/tags/provenance-fixture-v1", statement.SourceRef);

        var pubPem = File.ReadAllText(pubPath);
        Assert.Contains("BEGIN PUBLIC KEY", pubPem, StringComparison.Ordinal);
        Assert.NotEmpty(ArtifactTrustOptions.FingerprintPublicKey(pubPem));
        Assert.True(new FileInfo(bundle).Length > 512, "Bundle fixture looks truncated.");
    }

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodeyBox.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var fixtures = dir is null
            ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtifactProvenance")
            : Path.Combine(dir, "tests", "CodeyBox.Tests", "Fixtures", "ArtifactProvenance");
        return fixtures;
    }

    private static string? ProbeCosign()
    {
        foreach (var candidate in new[] { "/tmp/cosign", "cosign" })
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                process.Start();
                if (process.WaitForExit(15_000) && process.ExitCode == 0)
                    return candidate;
            }
            catch (Exception)
            {
                // Candidate unusable; try the next.
            }
        }
        return null;
    }

    [Fact]
    [Trait("requires_cosign", "true")]
    public void Live_CosignLocalKey_FixtureVerifiesThroughRealCosign()
    {
        var cosign = ProbeCosign();
        if (cosign is null)
            // requires_cosign: needs the cosign binary (CI installs it; see
            // .github/workflows/ci.yml). Without it the cryptographic leg cannot
            // run here — the hermetic fixture test below still pins fixture
            // integrity, and this leg returns without claiming verification.
            return;
        var fixtures = FixtureDir();
        var blobName = "tool-fixture.sh";
        Assert.True(File.Exists(Path.Combine(fixtures, blobName)), "Committed cosign fixtures are missing.");
        Assert.True(File.Exists(Path.Combine(fixtures, blobName + ".sigstore.json")), "Committed cosign bundle is missing.");
        Assert.True(File.Exists(Path.Combine(fixtures, blobName + ".provenance.json")), "Committed provenance statement is missing.");
        Assert.True(File.Exists(Path.Combine(fixtures, "tool-fixture.cosign.pub")), "Committed cosign public key is missing.");

        // Copy fixtures to a scratch dir: admission must verify staged
        // copies, never the committed originals.
        var scratch = Path.Combine(_tempRoot, "live");
        Directory.CreateDirectory(scratch);
        foreach (var file in Directory.GetFiles(fixtures))
            File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
        var source = Path.Combine(scratch, blobName);

        var pubPem = File.ReadAllText(Path.Combine(scratch, "tool-fixture.cosign.pub"));
        var digest = Sha256File(source);
        // cosign verify-blob attests only the pinned key (plus the subject
        // digest): repository/workflow/source-ref constraints have no
        // verifier-backed source in this contract, so the entry pins only
        // the key identity. The fixture sidecar's repository labels are
        // descriptive only.
        var entry = new TrustedArtifactPolicy
        {
            ArtifactId = "cosign-fixture",
            Sha256 = digest,
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(pubPem),
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pubPem,
        };
        var options = PolicyWith(entry);
        Assert.Empty(options.Validate());
        options.CosignBinaryPath = cosign;

        using var admission = new ArtifactAdmissionService();
        var admitted = admission.AdmitToolExecutable(source, options, CancellationToken.None);

        Assert.Equal("sha256:" + digest, admitted.Evidence.Digest);
        Assert.Equal("cosign-local-key", admitted.Evidence.Verifier);
        Assert.StartsWith("key:", admitted.Evidence.Identity, StringComparison.Ordinal);
        Assert.Equal(ArtifactEvidenceKinds.LocalKeyIssuer, admitted.Evidence.Issuer);
        Assert.False(string.IsNullOrWhiteSpace(admitted.Evidence.VerifierVersion));
    }

    [Fact]
    [Trait("requires_cosign", "true")]
    public void Live_CosignLocalKey_ValidSignatureWrongPublisher_IsBlocked()
    {
        var cosign = ProbeCosign();
        if (cosign is null)
            // requires_cosign: needs the cosign binary (CI installs it; see
            // .github/workflows/ci.yml). Without it the cryptographic leg cannot
            // run here — the hermetic fixture test below still pins fixture
            // integrity, and this leg returns without claiming verification.
            return;
        var fixtures = FixtureDir();
        var scratch = Path.Combine(_tempRoot, "live-wrong");
        Directory.CreateDirectory(scratch);
        foreach (var file in Directory.GetFiles(fixtures))
            File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
        var source = Path.Combine(scratch, "tool-fixture.sh");

        // The signature is genuinely valid, but the policy pins a different
        // key: admission must refuse. A valid signature from an arbitrary
        // identity is insufficient.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPub = key.ExportSubjectPublicKeyInfoPem();
        var entry = new TrustedArtifactPolicy
        {
            ArtifactId = "cosign-fixture",
            Sha256 = Sha256File(source),
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(otherPub),
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = otherPub,
        };
        var options = PolicyWith(entry);
        Assert.Empty(options.Validate());
        options.CosignBinaryPath = cosign;

        using var admission = new ArtifactAdmissionService();
        // cosign rejects the foreign key outright: cryptographically invalid.
        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.CryptographicallyInvalid, ex.Outcome);
    }

    [Fact]
    [Trait("requires_cosign", "true")]
    public void Live_CosignLocalKey_TamperedBytes_AreBlocked()
    {
        var cosign = ProbeCosign();
        if (cosign is null)
            // requires_cosign: needs the cosign binary (CI installs it; see
            // .github/workflows/ci.yml). Without it the cryptographic leg cannot
            // run here — the hermetic fixture test below still pins fixture
            // integrity, and this leg returns without claiming verification.
            return;
        var fixtures = FixtureDir();
        var scratch = Path.Combine(_tempRoot, "live-tamper");
        Directory.CreateDirectory(scratch);
        foreach (var file in Directory.GetFiles(fixtures))
            File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
        var source = Path.Combine(scratch, "tool-fixture.sh");
        File.AppendAllText(source, "# tampered\n");

        var pubPem = File.ReadAllText(Path.Combine(scratch, "tool-fixture.cosign.pub"));
        var entry = new TrustedArtifactPolicy
        {
            ArtifactId = "cosign-fixture",
            Sha256 = Sha256File(source),
            Publisher = "key:" + ArtifactTrustOptions.FingerprintPublicKey(pubPem),
            Issuer = ArtifactEvidenceKinds.LocalKeyIssuer,
            Evidence = ArtifactEvidenceKinds.CosignLocalKey,
            PublicKeyPem = pubPem,
        };
        var options = PolicyWith(entry);
        options.CosignBinaryPath = cosign;

        using var admission = new ArtifactAdmissionService();
        // The tampered digest matches no policy entry — except this test
        // pins the tampered digest, so verification itself must refuse on
        // the signature mismatch over the real cosign contract.
        var ex = Assert.Throws<ArtifactBlockedException>(
            () => admission.AdmitToolExecutable(source, options, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.CryptographicallyInvalid, ex.Outcome);
    }
}
