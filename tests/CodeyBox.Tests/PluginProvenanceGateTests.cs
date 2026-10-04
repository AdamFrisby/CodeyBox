using System.Security.Cryptography;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.ArtifactProvenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// The provenance gate sits before plugin load: under enforcement an
/// untrusted bundle is refused and its code never executes; an admitted
/// bundle reaches load; with the policy disabled behavior is unchanged.
/// </summary>
public sealed class PluginProvenanceGateTests : IDisposable
{
    private readonly List<ArtifactAdmissionService> _admissions = [];
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "codeybox-prov-loader-" + Guid.NewGuid().ToString("N"));

    public PluginProvenanceGateTests() => Directory.CreateDirectory(_tempRoot);

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

    private static PluginLoader MakeLoader(
        PluginOptions opts,
        ArtifactTrustOptions? trust,
        ArtifactAdmissionService? admission) =>
        new(opts, new ConfigurationBuilder().Build(), NullLogger<PluginLoader>.Instance,
            trustOptions: trust, admission: admission);

    private static ArtifactTrustOptions EnforcedPolicy(string digestHex) => new()
    {
        Enabled = true,
        TrustedArtifacts =
        [
            new TrustedArtifactPolicy
            {
                ArtifactId = "sample-plugin",
                Sha256 = digestHex,
                Publisher = "test-publisher",
                Issuer = "test-issuer",
                Evidence = ArtifactEvidenceKinds.OpensslLocal,
            },
        ],
    };

    [Fact]
    public void Enforced_UnpinnedBundle_IsBlockedBeforeLoad()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var trust = new ArtifactTrustOptions { Enabled = true };
        using var admission = new ArtifactAdmissionService();
        var loader = MakeLoader(
            new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["*"], Enabled = ["*"] },
            trust, admission);

        var plugins = loader.DiscoverPlugins();
        var statuses = loader.GetDiscoveryStatuses();

        // The blocked assembly contributes no executable plugin: nothing was
        // loaded, and the refusal is visible in discovery statuses (one per
        // candidate plugin in the refused bundle).
        Assert.Empty(plugins);
        var blocked = statuses.Where(s => s.SkipReason == PluginSkipReason.ProvenanceBlocked).ToList();
        Assert.Equal(2, blocked.Count);
        Assert.All(blocked, b => Assert.False(b.Loaded));
        var reports = loader.GetAssemblyReports();
        Assert.Contains(reports, r => !r.Loaded && r.SkipReason == PluginSkipReason.ProvenanceBlocked);
    }

    [Fact]
    public void Enforced_PinnedBundleWithoutEvidence_IsBlocked()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        // Pinned digest but no provenance sidecars next to the source: the
        // gate refuses with MissingEvidence before any verification attempt.
        var trust = EnforcedPolicy(Sha256File(samplePath));
        using var admission = new ArtifactAdmissionService();
        var loader = MakeLoader(
            new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["*"], Enabled = ["*"] },
            trust, admission);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
        Assert.Contains(
            loader.GetDiscoveryStatuses(),
            s => s.SkipReason == PluginSkipReason.ProvenanceBlocked);
    }

    [Fact]
    public void Enforced_AdmittedBundle_ReachesLoad()
    {
        // Whole-directory bundle copy so the sample plugin keeps its
        // dependencies; the bundle digest (primary + co-located
        // dependencies) joins the trust boundary.
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var sampleDir = Path.GetDirectoryName(samplePath)!;
        var bundleDir = Path.Combine(_tempRoot, "bundle");
        Directory.CreateDirectory(bundleDir);
        foreach (var file in Directory.GetFiles(sampleDir))
            File.Copy(file, Path.Combine(bundleDir, Path.GetFileName(file)));
        var staged = Path.Combine(bundleDir, Path.GetFileName(samplePath));
        File.WriteAllText(staged + ".provenance.json", """{"digest":"00","publisher":"x","issuer":"y"}""");
        File.WriteAllText(staged + ".provenance.sig", "dummy");

        var admission = new ArtifactAdmissionService(verifierFactory: _ => new AllowFakeVerifier());
        _admissions.Add(admission);
        var primaryDigest = Sha256File(staged);

        // Probe the production bundle digest from the refusal evidence, then
        // pin exactly it: the admitted load covers the whole bundle.
        // (Single-file bundles carry no bundle digest; the entry then pins
        // the primary alone.)
        string? bundleDigest = null;
        try
        {
            admission.AdmitPluginBundle(staged, EnforcedPolicy(primaryDigest), CancellationToken.None);
        }
        catch (ArtifactBlockedException ex)
        {
            bundleDigest = ex.Evidence?.BundleDigest?.Replace("sha256:", string.Empty, StringComparison.Ordinal);
        }

        var trust = EnforcedPolicy(primaryDigest);
        trust.TrustedArtifacts[0].BundleSha256 = bundleDigest;
        var loader = MakeLoader(
            new PluginOptions { AssemblyPaths = [staged], Allowlist = ["*"], Enabled = ["*"] },
            trust, admission);

        var plugins = loader.DiscoverPlugins();

        Assert.NotEmpty(plugins);
        Assert.DoesNotContain(
            loader.GetDiscoveryStatuses(),
            s => s.SkipReason == PluginSkipReason.ProvenanceBlocked);
    }

    [Fact]
    public void Enforced_TamperedDependency_CannotBypassBundleBoundary()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var sampleDir = Path.GetDirectoryName(samplePath)!;
        var bundleDir = Path.Combine(_tempRoot, "bundle-tamper");
        Directory.CreateDirectory(bundleDir);
        foreach (var file in Directory.GetFiles(sampleDir))
            File.Copy(file, Path.Combine(bundleDir, Path.GetFileName(file)));
        var staged = Path.Combine(bundleDir, Path.GetFileName(samplePath));
        File.WriteAllText(staged + ".provenance.json", """{"digest":"00","publisher":"x","issuer":"y"}""");
        File.WriteAllText(staged + ".provenance.sig", "dummy");

        var admission = new ArtifactAdmissionService(verifierFactory: _ => new AllowFakeVerifier());
        _admissions.Add(admission);
        string? bundleDigest = null;
        try
        {
            admission.AdmitPluginBundle(staged, EnforcedPolicy(Sha256File(staged)), CancellationToken.None);
        }
        catch (ArtifactBlockedException ex)
        {
            bundleDigest = ex.Evidence?.BundleDigest?.Replace("sha256:", string.Empty, StringComparison.Ordinal);
        }
        var trust = EnforcedPolicy(Sha256File(staged));
        trust.TrustedArtifacts[0].BundleSha256 = bundleDigest;

        // Tamper with the co-located dependencies: modifying one (or
        // smuggling in a new one) changes the bundle digest, so the pinned
        // policy no longer matches and the whole bundle is refused.
        var dependency = Directory.GetFiles(bundleDir, "*.dll")
            .FirstOrDefault(f => !string.Equals(f, staged, StringComparison.Ordinal));
        if (dependency is null)
        {
            dependency = Path.Combine(bundleDir, "smuggled.dll");
            File.WriteAllText(dependency, "smuggled");
        }
        else
        {
            File.AppendAllText(dependency, "\ntampered");
        }
        var loader = MakeLoader(
            new PluginOptions { AssemblyPaths = [staged], Allowlist = ["*"], Enabled = ["*"] },
            trust, admission);
        Assert.Empty(loader.DiscoverPlugins());
        Assert.Contains(
            loader.GetDiscoveryStatuses(),
            s => s.SkipReason == PluginSkipReason.ProvenanceBlocked);
    }

    [Fact]
    public void DisabledPolicy_PreservesExistingBehavior()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(
            new PluginOptions
            {
                AssemblyPaths = [samplePath],
                Allowlist = ["sample.auditor"],
                Enabled = ["sample.auditor"],
            },
            trust: null,
            admission: null);

        var plugins = loader.DiscoverPlugins();

        Assert.Single(plugins);
        Assert.Equal("sample.auditor", plugins[0].PluginId);
    }

    [Fact]
    public void LiveAccessor_EnforcementTracksCurrentPolicy()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var live = new ArtifactTrustOptions { Enabled = false };
        using var admission = new ArtifactAdmissionService();
        var loader = new PluginLoader(
            new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["*"], Enabled = ["*"] },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance,
            trustOptions: null,
            trustAccessor: () => live,
            admission: admission);

        // Disabled snapshot: loads as before.
        Assert.NotEmpty(loader.DiscoverPlugins());

        // Enabling the policy takes effect for subsequent loads without a
        // restart: the unpinned bundle is now refused before any load.
        live.Enabled = true;
        var reloader = new PluginLoader(
            new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["*"], Enabled = ["*"] },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance,
            trustOptions: null,
            trustAccessor: () => live,
            admission: admission);
        Assert.Empty(reloader.DiscoverPlugins());
        Assert.Contains(
            reloader.GetDiscoveryStatuses(),
            s => s.SkipReason == PluginSkipReason.ProvenanceBlocked);
    }
}
