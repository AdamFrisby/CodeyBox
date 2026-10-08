using System.Diagnostics;
using CodeyBox.Build.MSBuild;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

// CBX-NEXT-062: MSBuild binary logs are ONE adapter behind the neutral
// BuildDiagnostics contract. Fixtures below are genuine binlog bytes: a
// two-project solution is built with the real `dotnet` toolchain (no
// package restore — no network) and the pinned StructuredLogger writer
// records the log; every parse assertion runs through the production
// reader/parser/registry/verifier path. The non-MSBuild synthetic producer
// proves the same contract accepts other toolchains without dotnet or
// binlogs. Fake tokens use obvious FAKE placeholders (repo convention) so
// scanners stay quiet while still matching the redaction patterns.
public sealed class MSBuildFixtureBinlogs
{
    private static readonly Lazy<Task<FixtureSet>> Generation =
        new(GenerateAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<FixtureSet> GetAsync() => Generation.Value;

    public sealed record FixtureSet(byte[] FailingBuild, byte[] PassingBuild);

    private static async Task<FixtureSet> GenerateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cbx-msbuild-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Fixture.slnx"),
                """
                <Solution>
                  <Project Path="ProjA/ProjA.csproj" />
                  <Project Path="ProjB/ProjB.csproj" />
                  <Project Path="ProjOk/ProjOk.csproj" />
                </Solution>
                """);
            WriteProject(root, "ProjA",
                """
                class Program
                {
                    static void M1()
                    {
                        string first = 123;
                    }
                    static void M2()
                    {
                        int second = "xyz";
                    }
                }
                """,
                extraTargets: """
                  <Target Name="EmitSecretWarning" BeforeTargets="CoreCompile">
                    <Warning Code="CBX0001" File="$(MSBuildProjectDirectory)/Program.cs" Text="deploy token gho_FAKE_REDACTION_TEST_TOKEN_XXX must not leak" />
                  </Target>
                """);
            WriteProject(root, "ProjB",
                """
                class ProgramB
                {
                    static void Entry()
                    {
                        string wrong = 123;
                    }
                }
                """);
            WriteProject(root, "ProjOk",
                """
                class ProgramOk
                {
                    static void Entry() { }
                }
                """);

            var failingLog = Path.Combine(root, "failing.binlog");
            var failing = await RunDotnetAsync(root,
                "build", Path.Combine(root, "Fixture.slnx"),
                "--disable-build-servers", "--maxcpucount:1", "--nologo", "-v:q", $"-bl:{failingLog}");
            if (failing.code == 0)
                throw new InvalidOperationException("Fixture build unexpectedly succeeded: " + failing.stderr);
            if (!File.Exists(failingLog) || new FileInfo(failingLog).Length == 0)
                throw new InvalidOperationException("Fixture build produced no binary log: " + failing.stderr);
            var failingBytes = await File.ReadAllBytesAsync(failingLog);

            var passingLog = Path.Combine(root, "passing.binlog");
            var passing = await RunDotnetAsync(root,
                "build", Path.Combine(root, "ProjOk", "ProjOk.csproj"),
                "--disable-build-servers", "--maxcpucount:1", "--nologo", "-v:q", $"-bl:{passingLog}");
            if (passing.code != 0)
                throw new InvalidOperationException("Fixture passing build failed: " + passing.stderr);
            var passingBytes = await File.ReadAllBytesAsync(passingLog);

            return new FixtureSet(failingBytes, passingBytes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void WriteProject(string root, string name, string programCs, string extraTargets = "")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
            """ + extraTargets + "\n</Project>\n");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), programCs);
    }

    private static async Task<(int code, string stdout, string stderr)> RunDotnetAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var part in args) psi.ArgumentList.Add(part);
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Fixture dotnet build timed out: " + string.Join(' ', args));
        }
        return (process.ExitCode, stdout, stderr);
    }
}

public sealed class MSBuildBinlogParserTests
{
    private static MSBuildDiagnosticsOptions EnabledOptions() =>
        new() { Enabled = true };

    private static BuildDiagnosticsSourceBinding TestBinding(string attemptSuffix = "a1") => new()
    {
        SourceRef = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
        Configuration = "Debug",
        Attempt = "attempt-" + attemptSuffix,
    };

    [Fact]
    public async Task Parse_GenuineFailingBuild_ExtractsCausalFailuresWithProjectTargetContext()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding(), EnabledOptions());

        Assert.Equal(BuildDiagnosticsStatus.Enriched, evidence.Status);
        Assert.Equal(MSBuildDiagnosticsOptions.ProviderId, evidence.ProviderId);
        // Two broken statements in ProjA plus one in ProjB: three real
        // compiler errors, each attributable to its project and target.
        Assert.True(evidence.TotalErrorCount >= 3, $"expected >= 3 errors, got {evidence.TotalErrorCount}");
        Assert.NotEmpty(evidence.Diagnostics);
        Assert.Contains(evidence.Diagnostics, static d =>
            d.Severity == BuildDiagnosticSeverity.Error
            && d.Code != null && d.Code.StartsWith("CS", StringComparison.Ordinal)
            && d.Project != null && d.Project.EndsWith(".csproj", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(d.Target)
            && d.Location != null && d.Location.Line > 0);

        var projAErrors = evidence.Diagnostics
            .Where(static d => d.Severity == BuildDiagnosticSeverity.Error
                && (d.Project ?? string.Empty).EndsWith("ProjA.csproj", StringComparison.Ordinal))
            .ToArray();
        var projBErrors = evidence.Diagnostics
            .Where(static d => d.Severity == BuildDiagnosticSeverity.Error
                && (d.Project ?? string.Empty).EndsWith("ProjB.csproj", StringComparison.Ordinal))
            .ToArray();
        Assert.True(projAErrors.Length >= 2, "expected multiple ProjA errors for causal-link coverage");
        Assert.NotEmpty(projBErrors);

        // Weak temporal causality: each project's earliest error is the root;
        // later same-project errors link to it; cross-project errors stay
        // unlinked so independent failures are fixed independently.
        var rootA = Assert.Single(projAErrors, static d => d.IsRootCause);
        var rootB = Assert.Single(projBErrors, static d => d.IsRootCause);
        Assert.Contains(rootA.Id, evidence.RootCauseIds);
        Assert.Contains(rootB.Id, evidence.RootCauseIds);
        foreach (var later in projAErrors.Where(d => d.Id != rootA.Id))
            Assert.Equal(new[] { rootA.Id }, later.CausedByIds);
        Assert.Empty(rootB.CausedByIds);
        Assert.DoesNotContain(projBErrors, d => d.CausedByIds.Contains(rootA.Id));

        // The planted CBX0001 warning rides along as non-root context.
        Assert.Contains(evidence.Diagnostics, static d =>
            d.Severity == BuildDiagnosticSeverity.Warning && d.Code == "CBX0001" && !d.IsRootCause);
    }

    [Fact]
    public async Task Parse_PassingBuildLog_YieldsInsufficientRatherThanActingAsEvidence()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.PassingBuild, TestBinding("ok"), EnabledOptions());

        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("no-diagnostics-found", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_MissingInput_IsInsufficient()
    {
        var evidence = await MSBuildBinlogParser.ParseAsync([], TestBinding("missing"), EnabledOptions());
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("missing", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_NonBinlogInput_IsInsufficient()
    {
        var evidence = await MSBuildBinlogParser.ParseAsync(
            System.Text.Encoding.ASCII.GetBytes("this is not a binary log"),
            TestBinding("malformed"),
            EnabledOptions());
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("malformed", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TruncatedBinlog_IsInsufficient()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var truncated = fixtures.FailingBuild[..Math.Max(64, fixtures.FailingBuild.Length / 2)];
        var evidence = await MSBuildBinlogParser.ParseAsync(truncated, TestBinding("truncated"), EnabledOptions());
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("truncated", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_OversizedInput_IsInsufficientBeforeBuffering()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var options = EnabledOptions() with { MaxBinlogBytes = 16 };
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("big"), options);
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("oversized", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_UnsupportedVersion_IsInsufficient()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var options = EnabledOptions() with { MaxSupportedFileFormatVersion = 1 };
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("version"), options);
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("unsupported-version", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SupportedVersionGate_AcceptsPinnedReaderRange()
    {
        Assert.True(EnabledOptions().IsSupportedVersion(MSBuildDiagnosticsOptions.PinnedFileFormatVersion));
        Assert.False(EnabledOptions().IsSupportedVersion(MSBuildDiagnosticsOptions.PinnedFileFormatVersion + 1000));
        Assert.False(EnabledOptions().IsSupportedVersion(1));
    }

    [Fact]
    public async Task Parse_ZeroTimeBudget_IsInsufficientWithoutReading()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var options = EnabledOptions() with { ParseTimeout = TimeSpan.Zero };
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("timeout"), options);
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("parse-timeout", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_CancelledCallerToken_PropagatesInsteadOfPassing()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("cancel"), EnabledOptions(), cts.Token));
    }

    [Fact]
    public async Task Parse_RedactsSecretsBeforeEvidence()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("secret"), EnabledOptions());

        Assert.Equal(BuildDiagnosticsStatus.Enriched, evidence.Status);
        var warning = Assert.Single(evidence.Diagnostics, static d => d.Code == "CBX0001");
        Assert.Contains("***", warning.Message, StringComparison.Ordinal);
        foreach (var diagnostic in evidence.Diagnostics)
        {
            var haystack = string.Join('\n',
                diagnostic.Message, diagnostic.Code, diagnostic.Project, diagnostic.Target,
                diagnostic.Location?.Path);
            Assert.DoesNotContain("gho_", haystack, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_REDACTION", haystack, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Parse_ShallowDepthBound_YieldsExplicitBoundedGap()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var options = EnabledOptions() with { MaxDepth = 1 };
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("shallow"), options);
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("bounds", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_TinyNodeBound_YieldsExplicitBoundedGap()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var options = EnabledOptions() with { MaxNodesVisited = 1000 };
        var evidence = await MSBuildBinlogParser.ParseAsync(fixtures.FailingBuild, TestBinding("nodes"), options);
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("bounds", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }
}

public sealed class MSBuildBinlogProducerTests
{
    [Fact]
    public void Producer_IsDisabledByDefault()
    {
        var producer = new MSBuildBinlogProducer();
        Assert.False(producer.IsEnabled);
        Assert.Equal(MSBuildDiagnosticsOptions.ProviderId, producer.ProviderId);
    }

    [Fact]
    public async Task Producer_DisabledByDefault_YieldsDisabledWithoutTouchingPayload()
    {
        var registry = new BuildDiagnosticsProducerRegistry([new MSBuildBinlogProducer()]);
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "main", Attempt = "a" };
        var evidence = await registry.ProduceAsync(
            MSBuildDiagnosticsOptions.ProviderId,
            new BuildDiagnosticsProductionRequest
            {
                ExpectedBinding = binding,
                PayloadFormat = MSBuildDiagnosticsOptions.PayloadFormat,
                PayloadBytes = [0x1F, 0x8B],
            });
        Assert.Equal(BuildDiagnosticsStatus.Disabled, evidence.Status);
    }

    [Fact]
    public async Task Registry_UnknownProvider_YieldsDisabled()
    {
        var registry = new BuildDiagnosticsProducerRegistry([]);
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "main", Attempt = "a" };
        var evidence = await registry.ProduceAsync(
            "no-such-provider",
            new BuildDiagnosticsProductionRequest { ExpectedBinding = binding, PayloadFormat = "x/v1" });
        Assert.Equal(BuildDiagnosticsStatus.Disabled, evidence.Status);
    }

    [Fact]
    public async Task Producer_WrongPayloadFormat_IsRejectedByExactMatch()
    {
        var producer = new MSBuildBinlogProducer(() => new MSBuildDiagnosticsOptions { Enabled = true });
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "main", Attempt = "a" };
        var evidence = await producer.ProduceAsync(new BuildDiagnosticsProductionRequest
        {
            ExpectedBinding = binding,
            PayloadFormat = MSBuildDiagnosticsOptions.PayloadFormat + "-evil-suffix",
            PayloadBytes = [0x1F, 0x8B],
        });
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("unsupported-payload", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Producer_Enabled_ParsesGenuineBinlog()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var producer = new MSBuildBinlogProducer(() => new MSBuildDiagnosticsOptions { Enabled = true });
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "abc", Configuration = "Debug", Attempt = "a9" };
        var evidence = await producer.ProduceAsync(new BuildDiagnosticsProductionRequest
        {
            ExpectedBinding = binding,
            PayloadFormat = MSBuildDiagnosticsOptions.PayloadFormat,
            PayloadBytes = fixtures.FailingBuild,
        });
        Assert.Equal(BuildDiagnosticsStatus.Enriched, evidence.Status);
        Assert.True(evidence.SourceBinding.Matches(binding));
    }

    [Fact]
    public void SourceBinding_MatchesByExactEqualityOnly()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "abc123", Configuration = "Debug", Attempt = "a1" };
        Assert.True(binding.Matches(binding with { }));
        Assert.False(binding.Matches(binding with { SourceRef = "abc1234" }));
        Assert.False(binding.Matches(binding with { SourceRef = "ABC123" }));
        Assert.False(binding.Matches(binding with { Configuration = "Release" }));
        Assert.False(binding.Matches(binding with { Attempt = "a2" }));
        Assert.False(binding.Matches(null));
        Assert.True(
            new BuildDiagnosticsSourceBinding { SourceRef = "x", Attempt = "a" }.Matches(
                new BuildDiagnosticsSourceBinding { SourceRef = "x", Configuration = null, Attempt = "a" }));
    }

    [Fact]
    public void Options_DefaultsAreDisabledAndValid()
    {
        var defaults = new MSBuildDiagnosticsOptions();
        Assert.False(defaults.Enabled);
        Assert.True(MSBuildDiagnosticsOptions.IsValid(defaults));
        Assert.True(MSBuildDiagnosticsOptions.IsValid(defaults with { Enabled = true }));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(null));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(defaults with { BuildConfiguration = "../evil" }));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(defaults with { BuildConfiguration = "" }));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(defaults with { ParseTimeout = TimeSpan.Zero }));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(defaults with { MaxBinlogBytes = 0 }));
        Assert.False(MSBuildDiagnosticsOptions.IsValid(defaults with { MaxDiagnostics = 0 }));
        Assert.True(MSBuildDiagnosticsOptions.IsValidConfiguration("Release"));
        Assert.False(MSBuildDiagnosticsOptions.IsValidConfiguration("a/b"));
    }
}

public sealed class MSBuildEvidenceMergerTests
{
    private static BuildDiagnosticsEvidence EnrichedPart(
        string attempt, string diagnosticId, string project, BuildDiagnosticSeverity severity) =>
        new()
        {
            ProviderId = MSBuildDiagnosticsOptions.ProviderId,
            SourceBinding = new BuildDiagnosticsSourceBinding { SourceRef = "sha", Configuration = "Debug", Attempt = attempt },
            Status = BuildDiagnosticsStatus.Enriched,
            Diagnostics =
            [
                new BuildDiagnostic
                {
                    Id = diagnosticId,
                    ProviderId = MSBuildDiagnosticsOptions.ProviderId,
                    Severity = severity,
                    Code = "CS0001",
                    Message = "boom",
                    Project = project,
                },
            ],
            RootCauseIds = severity == BuildDiagnosticSeverity.Error ? [diagnosticId] : [],
            TotalErrorCount = severity == BuildDiagnosticSeverity.Error ? 1 : 0,
            TotalWarningCount = severity == BuildDiagnosticSeverity.Warning ? 1 : 0,
        };

    [Fact]
    public void Merge_EmptyParts_IsMissing()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "sha", Configuration = "Debug", Attempt = "a" };
        var merged = MSBuildEvidenceMerger.Merge([], binding, new MSBuildDiagnosticsOptions { Enabled = true });
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, merged.Status);
        Assert.Contains("missing", merged.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_StaleBinding_ThrowsRatherThanAttaching()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "sha", Configuration = "Debug", Attempt = "a" };
        var stale = EnrichedPart("different-attempt", "msbuild:d0001", "P.csproj", BuildDiagnosticSeverity.Error);
        Assert.Throws<ArgumentException>(() =>
            MSBuildEvidenceMerger.Merge([stale], binding, new MSBuildDiagnosticsOptions { Enabled = true }));
    }

    [Fact]
    public void Merge_MultiFile_RecapsRelinksAndSums()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "sha", Configuration = "Debug", Attempt = "a" };
        var options = new MSBuildDiagnosticsOptions { Enabled = true, MaxDiagnostics = 2 };
        var merged = MSBuildEvidenceMerger.Merge(
            [
                EnrichedPart("a", "msbuild:d0001", "P1.csproj", BuildDiagnosticSeverity.Error),
                EnrichedPart("a", "msbuild:d0002", "P2.csproj", BuildDiagnosticSeverity.Error),
                EnrichedPart("a", "msbuild:d0003", "P1.csproj", BuildDiagnosticSeverity.Warning),
            ],
            binding,
            options);
        Assert.Equal(BuildDiagnosticsStatus.Enriched, merged.Status);
        Assert.Equal(2, merged.TotalErrorCount);
        Assert.Equal(1, merged.TotalWarningCount);
        Assert.Equal(2, merged.Diagnostics.Count);
        Assert.True(merged.Truncated);
        Assert.True(merged.RootCauseIds.Count >= 1);
    }

    [Fact]
    public void Merge_AllInsufficient_SurfacesFirstReason()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "sha", Configuration = "Debug", Attempt = "a" };
        var merged = MSBuildEvidenceMerger.Merge(
            [BuildDiagnosticsEvidence.Insufficient(MSBuildDiagnosticsOptions.ProviderId, binding, "missing: x")],
            binding,
            new MSBuildDiagnosticsOptions { Enabled = true });
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, merged.Status);
        Assert.Contains("missing", merged.Reason ?? string.Empty, StringComparison.Ordinal);
    }
}

// The generic path: a non-MSBuild producer behind the same neutral
// contract. No dotnet, no binlogs — proves Core stays
// language/ecosystem-neutral while the MSBuild adapter stays pluggable.
public sealed class SyntheticProducerTests
{
    private sealed class CargoLikeProducer : IBuildDiagnosticsProducer
    {
        public int Calls;
        public string ProviderId => "cargo-like-test";
        public bool IsEnabled => true;

        public Task<BuildDiagnosticsEvidence> ProduceAsync(
            BuildDiagnosticsProductionRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            if (!string.Equals(request.PayloadFormat, "cargo-like-test/v1", StringComparison.Ordinal))
                return Task.FromResult(BuildDiagnosticsEvidence.Insufficient(
                    ProviderId, request.ExpectedBinding, "unsupported-payload: not cargo-like-test/v1"));
            return Task.FromResult(new BuildDiagnosticsEvidence
            {
                ProviderId = ProviderId,
                SourceBinding = request.ExpectedBinding,
                Status = BuildDiagnosticsStatus.Enriched,
                Diagnostics =
                [
                    new BuildDiagnostic
                    {
                        Id = "cargo:E0308-1",
                        ProviderId = ProviderId,
                        Severity = BuildDiagnosticSeverity.Error,
                        Code = "E0308",
                        Message = "mismatched types: expected `i32`, found `&str`",
                        Location = new BuildDiagnosticLocation { Path = "src/main.rs", Line = 12, Column = 5 },
                        Project = "my-crate",
                        Target = "check",
                        IsRootCause = true,
                    },
                ],
                RootCauseIds = ["cargo:E0308-1"],
                TotalErrorCount = 1,
            });
        }
    }

    [Fact]
    public async Task Registry_AcceptsNonMSBuildProducerThroughNeutralContract()
    {
        var producer = new CargoLikeProducer();
        var registry = new BuildDiagnosticsProducerRegistry([producer, new MSBuildBinlogProducer()]);
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "deadbeef", Attempt = "try-1" };
        var evidence = await registry.ProduceAsync(
            "cargo-like-test",
            new BuildDiagnosticsProductionRequest
            {
                ExpectedBinding = binding,
                PayloadFormat = "cargo-like-test/v1",
                PayloadBytes = System.Text.Encoding.UTF8.GetBytes("""{"reason":"compiler-message"}"""),
            });

        Assert.Equal(1, producer.Calls);
        Assert.Equal(BuildDiagnosticsStatus.Enriched, evidence.Status);
        Assert.Equal("cargo-like-test", evidence.ProviderId);
        Assert.True(evidence.SourceBinding.Matches(binding));
        var rendered = BuildDiagnosticsFormatter.Describe(evidence);
        Assert.Contains("E0308", rendered, StringComparison.Ordinal);
        Assert.Contains("src/main.rs:12:5", rendered, StringComparison.Ordinal);
        Assert.Contains("root cause", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_DisabledEvidence_RendersNothing()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "x", Attempt = "a" };
        Assert.Equal(string.Empty, BuildDiagnosticsFormatter.Describe(
            BuildDiagnosticsEvidence.Disabled("any", binding)));
        Assert.Equal(string.Empty, BuildDiagnosticsFormatter.Describe(
            BuildDiagnosticsEvidence.NotApplicable("any", binding)));
    }

    [Fact]
    public void Formatter_InsufficientEvidence_IsExplicit()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "x", Attempt = "a" };
        var rendered = BuildDiagnosticsFormatter.Describe(
            BuildDiagnosticsEvidence.Insufficient("msbuild-binlog", binding, "oversized: 1 byte over"));
        Assert.Contains("unavailable", rendered, StringComparison.Ordinal);
        Assert.Contains("oversized", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_BoundsOutputLength()
    {
        var binding = new BuildDiagnosticsSourceBinding { SourceRef = "x", Attempt = "a" };
        var rendered = BuildDiagnosticsFormatter.Describe(
            BuildDiagnosticsEvidence.Insufficient("p", binding, new string('r', 5000)), maxChars: 100);
        Assert.True(rendered.Length <= 100, $"length {rendered.Length}");
        Assert.Contains("truncated", rendered, StringComparison.Ordinal);
    }
}

public sealed class MSBuildDiagnosticsEnrichmentTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cbx-msbuild-enrich-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { } }

    private sealed class ScriptedSandboxProvider : ISandboxProvider
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public ScriptedSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;
        public string Name => "scripted";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            Task.FromResult<ISandbox>(new ScriptedSandbox(_onExec));
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Array.Empty<ManagedSandboxInfo>());
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        private sealed class ScriptedSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
        {
            public string Id => "scripted-sandbox";
            public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
                Task.FromResult(onExec(exec));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CountingProducer : IBuildDiagnosticsProducer
    {
        public int Calls;
        public string ProviderId => MSBuildDiagnosticsOptions.ProviderId;
        public bool IsEnabled => true;
        public Task<BuildDiagnosticsEvidence> ProduceAsync(
            BuildDiagnosticsProductionRequest request, CancellationToken ct = default)
        {
            Calls++;
            return new MSBuildBinlogProducer(() => new MSBuildDiagnosticsOptions { Enabled = true })
                .ProduceAsync(request, ct);
        }
    }

    private static readonly string TestSha = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    private static SandboxExecResult BuildFailureResult(string stdout) =>
        new(1, stdout, string.Empty);

    private static Func<SandboxExec, SandboxExecResult> FailedBuildWithBinlog(byte[] binlog, Action<SandboxExec>? onBuild = null) =>
        exec =>
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "sh" && exec.Argv[1] == "-c")
            {
                var script = exec.Argv[2];
                if (script.Contains("codeybox-msbuild-binlogs", StringComparison.Ordinal)
                    && script.Contains("-bl:", StringComparison.Ordinal))
                {
                    onBuild?.Invoke(exec);
                    return BuildFailureResult("Build FAILED\nerror CS1525: Invalid expression term ';'\n");
                }
                if (script == SandboxRequiredBuildVerifier.BuildScript)
                {
                    onBuild?.Invoke(exec);
                    return BuildFailureResult("Build FAILED\nerror CS1525: Invalid expression term ';'\n");
                }
                if (script.Contains("ls -1", StringComparison.Ordinal))
                    return new SandboxExecResult(0, "target-1.binlog\n", string.Empty);
                if (script.Contains("base64 -w0", StringComparison.Ordinal))
                    return new SandboxExecResult(0, $"{binlog.Length}\n{Convert.ToBase64String(binlog)}", string.Empty);
            }
            if (exec.Argv is ["git", "rev-parse", "HEAD"])
                return new SandboxExecResult(0, TestSha + "\n", string.Empty);
            return new SandboxExecResult(0, string.Empty, string.Empty);
        };

    private async Task<(LocalGitHost gitHost, string repoId, string workBranch)> SetupRepoAsync(bool withDotnetMarker)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        if (withDotnetMarker)
        {
            await File.WriteAllTextAsync(Path.Combine(seed, "CodeyBox.slnx"), "# solution marker\n");
            await TestSupport.RunGit(seed, "add", "CodeyBox.slnx");
            await TestSupport.RunGit(seed, "commit", "-m", "add solution marker");
        }
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var itemId = WorkItemId.New();
        var projectId = new ProjectId("test-project");
        var baseBranch = "main";
        var workBranch = "feature/msbuild-diagnostics";
        var repoId = await gitHost.EnsureRepositoryAsync(itemId, seed, baseBranch);
        var clone = Path.Combine(_workspace, "branch-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", gitHost.GetRepoPath(repoId), clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", workBranch);
        await File.WriteAllTextAsync(Path.Combine(clone, "ok.txt"), "ok\n");
        await TestSupport.RunGit(clone, "add", "ok.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "branch exists");
        await TestSupport.RunGit(clone, "push", "origin", workBranch);
        return (gitHost, repoId, workBranch);
    }

    private static RequiredBuildVerificationRequest VerificationRequest(
        string repoId, string workBranch) => new()
        {
            WorkItemId = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
            RepositoryId = repoId,
            BaseBranch = "main",
            WorkBranch = workBranch,
            Phase = "audit",
        };

    [Fact]
    public void CaptureScript_UsesLeastDataBinlogFlags()
    {
        var script = MSBuildDiagnosticsEnrichment.BuildScriptWithBinlogCapture;
        Assert.Contains("-bl:", script, StringComparison.Ordinal);
        Assert.Contains("ProjectImports=None", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectImports=Embed", script, StringComparison.Ordinal);
        // Fixed directory: every exec is a fresh shell with its own $$, so
        // the fetch step could never find a PID-suffixed directory.
        Assert.Contains("binlog_dir=\"$tmp_root/codeybox-msbuild-binlogs\"", script, StringComparison.Ordinal);
        // A stale log from an earlier attempt must never attach to this one.
        Assert.Contains("rm -f \"$binlog_dir\"/target-*.binlog", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_FailedBuildWithBinlog_AttachesBoundRootCauseEvidence()
    {
        var fixtures = await MSBuildFixtureBinlogs.GetAsync();
        var (gitHost, repoId, workBranch) = await SetupRepoAsync(withDotnetMarker: true);
        string? buildScript = null;
        IReadOnlyDictionary<string, string>? buildEnv = null;
        var provider = new ScriptedSandboxProvider(FailedBuildWithBinlog(fixtures.FailingBuild, exec =>
        {
            buildScript = exec.Argv[2];
            buildEnv = exec.ExtraEnvironment;
        }));
        var producer = new CountingProducer();
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<SandboxRequiredBuildVerifier>.Instance,
            () => new MSBuildDiagnosticsOptions { Enabled = true },
            producer);

        var result = await verifier.VerifyAsync(VerificationRequest(repoId, workBranch), CancellationToken.None);

        // The authoritative outcome is untouched: still a build failure.
        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Equal(1, result.ExitCode);
        // The same isolated execution carried the capture (no second build).
        Assert.NotNull(buildScript);
        Assert.Contains("-bl:", buildScript, StringComparison.Ordinal);
        Assert.NotNull(buildEnv);
        Assert.Equal("Debug", buildEnv[MSBuildDiagnosticsEnrichment.BinlogConfigVariable]);
        Assert.Equal(1, producer.Calls);

        Assert.NotNull(result.BuildDiagnostics);
        var evidence = result.BuildDiagnostics;
        Assert.Equal(BuildDiagnosticsStatus.Enriched, evidence.Status);
        Assert.Equal(MSBuildDiagnosticsOptions.ProviderId, evidence.ProviderId);
        Assert.True(evidence.TotalErrorCount >= 3, $"expected >= 3 errors, got {evidence.TotalErrorCount}");
        Assert.NotEmpty(evidence.RootCauseIds);
        Assert.Equal(TestSha, evidence.SourceBinding.SourceRef);
        Assert.Equal("Debug", evidence.SourceBinding.Configuration);
        Assert.False(string.IsNullOrWhiteSpace(evidence.SourceBinding.Attempt));
        var artifact = Assert.Single(evidence.ArtifactRefs);
        Assert.Equal("target-1.binlog", artifact.Name);
        Assert.Equal(fixtures.FailingBuild.Length, artifact.ByteSize);
        Assert.DoesNotContain("gho_", string.Join('\n', evidence.Diagnostics.Select(static d => d.Message)), StringComparison.Ordinal);

        // Repair-loop text carries the causal failures plus context.
        var summary = RequiredBuildGate.BuildFailureSummary(result);
        Assert.Contains("CS", summary, StringComparison.Ordinal);
        Assert.Contains("structured diagnostics", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_DisabledByDefault_RunsPlainScriptWithNoDiagnostics()
    {
        var (gitHost, repoId, workBranch) = await SetupRepoAsync(withDotnetMarker: true);
        string? buildScript = null;
        var producer = new CountingProducer();
        var verifier = new SandboxRequiredBuildVerifier(
            new ScriptedSandboxProvider(FailedBuildWithBinlog([], exec => buildScript = exec.Argv[2])),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<SandboxRequiredBuildVerifier>.Instance);

        var result = await verifier.VerifyAsync(VerificationRequest(repoId, workBranch), CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Null(result.BuildDiagnostics);
        Assert.Equal(0, producer.Calls);
        Assert.Equal(SandboxRequiredBuildVerifier.BuildScript, buildScript);
        var summary = RequiredBuildGate.BuildFailureSummary(result);
        Assert.DoesNotContain("structured diagnostics", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_MissingBinlog_KeepsFailureWithExplicitGap()
    {
        var (gitHost, repoId, workBranch) = await SetupRepoAsync(withDotnetMarker: true);
        var provider = new ScriptedSandboxProvider(exec =>
        {
            if (exec.Argv.Count >= 3 && exec.Argv[1] == "-c" && exec.Argv[2].Contains("ls -1", StringComparison.Ordinal))
                return new SandboxExecResult(0, string.Empty, string.Empty);
            return FailedBuildWithBinlog([])(exec);
        });
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<SandboxRequiredBuildVerifier>.Instance,
            () => new MSBuildDiagnosticsOptions { Enabled = true },
            new MSBuildBinlogProducer(() => new MSBuildDiagnosticsOptions { Enabled = true }));

        var result = await verifier.VerifyAsync(VerificationRequest(repoId, workBranch), CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.NotNull(result.BuildDiagnostics);
        var evidence = result.BuildDiagnostics;
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.Contains("missing", evidence.Reason ?? string.Empty, StringComparison.Ordinal);
        var summary = RequiredBuildGate.BuildFailureSummary(result);
        Assert.Contains("structured diagnostics unavailable", summary, StringComparison.Ordinal);
        Assert.Contains("missing", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_CorruptBinlog_KeepsFailureWithExplicitGap()
    {
        var (gitHost, repoId, workBranch) = await SetupRepoAsync(withDotnetMarker: true);
        var corrupt = System.Text.Encoding.ASCII.GetBytes("definitely not a binary log");
        var provider = new ScriptedSandboxProvider(FailedBuildWithBinlog(corrupt));
        var verifier = new SandboxRequiredBuildVerifier(
            provider,
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<SandboxRequiredBuildVerifier>.Instance,
            () => new MSBuildDiagnosticsOptions { Enabled = true },
            new MSBuildBinlogProducer(() => new MSBuildDiagnosticsOptions { Enabled = true }));

        var result = await verifier.VerifyAsync(VerificationRequest(repoId, workBranch), CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.NotNull(result.BuildDiagnostics);
        var evidence = result.BuildDiagnostics;
        Assert.Equal(BuildDiagnosticsStatus.InsufficientDiagnostics, evidence.Status);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Reason));
    }

    [Fact]
    public async Task Verify_NonDotnetBranch_SkipsWithoutProducerCall()
    {
        var (gitHost, repoId, workBranch) = await SetupRepoAsync(withDotnetMarker: false);
        var producer = new CountingProducer();
        var verifier = new SandboxRequiredBuildVerifier(
            new ScriptedSandboxProvider(_ => throw new InvalidOperationException("no sandbox should be created")),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<SandboxRequiredBuildVerifier>.Instance,
            () => new MSBuildDiagnosticsOptions { Enabled = true },
            producer);

        var result = await verifier.VerifyAsync(VerificationRequest(repoId, workBranch), CancellationToken.None);

        Assert.Equal(RequiredBuildVerificationStatus.Skipped, result.Status);
        Assert.Null(result.BuildDiagnostics);
        Assert.Equal(0, producer.Calls);
    }
}
