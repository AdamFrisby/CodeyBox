using System.Text;
using CodeyBox.Core;
using CodeyBox.CycloneDxSbomAuditorPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the producer-neutral CycloneDX SBOM path (CBX-NEXT-043):
/// cross-ecosystem fixtures (.NET/NuGet via cdxgen, Node/npm via
/// cyclonedx-npm, JVM/native via syft) exercised through the SAME Core
/// import/validate/diff path; schema, identity, digest, graph, baseline,
/// policy, redaction, timeout, generator-selection, and evidence-retention
/// behavior. Synthetic fixtures and fake process transports around real
/// production code only — no live vendor calls, no dependency installation.
/// </summary>
public sealed class CycloneDxSbomTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly SbomCycloneDxOptions Options = new()
    {
        Enabled = true,
        MaxSbomBytes = 1024 * 1024,
        MaxComponents = 1000,
        MaxDependencies = 5000,
    };

    private const string NuGetSbom = """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "serialNumber": "urn:uuid:11111111-1111-1111-1111-111111111111",
          "metadata": {
            "tools": {
              "components": [{ "type": "application", "name": "cdxgen", "version": "11.0.0" }]
            }
          },
          "components": [
            {
              "bom-ref": "pkg:nuget/Newtonsoft.Json@13.0.3",
              "type": "library",
              "group": "",
              "name": "Newtonsoft.Json",
              "version": "13.0.3",
              "purl": "pkg:nuget/Newtonsoft.Json@13.0.3",
              "hashes": [{ "alg": "SHA-256", "content": "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855" }],
              "licenses": [{ "license": { "id": "MIT" } }]
            },
            {
              "bom-ref": "pkg:nuget/Microsoft.Extensions.Logging@8.0.0",
              "type": "library",
              "name": "Microsoft.Extensions.Logging",
              "version": "8.0.0",
              "purl": "pkg:nuget/Microsoft.Extensions.Logging@8.0.0",
              "hashes": [{ "alg": "SHA-512", "content": "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855" }]
            }
          ],
          "dependencies": [
            { "ref": "pkg:nuget/Newtonsoft.Json@13.0.3", "dependsOn": [] },
            { "ref": "pkg:nuget/Microsoft.Extensions.Logging@8.0.0", "dependsOn": ["pkg:nuget/Newtonsoft.Json@13.0.3"] }
          ]
        }
        """;

    private const string NpmSbom = """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.5",
          "serialNumber": "urn:uuid:22222222-2222-2222-2222-222222222222",
          "metadata": {
            "tools": [{ "name": "cyclonedx-npm", "version": "1.19.0" }],
            "component": { "bom-ref": "my-app@1.0.0", "type": "application", "name": "my-app", "version": "1.0.0" }
          },
          "components": [
            {
              "bom-ref": "lodash@4.17.21",
              "type": "library",
              "name": "lodash",
              "version": "4.17.21",
              "purl": "pkg:npm/lodash@4.17.21",
              "licenses": [{ "expression": "MIT" }]
            },
            {
              "bom-ref": "debug@4.3.4",
              "type": "library",
              "name": "debug",
              "version": "4.3.4",
              "scope": "required",
              "purl": "pkg:npm/debug@4.3.4?repository_url=https%3A%2F%2Fregistry.npmjs.org",
              "hashes": [{ "alg": "SHA-1", "content": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4" }]
            }
          ],
          "dependencies": [
            { "ref": "my-app@1.0.0", "dependsOn": ["lodash@4.17.21", "debug@4.3.4"] },
            { "ref": "lodash@4.17.21", "dependsOn": [] },
            { "ref": "debug@4.3.4", "dependsOn": [] }
          ]
        }
        """;

    private const string JvmNativeSbom = """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.4",
          "metadata": {
            "tools": [{ "name": "syft", "version": "1.0.0" }]
          },
          "components": [
            {
              "bom-ref": "commons-lang3",
              "type": "library",
              "group": "org.apache.commons",
              "name": "commons-lang3",
              "version": "3.14.0",
              "purl": "pkg:maven/org.apache.commons/commons-lang3@3.14.0"
            },
            {
              "bom-ref": "openssl-deb",
              "type": "library",
              "name": "openssl",
              "version": "3.0.11",
              "purl": "pkg:deb/debian/openssl@3.0.11?arch=amd64&distro=debian-12"
            }
          ],
          "dependencies": [
            { "ref": "commons-lang3", "dependsOn": [] },
            { "ref": "openssl-deb", "dependsOn": [] }
          ]
        }
        """;

    [Fact]
    public void ValidImport_AllEcosystems_SameNeutralPath()
    {
        var nuget = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NuGetSbom), "json", Options);
        var npm = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "json", Options);
        var jvm = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(JvmNativeSbom), "json", Options);

        Assert.True(nuget.Ok);
        Assert.True(npm.Ok);
        Assert.True(jvm.Ok);
        Assert.Equal("1.6", nuget.Document!.SpecVersion);
        Assert.Equal("1.5", npm.Document!.SpecVersion);
        Assert.Equal("1.4", jvm.Document!.SpecVersion);
        Assert.Equal("cdxgen", nuget.Document.Producer.Producer);
        Assert.Equal("11.0.0", nuget.Document.Producer.ToolVersion);
        Assert.Equal("cyclonedx-npm", npm.Document.Producer.Producer);
        Assert.Equal("syft", jvm.Document.Producer.Producer);
        Assert.Equal(2, nuget.Document.Components.Count);
        Assert.Equal(2, npm.Document.Components.Count);
        Assert.Equal(2, jvm.Document.Components.Count);
        Assert.All(nuget.Document.Components, c => Assert.NotNull(c.Identity.Purl));
        Assert.Contains(npm.Document.Components, c => c.Identity.Purl!.Contains("?repository_url=", StringComparison.Ordinal));
        Assert.Contains(jvm.Document.Components, c => c.Identity.Purl!.StartsWith("pkg:maven/", StringComparison.Ordinal));
        Assert.Contains(jvm.Document.Components, c => c.Identity.Purl!.StartsWith("pkg:deb/", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidImport_XmlFormat_Supported()
    {
        const string xml = """
            <bom xmlns="http://cyclonedx.org/schema/bom/1.5" specVersion="1.5">
              <components>
                <component type="library" bom-ref="lodash@4.17.21">
                  <name>lodash</name>
                  <version>4.17.21</version>
                  <purl>pkg:npm/lodash@4.17.21</purl>
                </component>
              </components>
            </bom>
            """;
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(xml), "xml", Options);
        Assert.True(result.Ok);
        Assert.Equal("xml", result.Document!.Format);
        Assert.Single(result.Document.Components);
    }

    [Fact]
    public void Import_MissingComponentsArray_RejectedAsPartialInventory()
    {
        const string noComponents = """{ "bomFormat": "CycloneDX", "specVersion": "1.5" }""";
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(noComponents), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.invalid-schema");
    }

    [Fact]
    public void Import_UnsupportedSpecVersion_Rejected()
    {
        var doc = NpmSbom.Replace("\"specVersion\": \"1.5\"", "\"specVersion\": \"9.9\"");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.unsupported-spec");
    }

    [Fact]
    public void Import_MissingBomFormat_Rejected()
    {
        const string noFormat = """{ "specVersion": "1.5", "components": [] }""";
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(noFormat), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.invalid-schema");
    }

    [Fact]
    public void Import_TruncatedJson_RejectedAsMalformed()
    {
        var truncated = NpmSbom[..(NpmSbom.Length / 2)];
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(truncated), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.malformed");
    }

    [Fact]
    public void Import_EmptyBytes_RejectedAsMissing()
    {
        var result = SbomCycloneDxImport.Import([], "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.missing");
    }

    [Fact]
    public void Import_OversizedBytes_RejectedBeforeParsing()
    {
        var tiny = new SbomCycloneDxOptions
        {
            Enabled = true,
            MaxSbomBytes = 1024,
            MaxComponents = 1000,
            MaxDependencies = 5000,
        };
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "json", tiny);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.oversized");
    }

    [Fact]
    public void Import_UnsupportedFormat_Rejected()
    {
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "yaml", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.unsupported-format");
    }

    [Fact]
    public void Import_DuplicateBomRef_Rejected()
    {
        var doc = NpmSbom.Replace("\"bom-ref\": \"debug@4.3.4\"", "\"bom-ref\": \"lodash@4.17.21\"");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.duplicate-identity");
    }

    [Fact]
    public void Import_DuplicatePurlIdentity_Rejected()
    {
        var doc = NpmSbom.Replace(
            "\"purl\": \"pkg:npm/debug@4.3.4?repository_url=https%3A%2F%2Fregistry.npmjs.org\"",
            "\"purl\": \"pkg:npm/lodash@4.17.21\"");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.duplicate-identity");
    }

    [Fact]
    public void Import_PurlQualifiersDistinguishIdentities()
    {
        var doc = NpmSbom.Replace(
            "pkg:npm/debug@4.3.4?repository_url=https%3A%2F%2Fregistry.npmjs.org",
            "pkg:npm/debug@4.3.4?repository_url=https%3A%2F%2Fexample.invalid");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Document!.Components.Count);
    }

    [Fact]
    public void Import_SameNameDifferentPurls_NeverCollapsed()
    {
        const string sameName = """
            {
              "bomFormat": "CycloneDX", "specVersion": "1.5",
              "components": [
                { "bom-ref": "a", "type": "library", "name": "lodash", "version": "4.17.20", "purl": "pkg:npm/lodash@4.17.20" },
                { "bom-ref": "b", "type": "library", "name": "lodash", "version": "4.17.21", "purl": "pkg:npm/lodash@4.17.21" }
              ]
            }
            """;
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(sameName), "json", Options);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Document!.Components.Count);
    }

    [Fact]
    public void Import_InvalidPurl_Rejected()
    {
        var doc = NpmSbom.Replace("pkg:npm/lodash@4.17.21", "not-a-purl");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.invalid-identity");
    }

    [Fact]
    public void Import_InvalidDigest_Rejected()
    {
        var doc = NpmSbom.Replace("e3b0c44298fc1c149afbf4c8996fb92427ae41e4", "zzzz");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.invalid-digest");
    }

    [Fact]
    public void Import_ComponentWithoutIdentifiers_RejectedAsPartialInventory()
    {
        const string anonymous = """
            {
              "bomFormat": "CycloneDX", "specVersion": "1.5",
              "components": [{ "type": "library", "name": "mystery" }]
            }
            """;
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(anonymous), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.invalid-identity");
    }

    [Fact]
    public void Import_UnknownDependencyRef_RejectedAsAmbiguous()
    {
        var doc = NpmSbom.Replace("\"ref\": \"debug@4.3.4\"", "\"ref\": \"ghost@0.0.0\"");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.ambiguous-relationship");
    }

    [Fact]
    public void Import_DuplicateDependencyRef_RejectedAsAmbiguous()
    {
        var doc = NpmSbom.Replace(
            "{ \"ref\": \"debug@4.3.4\", \"dependsOn\": [] }",
            "{ \"ref\": \"lodash@4.17.21\", \"dependsOn\": [] }");
        var result = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(doc), "json", Options);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "sbom.ambiguous-relationship");
    }

    [Fact]
    public void Diff_DefinesAdditionsRemovalsVersionAndRelationshipChanges()
    {
        var baseline = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "json", Options).Document!;
        var candidateJson = NpmSbom
            .Replace("pkg:npm/lodash@4.17.21", "pkg:npm/lodash@4.17.22")
            .Replace("\"version\": \"4.17.21\"", "\"version\": \"4.17.22\"")
            .Replace("\"bom-ref\": \"lodash@4.17.21\"", "\"bom-ref\": \"lodash@4.17.22\"")
            .Replace(
                "{ \"ref\": \"my-app@1.0.0\", \"dependsOn\": [\"lodash@4.17.21\", \"debug@4.3.4\"] }",
                "{ \"ref\": \"my-app@1.0.0\", \"dependsOn\": [\"lodash@4.17.22\", \"debug@4.3.4\"] }")
            .Replace(
                "{ \"ref\": \"lodash@4.17.21\", \"dependsOn\": [] }",
                "{ \"ref\": \"lodash@4.17.22\", \"dependsOn\": [] }")
            .Replace(
                "\"purl\": \"pkg:maven/org.apache.commons/commons-lang3@3.14.0\"",
                "\"purl\": \"pkg:maven/org.apache.commons/commons-lang3@3.14.0\"");
        candidateJson = candidateJson.Replace(
            """
                  "dependencies": [
            """,
            """
                  "dependencies": [
                    { "ref": "debug@4.3.4", "dependsOn": ["lodash@4.17.22"] },
            """);
        var candidate = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(candidateJson), "json", Options);
        Assert.True(candidate.Ok);

        var diff = SbomCycloneDxDiff.Compare(
            baseline,
            candidate.Document!,
            CancellationToken.None);

        Assert.Contains(diff.Changes, c => c.Kind == SbomChangeKind.VersionChanged && c.IdentityKey.Contains("lodash", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Changes, c => c.Kind == SbomChangeKind.Added && c.IdentityKey.Contains("lodash@4.17.22", StringComparison.Ordinal)
            && diff.Changes.Any(r => r.Kind == SbomChangeKind.Removed && r.IdentityKey.Contains("lodash@4.17.21", StringComparison.Ordinal)));
        Assert.Contains(diff.Changes, c => c.Kind == SbomChangeKind.RelationshipChanged);
        Assert.All(diff.Changes, c => Assert.StartsWith("sbom-", c.FindingId, StringComparison.Ordinal));
        Assert.All(diff.Changes, c => Assert.Contains(c.IdentityKey, c.Detail, StringComparison.Ordinal));

        var again = SbomCycloneDxDiff.Compare(baseline, candidate.Document!, CancellationToken.None);
        Assert.Equal(
            diff.Changes.Select(c => c.FindingId).OrderBy(x => x, StringComparer.Ordinal),
            again.Changes.Select(c => c.FindingId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Diff_AddedAndRemoved_AreExplicit()
    {
        var baseline = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NuGetSbom), "json", Options).Document!;
        const string candidateJson = """
            {
              "bomFormat": "CycloneDX", "specVersion": "1.6",
              "components": [
                { "bom-ref": "pkg:nuget/Newtonsoft.Json@13.0.3", "type": "library", "name": "Newtonsoft.Json", "version": "13.0.3", "purl": "pkg:nuget/Newtonsoft.Json@13.0.3" },
                { "bom-ref": "pkg:nuget/Dapper@2.1.1", "type": "library", "name": "Dapper", "version": "2.1.1", "purl": "pkg:nuget/Dapper@2.1.1" }
              ],
              "dependencies": [
                { "ref": "pkg:nuget/Newtonsoft.Json@13.0.3", "dependsOn": [] },
                { "ref": "pkg:nuget/Dapper@2.1.1", "dependsOn": [] }
              ]
            }
            """;
        var candidate = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(candidateJson), "json", Options).Document!;
        var diff = SbomCycloneDxDiff.Compare(baseline, candidate, CancellationToken.None);
        Assert.Contains(diff.Changes, c => c.Kind == SbomChangeKind.Added && c.IdentityKey.Contains("Dapper", StringComparison.Ordinal));
        Assert.Contains(diff.Changes, c => c.Kind == SbomChangeKind.Removed && c.IdentityKey.Contains("Microsoft.Extensions.Logging", StringComparison.Ordinal));
    }

    [Fact]
    public void Policy_FailOnAnyChange_BlocksRemovals()
    {
        var diff = new SbomDiff
        {
            Changes =
            [
                new SbomChange { Kind = SbomChangeKind.Removed, IdentityKey = "ref:x", Detail = "gone", FindingId = "sbom-1" },
            ],
        };
        var strict = SbomCycloneDxDiff.Evaluate(diff, SbomPolicyMode.FailOnAnyChange, true);
        Assert.False(strict.Passed);
        Assert.Contains(strict.Findings, f => f.Severity == AuditSeverity.Error);

        var lenient = SbomCycloneDxDiff.Evaluate(diff, SbomPolicyMode.FailOnAddedOrVersionChanged, true);
        Assert.True(lenient.Passed);
        Assert.Contains(lenient.Findings, f => f.Severity == AuditSeverity.Info);

        var advisory = SbomCycloneDxDiff.Evaluate(diff, SbomPolicyMode.AdvisoryOnly, true);
        Assert.True(advisory.Passed);
    }

    [Fact]
    public void Policy_IncompleteCoverage_NeverPasses()
    {
        var empty = new SbomDiff();
        var verdict = SbomCycloneDxDiff.Evaluate(empty, SbomPolicyMode.AdvisoryOnly, false);
        Assert.False(verdict.Passed);
        Assert.True(verdict.EvidenceInsufficient);
    }

    [Fact]
    public void Binding_Mismatch_IsReported()
    {
        var baseline = new SbomBaseline
        {
            ProjectId = "proj-a",
            ConfigDigest = "config-a",
            ContentDigest = EmptySha256,
            Document = SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "json", Options).Document!,
        };
        var candidate = new SbomCandidateBinding
        {
            ProjectId = "proj-b",
            ResolvedSha = "abc",
            ConfigDigest = "config-a",
            ContentDigest = EmptySha256,
        };
        Assert.NotNull(SbomCycloneDxDiff.VerifyBinding(baseline, candidate));
        Assert.NotNull(SbomCycloneDxDiff.VerifyBinding(
            baseline, candidate with { ProjectId = "proj-a", ConfigDigest = "config-b" }));
        var okBaseline = baseline with
        {
            ContentDigest = baseline.Document.ContentDigest,
        };
        Assert.Null(SbomCycloneDxDiff.VerifyBinding(
            okBaseline, candidate with { ProjectId = "proj-a", ConfigDigest = "config-a" }));
    }

    [Fact]
    public void Redaction_RemovesSecrets()
    {
        var redacted = SbomCycloneDxDiff.Redact("api_key=abc123\nkeep-this");
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("keep-this", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SbomCycloneDxImport.Import(Encoding.UTF8.GetBytes(NpmSbom), "json", Options, cts.Token));
    }

    [Fact]
    public async Task EvidenceWriter_RetainsBoundedArtifacts()
    {
        var store = new InMemoryAuditRunArtifactStore();
        var candidate = Encoding.UTF8.GetBytes(NpmSbom);
        var stored = await SbomEvidenceWriter.PutAsync(
            store, "run-1", candidate, """{"ok":true}""", """{"changes":[]}""", 1024 * 1024);
        Assert.Equal(3, stored.Count);
        Assert.All(stored, s => Assert.True(SbomCycloneDxDiff.ArtifactNames.IsKnown(s.Name)));
        var fetched = await store.GetAsync("run-1", SbomCycloneDxDiff.ArtifactNames.Candidate);
        Assert.NotNull(fetched);
        Assert.Equal(candidate, fetched!.Value.Content);

        await Assert.ThrowsAsync<AuditRunArtifactTooLargeException>(() =>
            SbomEvidenceWriter.PutAsync(store, "run-2", candidate, """{"ok":true}""", """{"changes":[]}""", 16));
    }

    [Fact]
    public async Task Auditor_DisabledByDefault_IsUnavailableNeverPass()
    {
        var auditor = new CycloneDxSbomAuditor();
        await auditor.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>()), CancellationToken.None);
        var sandbox = new FakeSandbox((_, _) => Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty)));
        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Auditor_AbsentBaseline_FailsNeverPasses()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string> { ["bom.json"] = NpmSbom });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error && f.Title.Contains("baseline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auditor_BaselineMismatch_FailsWithActionableFinding()
    {
        var baselineDigest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(NuGetSbom));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = baselineDigest,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>
        {
            ["bom.json"] = NpmSbom,
            ["approved.cdx.json"] = JvmNativeSbom,
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Title.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auditor_MatchingBaseline_PassesWithNonVulnerabilityNote()
    {
        var digest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(NpmSbom));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = digest,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>
        {
            ["bom.json"] = NpmSbom,
            ["approved.cdx.json"] = NpmSbom,
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Contains(result.Findings, f =>
            f.Description.Contains("vulnerability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auditor_VersionChange_FailsWithActionableDiff()
    {
        var baseline = NpmSbom;
        var candidate = NpmSbom
            .Replace("pkg:npm/lodash@4.17.21", "pkg:npm/lodash@4.17.22")
            .Replace("\"version\": \"4.17.21\"", "\"version\": \"4.17.22\"")
            .Replace("\"bom-ref\": \"lodash@4.17.21\"", "\"bom-ref\": \"lodash@4.17.22\"")
            .Replace("\"lodash@4.17.21\", \"debug@4.3.4\"", "\"lodash@4.17.22\", \"debug@4.3.4\"")
            .Replace("{ \"ref\": \"lodash@4.17.21\", \"dependsOn\": [] }", "{ \"ref\": \"lodash@4.17.22\", \"dependsOn\": [] }");
        var digest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(baseline));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = digest,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>
        {
            ["bom.json"] = candidate,
            ["approved.cdx.json"] = baseline,
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        var change = Assert.Single(result.Findings, f => f.Title.Contains("version", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("4.17.21", change.Description, StringComparison.Ordinal);
        Assert.Contains("4.17.22", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auditor_ProjectMismatch_Fails()
    {
        var digest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(NpmSbom));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = digest,
            ["Scoped:BaselineProjectId"] = "other-project",
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>
        {
            ["bom.json"] = NpmSbom,
            ["approved.cdx.json"] = NpmSbom,
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext("proj-1"), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Title.Contains("project", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auditor_MalformedCandidate_FailsNeverThrows()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = EmptySha256,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string> { ["bom.json"] = "{ truncated" });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
    }

    [Fact]
    public async Task Auditor_MissingInputWithoutGenerator_IsUnavailable()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>());
        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Auditor_UnknownGenerator_IsDeterministicUnavailable()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:Generator"] = "not-a-generator",
            ["Scoped:EcosystemTags"] = "npm",
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task Auditor_EscapingCandidatePath_Rejected()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "../etc/passwd",
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>());
        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task GeneratorSelection_Cdxgen_NeverInvokesDotnet()
    {
        var seen = new List<IReadOnlyList<string>>();
        const string generated = """
            {
              "bomFormat": "CycloneDX", "specVersion": "1.5",
              "metadata": { "tools": [{ "name": "cdxgen", "version": "11.0.0" }] },
              "components": [
                { "bom-ref": "left-pad@1.3.0", "type": "library", "name": "left-pad", "version": "1.3.0", "purl": "pkg:npm/left-pad@1.3.0" }
              ]
            }
            """;
        var digest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(generated));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = digest,
            ["Scoped:Generator"] = "cdxgen",
            ["Scoped:GeneratorExpectedVersion"] = "11.0.0",
            ["Scoped:EcosystemTags"] = "npm",
        });
        var sandbox = new FakeSandbox((exec, _) =>
        {
            seen.Add(exec.Argv);
            if (exec.Argv is ["cdxgen", "--version"])
                return Task.FromResult(new SandboxExecResult(0, "11.0.0\n", string.Empty));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cdxgen")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
            {
                var target = exec.Argv[^1];
                if (target.EndsWith("approved.cdx.json", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, generated, string.Empty));
                if (target.EndsWith(".codeybox-sbom-candidate.cdx.json", StringComparison.Ordinal))
                    return Task.FromResult(new SandboxExecResult(0, generated, string.Empty));
                return Task.FromResult(new SandboxExecResult(1, string.Empty, "missing"));
            }
            return Task.FromResult(new SandboxExecResult(1, string.Empty, "missing"));
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.NotEmpty(seen);
        Assert.All(seen, argv => Assert.DoesNotContain(argv, a =>
            string.Equals(a, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "restore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "msbuild", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(seen, argv => argv.Count > 0 && argv[0] == "cdxgen");
    }

    [Fact]
    public async Task GeneratorVersion_Mismatch_IsUnavailable()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:Generator"] = "cdxgen",
            ["Scoped:GeneratorExpectedVersion"] = "11.0.0",
            ["Scoped:EcosystemTags"] = "npm",
        });
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv is ["cdxgen", "--version"])
                return Task.FromResult(new SandboxExecResult(0, "9.9.9\n", string.Empty));
            return Task.FromResult(new SandboxExecResult(1, string.Empty, "missing"));
        });
        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task Auditor_CancelledToken_DoesNotPass()
    {
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = EmptySha256,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string> { ["bom.json"] = NpmSbom });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public async Task Auditor_RedactsSecretsFromReport()
    {
        var leaky = NpmSbom.Replace(
            "\"serialNumber\": \"urn:uuid:22222222-2222-2222-2222-222222222222\",",
            "\"serialNumber\": \"urn:uuid:22222222-2222-2222-2222-222222222222\",\n  \"comment\": \"token=hunter2\",");
        var digest = SbomCycloneDxImport.DigestBytes(Encoding.UTF8.GetBytes(leaky));
        var auditor = await BuildAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Enabled"] = "true",
            ["Scoped:CandidatePath"] = "bom.json",
            ["Scoped:BaselinePath"] = "approved.cdx.json",
            ["Scoped:BaselineDigest"] = digest,
        });
        var sandbox = FakeFileSandbox(new Dictionary<string, string>
        {
            ["bom.json"] = leaky,
            ["approved.cdx.json"] = leaky,
        });
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.DoesNotContain("hunter2", result.RawOutput ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<CycloneDxSbomAuditor> BuildAuditorAsync(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var auditor = new CycloneDxSbomAuditor();
        await auditor.InitializeAsync(BuildPluginContext(scopedValues), CancellationToken.None);
        return auditor;
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: CycloneDxSbomAuditor.PluginId,
            PluginDisplayName: "CodeyBox: CycloneDX SBOM Baseline Comparison",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static FakeSandbox FakeFileSandbox(IReadOnlyDictionary<string, string> files) =>
        new((exec, _) =>
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "cat")
            {
                var target = exec.Argv[^1].Trim();
                if (files.TryGetValue(target, out var content))
                    return Task.FromResult(new SandboxExecResult(0, content, string.Empty));
                return Task.FromResult(new SandboxExecResult(1, string.Empty, "No such file"));
            }
            return Task.FromResult(new SandboxExecResult(1, string.Empty, "missing"));
        });

    private static AuditContext FakeContext(string projectId = "proj-1") =>
        new(WorkItemId.New(), "feature", "main", 1, "do x", ProjectId: projectId);

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        public string Id => "fake";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
