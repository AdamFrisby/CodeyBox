using System.Security.Cryptography;
using CodeyBox.Core;
using CodeyBox.OpenStackSandboxPlugin;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.ArtifactProvenance;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification is enforced at the executable trust boundaries: Incus
/// admits before fingerprinting and the workspace consumes only admitted
/// bytes; Multipass binds provenance into the baseline name and transfers
/// only confirmed staged copies; OpenStack admits before planning and stages
/// admitted bytes. Disabled policy preserves existing behavior.
/// </summary>
public sealed class ExecutableProvisionBoundaryTests : IDisposable
{
    private readonly List<ArtifactAdmissionService> _admissions = [];
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "codeybox-prov-boundary-" + Guid.NewGuid().ToString("N"));

    public ExecutableProvisionBoundaryTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        foreach (var admission in _admissions)
            admission.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class AllowFakeVerifier : IArtifactVerifier
    {
        public string Name => "fake-test-verifier";
        public string EvidenceKind => ArtifactEvidenceKinds.OpensslLocal;
        public VerifierVerdict Verify(
            StagedArtifact staged,
            TrustedArtifactPolicy policy,
            ArtifactTrustOptions options,
            CancellationToken ct) => new()
            {
                CryptographicallyValid = true,
                ObservedPublisher = policy.Publisher,
                ObservedIssuer = policy.Issuer,
                Verifier = "fake-test-verifier",
                VerifierVersion = "test-1",
            };
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private string WriteTool(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        File.WriteAllText(path + ".provenance.json", """{"digest":"00","publisher":"x","issuer":"y"}""");
        File.WriteAllText(path + ".provenance.sig", "dummy");
        return path;
    }

    private ArtifactAdmissionService FakeAdmission()
    {
        var admission = new ArtifactAdmissionService(verifierFactory: _ => new AllowFakeVerifier());
        _admissions.Add(admission);
        return admission;
    }

    private static ArtifactTrustOptions PolicyFor(string digestHex) => new()
    {
        Enabled = true,
        TrustedArtifacts =
        [
            new TrustedArtifactPolicy
            {
                ArtifactId = "tool",
                Sha256 = digestHex,
                Publisher = "test-publisher",
                Issuer = "test-issuer",
                Evidence = ArtifactEvidenceKinds.OpensslLocal,
            },
        ],
    };

    private static BaselineExecutableProvision ProvisionFor(string source) => new()
    {
        HostSourcePath = source,
        VmDestPath = "/usr/local/bin/fixture-tool",
        VmSymlinks = [],
        Label = "fixture",
    };

    // ── Incus ──────────────────────────────────────────────────────────

    [Fact]
    public void Incus_AdmitAndFingerprint_BindsEvidenceDigests()
    {
        var source = WriteTool("agy", "tool-bytes");
        var options = new IncusSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var trust = PolicyFor(Sha256File(source));

        var inputs = IncusBaselineProvisioning.AdmitAndFingerprintExecutables(
            options, _ => null, FakeAdmission(), trust, CancellationToken.None);

        Assert.Equal(["sha256:" + Sha256File(source)], inputs.Fingerprints);
        var evidence = Assert.Single(inputs.Evidences);
        Assert.Equal("sha256:" + Sha256File(source), evidence.Digest);
        Assert.Equal("test-publisher", evidence.Identity);
        Assert.False(string.IsNullOrWhiteSpace(evidence.PolicyDigest));
        Assert.True(File.Exists(inputs.Staged[0].StagedPath));
        Assert.Equal("tool-bytes", File.ReadAllText(inputs.Staged[0].StagedPath));
    }

    [Fact]
    public void Incus_AdmitAndFingerprint_TamperedSource_IsBlocked()
    {
        var source = WriteTool("agy", "tool-bytes");
        var options = new IncusSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };

        var ex = Assert.Throws<ArtifactBlockedException>(() =>
            IncusBaselineProvisioning.AdmitAndFingerprintExecutables(
                options, _ => null, FakeAdmission(), PolicyFor(new string('0', 64)), CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.MissingEvidence, ex.Outcome);
    }

    [Fact]
    public void Incus_Workspace_ConsumesOnlyAdmittedBytes()
    {
        var source = WriteTool("agy", "tool-bytes");
        var digest = Sha256File(source);
        var options = new IncusSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var trust = PolicyFor(digest);
        var admission = FakeAdmission();
        var inputs = IncusBaselineProvisioning.AdmitAndFingerprintExecutables(
            options, _ => null, admission, trust, CancellationToken.None);

        var stagingRoot = Path.Combine(_tempRoot, "incus-stage");
        Directory.CreateDirectory(stagingRoot);
        using var workspace = IncusProvisioningWorkspace.Create(
            options, stagingRoot, _ => null, Guid.NewGuid, CancellationToken.None,
            inputs.Staged, trust);

        var staged = Assert.Single(workspace.Executables);
        Assert.Equal("sha256:" + digest, staged.ContentSha256);
        Assert.Equal("tool-bytes", File.ReadAllText(staged.StagedPath));
    }

    [Fact]
    public void Incus_Workspace_SubstitutedStagedCopy_IsBlocked()
    {
        var source = WriteTool("agy", "tool-bytes");
        var options = new IncusSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var trust = PolicyFor(Sha256File(source));
        var admission = FakeAdmission();
        var inputs = IncusBaselineProvisioning.AdmitAndFingerprintExecutables(
            options, _ => null, admission, trust, CancellationToken.None);
        File.WriteAllText(inputs.Staged[0].StagedPath, "substituted");

        var stagingRoot = Path.Combine(_tempRoot, "incus-stage-sub");
        Directory.CreateDirectory(stagingRoot);
        var ex = Assert.Throws<ArtifactBlockedException>(() => IncusProvisioningWorkspace.Create(
            options, stagingRoot, _ => null, Guid.NewGuid, CancellationToken.None,
            inputs.Staged, trust));
        Assert.Equal(ProvenanceOutcome.CryptographicallyInvalid, ex.Outcome);
    }

    [Fact]
    public void Incus_Workspace_WithoutAdmissionUnderEnforcement_IsBlocked()
    {
        var source = WriteTool("agy", "tool-bytes");
        var options = new IncusSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var trust = PolicyFor(Sha256File(source));

        var stagingRoot = Path.Combine(_tempRoot, "incus-stage-noadmit");
        Directory.CreateDirectory(stagingRoot);
        var ex = Assert.Throws<ArtifactBlockedException>(() => IncusProvisioningWorkspace.Create(
            options, stagingRoot, _ => null, Guid.NewGuid, CancellationToken.None,
            admittedExecutables: null, trust));
        Assert.Equal(ProvenanceOutcome.VerifierUnavailable, ex.Outcome);
    }

    [Fact]
    public void Incus_Naming_ProvenanceChangesHash_EmptyPreservesLegacy()
    {
        var fingerprints = new[] { "sha256:" + new string('a', 64) };
        var options = new IncusSandboxOptions
        {
            ExecutableProvisions = [ProvisionFor("/irrelevant/a")],
        };
        var legacy = IncusBaselineNaming.ComputeSharedToolchainHash(options, fingerprints);
        Assert.Equal(legacy, IncusBaselineNaming.ComputeSharedToolchainHash(options, fingerprints, []));
        Assert.Equal(legacy, IncusBaselineNaming.ComputeSharedToolchainHash(options, fingerprints, null));

        var evidence = new ArtifactProvenanceEvidence
        {
            ArtifactId = "tool",
            Digest = fingerprints[0],
            Verifier = "fake",
            Identity = "pub",
            Issuer = "iss",
            VerifiedAtUtc = DateTimeOffset.UtcNow,
            PolicyDigest = "policy-1",
        };
        Assert.NotEqual(
            legacy,
            IncusBaselineNaming.ComputeSharedToolchainHash(options, fingerprints, [evidence]));
    }

    // ── Multipass ──────────────────────────────────────────────────────

    [Fact]
    public void Multipass_HashCompat_DisabledPolicyIsUnchanged()
    {
        var source = WriteTool("agy", "tool-bytes");
        var options = new MultipassSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var baseline = MultipassSandboxProvider.ComputeBaselineHash(options, "default", SandboxProfileFlavor.Headless);
        Assert.Equal(
            baseline,
            MultipassSandboxProvider.ComputeBaselineHash(options, "default", SandboxProfileFlavor.Headless, null, null));
        Assert.Equal(12, baseline.Length);

        var other = MultipassSandboxProvider.ComputeBaselineHash(
            options, "default", SandboxProfileFlavor.Headless, null, "prov:abc123");
        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Multipass_ResolveRef_BindsProvenanceAndRejectsTampering()
    {
        var source = WriteTool("agy", "tool-bytes");
        var digest = Sha256File(source);
        MultipassSandboxOptions Options() => new()
        {
            ExecutableProvisions = [ProvisionFor(source)],
            NetworkProfiles = new Dictionary<string, string> { ["default"] = "br-test" },
            UseBaselineImages = true,
        };
        var trust = PolicyFor(digest);

        var enforced = new MultipassSandboxProvider(
            Options(), NullLogger<MultipassSandboxProvider>.Instance,
            trustAccessor: () => trust, admission: FakeAdmission());
        var legacy = new MultipassSandboxProvider(
            Options(), NullLogger<MultipassSandboxProvider>.Instance);

        var bound = enforced.ResolveBaselineRef("default", SandboxProfileFlavor.Headless);
        var unbound = legacy.ResolveBaselineRef("default", SandboxProfileFlavor.Headless);
        Assert.NotNull(bound);
        Assert.NotEqual(unbound, bound);

        File.WriteAllText(source, "tampered");
        Assert.Throws<ArtifactBlockedException>(() =>
            enforced.ResolveBaselineRef("default", SandboxProfileFlavor.Headless));
    }

    [Fact]
    public void Multipass_TransferSource_ConfirmsStagedBytes()
    {
        var source = WriteTool("agy", "tool-bytes");
        var trust = PolicyFor(Sha256File(source));
        var admission = FakeAdmission();
        var admitted = admission.AdmitToolExecutable(source, trust, CancellationToken.None);

        var transfer = MultipassSandboxProvider.VerifyAdmittedTransferSource(
            admitted, trust.MaxArtifactBytes, CancellationToken.None);
        Assert.Equal(admitted.StagedPath, transfer);

        File.WriteAllText(admitted.StagedPath, "substituted");
        var ex = Assert.Throws<ArtifactBlockedException>(() =>
            MultipassSandboxProvider.VerifyAdmittedTransferSource(
                admitted, trust.MaxArtifactBytes, CancellationToken.None));
        Assert.Equal(ProvenanceOutcome.CryptographicallyInvalid, ex.Outcome);
    }

    // ── OpenStack ──────────────────────────────────────────────────────

    [Fact]
    public void OpenStack_Plan_BindsProvenanceAndRejectsTampering()
    {
        var source = WriteTool("agy", "tool-bytes");
        var digest = Sha256File(source);
        var options = new OpenStackSandboxOptions { ExecutableProvisions = [ProvisionFor(source)] };
        var trust = PolicyFor(digest);
        var builder = new OpenStackBaselineBuilder(
            options,
            new OpenStackCredentials(new Uri("http://localhost/"), "id", "secret", "region", "public", true),
            new OpenStackApiClient(new HttpClient()),
            new FakeTransportsKeys(), new FakeTransportsFactory(),
            _ => null, TimeProvider.System, NullLogger.Instance,
            trust, FakeAdmission());

        var plan = builder.Plan();

        Assert.Equal(["sha256:" + digest], plan.Fingerprints);
        Assert.False(string.IsNullOrWhiteSpace(plan.ProvenanceFingerprint));
        Assert.Single(plan.Admitted);

        File.WriteAllText(source, "tampered");
        Assert.Throws<ArtifactBlockedException>(() => builder.Plan());
    }

    [Fact]
    public void OpenStack_Plan_DisabledPolicyPreservesLegacyHash()
    {
        // Disabled policy: the legacy content hash applies and no admission
        // runs, so exemplary sidecars are unnecessary — only a real file.
        var source = Path.Combine(_tempRoot, "legacy-tool");
        File.WriteAllText(source, "legacy-bytes");
        var options = new OpenStackSandboxOptions
        {
            ExecutableProvisions =
            [
                new BaselineExecutableProvision { HostSourcePath = source, VmDestPath = "/usr/local/bin/a", Label = "a" },
            ],
        };
        var builder = new OpenStackBaselineBuilder(
            options,
            new OpenStackCredentials(new Uri("http://localhost/"), "id", "secret", "region", "public", true),
            new OpenStackApiClient(new HttpClient()),
            new FakeTransportsKeys(), new FakeTransportsFactory(),
            _ => null, TimeProvider.System, NullLogger.Instance);
        var plan = builder.Plan();
        Assert.Empty(plan.ProvenanceFingerprint);
        Assert.Empty(plan.Admitted);
    }

    private sealed class FakeTransportsKeys : IOpenStackKeyGenerator
    {
        public Task<OpenStackClientKeyMaterial> GenerateClientKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct) =>
            throw new NotSupportedException("No key generation in plan tests.");

        public Task<OpenStackHostKeyMaterial> GenerateHostKeyAsync(
            string keygenBinary, string directory, string comment, CancellationToken ct) =>
            throw new NotSupportedException("No key generation in plan tests.");
    }

    private sealed class FakeTransportsFactory : IOpenStackTransportFactory
    {
        public IRemoteHostTransport Create(OpenStackSshTransportSpec spec) =>
            throw new NotSupportedException("No transport in plan tests.");
    }
}
