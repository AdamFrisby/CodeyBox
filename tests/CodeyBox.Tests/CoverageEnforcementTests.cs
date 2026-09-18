using CodeyBox.Api;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.DotnetTestRunnerPlugin;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Enforcing <c>coverage</c> test selection: with
/// <c>Audit:TestSelection:Mode=coverage</c> the per-item
/// <c>csharp:test-pass</c> run executes only the tests whose recorded
/// per-test coverage intersects the changed lines, NESTED INSIDE the
/// project-graph superset (coverage can only shrink it, never grow beyond
/// it). The fallback ladder runs coverage → project-graph → all: a change
/// with no intersecting coverage but a narrowing superset executes the
/// superset, while a shared-root change, stale baseline, selector error, or
/// the merge/release path runs the full suite.
/// </summary>
public sealed class CoverageEnforcementTests
{
    private static readonly string[] BaseDotnetTest = ["dotnet", "test", "--no-build"];

    [Theory]
    [InlineData("coverage", TestSelectionMode.Coverage)]
    [InlineData("Coverage", TestSelectionMode.Coverage)]
    [InlineData("  coverage  ", TestSelectionMode.Coverage)]
    [InlineData("COVERAGE", TestSelectionMode.Coverage)]
    public void ModeParser_ParsesCoverage(string value, TestSelectionMode expected)
    {
        Assert.True(TestSelectionModeParser.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
        Assert.Equal(expected, TestSelectionModeParser.Parse(value));
    }

    [Fact]
    public async Task EnforcingRun_PrivateMethodChange_RunsOnlyCoveringTests()
    {
        // End-to-end acceptance: a change to one executable line runs only the
        // tests that cover it — never the superset remainder, never tests
        // outside the superset.
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 10),
            CoverageBaseline(),
            "Passed! - Failed: 0, Passed: 1");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.Contains("--filter", testArgv);
        var filter = testArgv[((List<string>)[.. testArgv]).IndexOf("--filter") + 1];
        Assert.Contains("Ns.Leaf.LeafTests", filter);
        Assert.DoesNotContain("Ns.Leaf.OtherTests", filter);
        Assert.DoesNotContain("Ns.Leaf.NewTests", filter);
        Assert.DoesNotContain("Ns.Other.UnrelatedTests", filter);

        Assert.NotNull(result.TestSelection);
        Assert.Equal(TestSelectionMode.Coverage.ToString(), result.TestSelection.Mode);
        Assert.Equal(CoverageTestSelector.SelectorName, result.TestSelection.Selector);
        Assert.Equal(
            [ProjectGraphTestSelector.SelectorName, CoverageTestSelector.SelectorName],
            result.TestSelection.Layers);
        Assert.Equal(TestSelectionTelemetryComputer.AssessmentEnforced, result.TestSelection.Assessment);
        Assert.Equal(1, result.TestSelection.SelectedCount);
        Assert.Equal(4, result.TestSelection.TotalCount);
        Assert.Empty(result.TestSelection.Fallbacks);
    }

    [Fact]
    public async Task EnforcingRun_NoIntersectingCoverage_FallsBackToProjectGraph()
    {
        // The changed line is referenced but covered by nothing: the coverage
        // rung carries no signal, so the ladder descends to the superset.
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 99),
            CoverageBaseline(),
            "Passed! - Failed: 0, Passed: 2");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.Contains("--filter", testArgv);
        var filter = testArgv[((List<string>)[.. testArgv]).IndexOf("--filter") + 1];
        Assert.Contains("Ns.Leaf.LeafTests", filter);
        Assert.Contains("Ns.Leaf.OtherTests", filter);
        Assert.DoesNotContain("Ns.Other.UnrelatedTests", filter);

        Assert.NotNull(result.TestSelection);
        Assert.Equal(TestSelectionTelemetryComputer.AssessmentEnforced, result.TestSelection.Assessment);
        Assert.Equal(2, result.TestSelection.SelectedCount);
        Assert.Equal(4, result.TestSelection.TotalCount);
        var fallback = Assert.Single(result.TestSelection.Fallbacks);
        Assert.Contains(CoverageTestSelector.ProjectGraphRungMarker, fallback);
    }

    [Fact]
    public async Task EnforcingRun_FileUnknownToCoverageButKnownToGraph_FallsBackToProjectGraph()
    {
        // Absent per-file coverage (no record references the file) with a
        // narrowing project-graph superset executes the superset, not the
        // full suite.
        var sink = new InMemoryTestSelectionShadowSink();
        var baseline = $$"""
            {
              "format": "codeybox-test-selection-baseline/1",
              "commit": "abc123",
              "producedAtUtc": "{{DateTimeOffset.UtcNow.AddHours(-1):O}}",
              "fileProject": { "src/Brand/New.cs": "src/Brand/Brand.csproj" },
              "projects": { "src/Brand/Brand.csproj": ["Ns.Brand.BrandTests"] },
              "tests": {
                "Ns.Brand.BrandTests": {
                  "file": "tests/Brand.Tests/BrandTests.cs",
                  "covers": { "src/Brand/Other.cs": [7] }
                }
              }
            }
            """;
        var sandbox = SandboxFor(
            DiffFor("src/Brand/New.cs", startLine: 3),
            baseline,
            "Passed! - Failed: 0, Passed: 1");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.Contains("--filter", testArgv);
        var filter = testArgv[((List<string>)[.. testArgv]).IndexOf("--filter") + 1];
        Assert.Contains("Ns.Brand.BrandTests", filter);
        Assert.NotNull(result.TestSelection);
        Assert.Equal(TestSelectionTelemetryComputer.AssessmentEnforced, result.TestSelection.Assessment);
    }

    [Fact]
    public async Task EnforcingRun_StaleBaseline_FallsBackToFullSuite()
    {
        // A baseline older than MaxBaselineAge fails BOTH rungs (the ladder
        // descends through the project-graph rung, which is stale too).
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 10),
            CoverageBaseline(DateTimeOffset.UtcNow.AddDays(-8)),
            "Passed!");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
        Assert.Contains("stale", result.TestSelection.Detail);
    }

    [Fact]
    public async Task EnforcingRun_CoreChange_FallsBackToFullSuite()
    {
        // A change owned by an ALWAYS-FULL project (the shared core contract
        // assembly) runs the full suite even with fresh coverage data.
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/CodeyBox.Core/Foo.cs", startLine: 10),
            CoverageBaseline(),
            "Passed!");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
        Assert.Contains("always-full", result.TestSelection.Detail);
    }

    [Fact]
    public async Task EnforcingRun_SupersetSelectorError_FallsBackToFullSuite()
    {
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 10),
            CoverageBaseline(),
            "Passed!");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ThrowingSelector(),
                    () => new CoverageTestSelectionOptions(),
                    TimeProvider.System),
                Sink = new InMemoryTestSelectionShadowSink(),
                ModeAccessor = () => TestSelectionMode.Coverage,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
    }

    [Fact]
    public async Task EnforcingRun_CoverageOptionsError_FallsBackToProjectGraph()
    {
        // The coverage rung cannot read its knobs, but the already-computed
        // superset decision narrowed without them — the ladder descends to it.
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 10),
            CoverageBaseline(),
            "Passed! - Failed: 0, Passed: 2");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(() => new CoverageTestSelectionOptions()),
                    ThrowingOptions,
                    TimeProvider.System),
                Sink = sink,
                ModeAccessor = () => TestSelectionMode.Coverage,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.Contains("--filter", testArgv);
        Assert.NotNull(result.TestSelection);
        Assert.Equal(TestSelectionTelemetryComputer.AssessmentEnforced, result.TestSelection.Assessment);
    }

    [Fact]
    public async Task EnforcingRun_AllMode_IsKillSwitch()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            DiffFor("src/Leaf/A.cs", startLine: 10),
            CoverageBaseline(),
            "Passed!");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(() => new CoverageTestSelectionOptions()),
                    () => new CoverageTestSelectionOptions(),
                    TimeProvider.System),
                Sink = sink,
                ModeAccessor = () => TestSelectionMode.All,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public void Program_ResolvesCoverageSelector_ForEnforcingMode()
    {
        using var factory = new EnforcingWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "coverage",
        });

        var selector = factory.Services.GetRequiredService<ITestSelector>();
        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();
        var decision = selector.Select(new TestSelectionRequest(runner, "main", [], baseline: null));

        Assert.True(decision.Selection.IsAll);
        Assert.Contains(CoverageTestSelector.SelectorName, decision.Justification);
    }

    [Fact]
    public void Program_DefaultModeStaysAll()
    {
        using var factory = new EnforcingWiringFactory();
        var monitor = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TestSelectionOptions>>();
        Assert.Equal(TestSelectionModeParser.DefaultModeName, monitor.CurrentValue.Mode);
    }

    [Fact]
    public async Task RequiredBuildVerification_NeverInvokesSelector_UnderCoverageMode()
    {
        // FULL-SUITE-ON-MAIN IS STRUCTURAL: even with Mode=coverage the
        // merge/release verifier takes no ITestSelector dependency and never
        // consults the seam.
        var recording = new CountingSelector();
        using var factory = new EnforcingWiringFactory(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{TestSelectionOptions.SectionName}:Mode"] = "coverage",
            },
            services =>
            {
                services.RemoveAll<ITestSelector>();
                services.AddSingleton<ITestSelector>(recording);
            });

        var verifier = factory.Services.GetRequiredService<IRequiredBuildVerifier>();
        var result = await verifier.VerifyAsync(new RequiredBuildVerificationRequest
        {
            WorkItemId = new WorkItemId(Guid.NewGuid()),
            ProjectId = new ProjectId("test-project"),
            RepositoryId = "does-not-exist-" + Guid.NewGuid().ToString("N"),
            BaseBranch = "main",
            WorkBranch = "main",
            Phase = "audit",
            SandboxPolicy = new RequiredBuildSandboxPolicy(),
        }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEqual(RequiredBuildVerificationStatus.Passed, result.Status);
        Assert.NotEqual(RequiredBuildVerificationStatus.Failed, result.Status);
        Assert.Equal(0, recording.Calls);
    }

    private static CoverageTestSelectionOptions ThrowingOptions()
        => throw new InvalidOperationException("options down");

    private static DotnetTestAuditor EnforcingRunner(InMemoryTestSelectionShadowSink sink)
        => new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new CoverageTestSelector(
                    new ProjectGraphTestSelector(() => new CoverageTestSelectionOptions()),
                    () => new CoverageTestSelectionOptions(),
                    TimeProvider.System),
                Sink = sink,
                ModeAccessor = () => TestSelectionMode.Coverage,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

    private static string DiffFor(string path, int startLine)
        => $"diff --git a/{path} b/{path}\n+++ b/{path}\n@@ -0,0 +{startLine},1 @@\n+var x = 1;\n";

    private static string CoverageBaseline(DateTimeOffset? producedAt = null)
        => $$"""
            {
              "format": "codeybox-test-selection-baseline/1",
              "commit": "abc123",
              "producedAtUtc": "{{(producedAt ?? DateTimeOffset.UtcNow.AddHours(-1)):O}}",
              "fileProject": {
                "src/Leaf/A.cs": "src/Leaf/Leaf.csproj",
                "src/CodeyBox.Core/Foo.cs": "src/CodeyBox.Core/CodeyBox.Core.csproj"
              },
              "projects": {
                "src/Leaf/Leaf.csproj": ["Ns.Leaf.LeafTests", "Ns.Leaf.OtherTests"],
                "src/CodeyBox.Core/CodeyBox.Core.csproj": ["Ns.Leaf.LeafTests", "Ns.Leaf.OtherTests", "Ns.Other.UnrelatedTests", "Ns.Leaf.NewTests"]
              },
              "tests": {
                "Ns.Leaf.LeafTests": {
                  "file": "tests/Leaf.Tests/LeafTests.cs",
                  "covers": { "src/Leaf/A.cs": [10, 11, 12] }
                },
                "Ns.Leaf.OtherTests": {
                  "file": "tests/Leaf.Tests/OtherTests.cs",
                  "covers": { "src/Leaf/A.cs": [50] }
                },
                "Ns.Leaf.NewTests": { "file": "tests/Leaf.Tests/NewTests.cs", "covers": {} },
                "Ns.Other.UnrelatedTests": {
                  "file": "tests/Other.Tests/UnrelatedTests.cs",
                  "covers": { "src/Other.cs": [5] }
                }
              }
            }
            """;

    private static FakeSandbox SandboxFor(string diff, string baseline, string testOutput)
        => new(exec =>
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "git")
                return new SandboxExecResult(0, diff, "");
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return new SandboxExecResult(0, baseline, "");
            return new SandboxExecResult(0, testOutput, "");
        });

    private static AuditContext ContextFor()
        => new(WorkItemId.New(), "work", "main", 1, "prompt");

    private sealed class ThrowingSelector : ITestSelector
    {
        public TestSelectionDecision Select(TestSelectionRequest request)
            => throw new InvalidOperationException("selector down");
    }

    private sealed class CountingSelector : ITestSelector
    {
        public int Calls { get; private set; }

        public TestSelectionDecision Select(TestSelectionRequest request)
        {
            Calls++;
            return new TestSelectionDecision(TestSelection.All, "counting selector");
        }
    }

    private sealed class FakeSandbox(Func<SandboxExec, SandboxExecResult> onExec) : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec = onExec;
        public List<IReadOnlyList<string>> ExecutedArgv { get; } = new();
        public string Id => "fake";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecutedArgv.Add(exec.Argv);
            return Task.FromResult(_onExec(exec));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EnforcingWiringFactory : WebApplicationFactory<Program>
    {
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-coverage-enforcing-wiring-");
        private string _dbPath => _scratch.DbPath("coverage-enforcing-wiring.db");
        private readonly IReadOnlyDictionary<string, string?> _extra;
        private readonly Action<IServiceCollection>? _configureServices;

        public EnforcingWiringFactory(
            IReadOnlyDictionary<string, string?>? extra = null,
            Action<IServiceCollection>? configureServices = null)
        {
            _extra = extra ?? new Dictionary<string, string?>();
            _configureServices = configureServices;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
                };
                foreach (var kv in _extra)
                    settings[kv.Key] = kv.Value;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                _configureServices?.Invoke(services);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            base.Dispose(disposing);
        }
    }
}
