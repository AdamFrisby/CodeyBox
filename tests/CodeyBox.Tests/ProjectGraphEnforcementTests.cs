using CodeyBox.Api;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.DotnetTestRunnerPlugin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Enforcing <c>project-graph</c> test selection: with
/// <c>Audit:TestSelection:Mode=project-graph</c> the per-item
/// <c>csharp:test-pass</c> run executes only the affected subset, while the
/// merge/release path still runs everything and any selector failure falls
/// back to the full suite.
/// </summary>
public sealed class ProjectGraphEnforcementTests
{
    private static readonly string[] BaseDotnetTest = ["dotnet", "test", "--no-build"];

    [Theory]
    [InlineData("project-graph", TestSelectionMode.ProjectGraph)]
    [InlineData("Project-Graph", TestSelectionMode.ProjectGraph)]
    [InlineData("  project_graph  ", TestSelectionMode.ProjectGraph)]
    [InlineData("projectgraph", TestSelectionMode.ProjectGraph)]
    public void ModeParser_ParsesProjectGraph(string value, TestSelectionMode expected)
    {
        Assert.True(TestSelectionModeParser.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
        Assert.Equal(expected, TestSelectionModeParser.Parse(value));
    }

    [Fact]
    public async Task EnforcingRun_LeafChange_ExecutesOnlyAffectedTests()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            "diff --git a/src/Leaf/A.cs b/src/Leaf/A.cs\n+++ b/src/Leaf/A.cs\n@@ -0,0 +10,1 @@\n+var x = 1;\n",
            LeafBaseline(),
            "Passed! - Failed: 0, Passed: 1");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.Contains("--filter", testArgv);
        var filterIndex = ((List<string>)[.. testArgv]).IndexOf("--filter");
        var filter = testArgv[filterIndex + 1];
        Assert.Contains("Ns.Leaf.LeafTests", filter);
        Assert.DoesNotContain("Ns.Other.OtherTests", filter);

        Assert.NotNull(result.TestSelection);
        Assert.Equal(TestSelectionMode.ProjectGraph.ToString(), result.TestSelection.Mode);
        Assert.Equal(ProjectGraphTestSelector.SelectorName, result.TestSelection.Selector);
        Assert.Equal(TestSelectionTelemetryComputer.AssessmentEnforced, result.TestSelection.Assessment);
        Assert.Equal(1, result.TestSelection.SelectedCount);
        Assert.Equal(2, result.TestSelection.TotalCount);
    }

    [Fact]
    public async Task EnforcingRun_CoreChange_FallsBackToFullSuite()
    {
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(
            "diff --git a/src/CodeyBox.Core/Foo.cs b/src/CodeyBox.Core/Foo.cs\n+++ b/src/CodeyBox.Core/Foo.cs\n@@ -0,0 +10,1 @@\n+var x = 1;\n",
            LeafBaseline(),
            "Passed!");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
    }

    [Fact]
    public async Task EnforcingRun_SelectorError_FallsBackToFullSuite()
    {
        var sandbox = SandboxFor(
            "diff --git a/src/Leaf/A.cs b/src/Leaf/A.cs\n+++ b/src/Leaf/A.cs\n@@ -0,0 +10,1 @@\n+var x = 1;\n",
            LeafBaseline(),
            "Passed!");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new ThrowingSelector(),
                Sink = new InMemoryTestSelectionShadowSink(),
                ModeAccessor = () => TestSelectionMode.ProjectGraph,
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
    public async Task EnforcingRun_AmbiguousSelection_FallsBackToFullSuite()
    {
        var sandbox = SandboxFor(
            "diff --git a/src/Leaf/A.cs b/src/Leaf/A.cs\n+++ b/src/Leaf/A.cs\n@@ -0,0 +10,1 @@\n+var x = 1;\n",
            LeafBaseline(),
            "Passed!");
        var auditor = new DotnetTestAuditor(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new BlankFilterSelector(),
                Sink = new InMemoryTestSelectionShadowSink(),
                ModeAccessor = () => TestSelectionMode.ProjectGraph,
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
    public void FilterExpression_EscapesVstestMetacharacters()
    {
        // Baseline test names are untrusted input: every entry must become a
        // single escaped FullyQualifiedName match — no raw operators survive,
        // so a crafted name cannot rewrite the executed subset.
        var auditor = EnforcingRunner(new InMemoryTestSelectionShadowSink());
        var argv = auditor.BuildInvocation(
            new TestSelection(["Ns.Leaf.LeafTests|A&B", "FullyQualifiedName=x", "A~B=C(1)!"]),
            TestRunOptions.Default);

        var filterIndex = ((List<string>)[.. argv]).IndexOf("--filter");
        Assert.True(filterIndex >= 0);
        Assert.Equal(
            @"FullyQualifiedName=Ns.Leaf.LeafTests\|A\&B|FullyQualifiedName=FullyQualifiedName\=x|FullyQualifiedName=A\~B\=C\(1\)\!",
            argv[filterIndex + 1]);
    }

    [Fact]
    public async Task EnforcingRun_ZeroTestsExecuted_FallsBackToFullSuite()
    {
        // A narrowed run that exits 0 with zero tests executed (a filter that
        // matches nothing) must fall back to the full suite, not report a pass.
        var sink = new InMemoryTestSelectionShadowSink();
        var baseline = LeafBaseline();
        var sandbox = new FakeSandbox(exec =>
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "git")
                return new SandboxExecResult(0, LeafDiff(), "");
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return new SandboxExecResult(0, baseline, "");
            if (exec.Argv.Contains("--filter"))
                return new SandboxExecResult(0, "No test matches the given testcase filter `FullyQualifiedName=Ns.Leaf.LeafTests`.", "");
            return new SandboxExecResult(0, "Passed! - Failed: 0, Passed: 2", "");
        });
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgvs = sandbox.ExecutedArgv.Where(a => a.Count > 1 && a[1] == "test").ToList();
        Assert.Equal(2, testArgvs.Count);
        Assert.Contains("--filter", testArgvs[0]);
        Assert.DoesNotContain("--filter", testArgvs[1]);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
        Assert.Contains("zero tests", result.TestSelection.Detail);
    }

    [Fact]
    public async Task EnforcingRun_StaleBaseline_FallsBackToFullSuite()
    {
        // A baseline older than MaxBaselineAge must not narrow the enforcing
        // run, even for a change confined to one leaf project.
        var sink = new InMemoryTestSelectionShadowSink();
        var sandbox = SandboxFor(LeafDiff(), LeafBaseline(DateTimeOffset.UtcNow.AddDays(-8)), "Passed!");
        var auditor = EnforcingRunner(sink);

        var result = await auditor.RunAsync(sandbox, "/work", ContextFor());

        Assert.True(result.Passed);
        var testArgv = Assert.Single(sandbox.ExecutedArgv, a => a.Count > 1 && a[1] == "test");
        Assert.DoesNotContain("--filter", testArgv);
        Assert.Equal(TestSelectionShadowRecord.AssessmentFullSuite, result.TestSelection!.Assessment);
        Assert.Contains("stale", result.TestSelection.Detail);
    }

    [Fact]
    public void Program_ResolvesProjectGraphSelector_ForEnforcingMode()
    {
        using var factory = new EnforcingWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "project-graph",
        });

        var selector = factory.Services.GetRequiredService<ITestSelector>();
        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();
        var decision = selector.Select(new TestSelectionRequest(runner, "main", [], baseline: null));

        Assert.True(decision.Selection.IsAll);
        Assert.Contains(ProjectGraphTestSelector.SelectorName, decision.Justification);
    }

    [Fact]
    public void Program_DefaultModeStaysAll()
    {
        using var factory = new EnforcingWiringFactory();
        var monitor = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TestSelectionOptions>>();
        Assert.Equal(TestSelectionModeParser.DefaultModeName, monitor.CurrentValue.Mode);
    }

    private static DotnetTestAuditor EnforcingRunner(InMemoryTestSelectionShadowSink sink)
        => new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = BaseDotnetTest,
            Shadow = new TestSelectionShadowConfig
            {
                Selector = new ProjectGraphTestSelector(() => new CoverageTestSelectionOptions()),
                Sink = sink,
                ModeAccessor = () => TestSelectionMode.ProjectGraph,
                OptionsAccessor = () => new CoverageTestSelectionOptions(),
            },
        });

    private static string LeafDiff()
        => "diff --git a/src/Leaf/A.cs b/src/Leaf/A.cs\n+++ b/src/Leaf/A.cs\n@@ -0,0 +10,1 @@\n+var x = 1;\n";

    private static string LeafBaseline(DateTimeOffset? producedAt = null)
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
                "src/Leaf/Leaf.csproj": ["Ns.Leaf.LeafTests"],
                "src/CodeyBox.Core/CodeyBox.Core.csproj": ["Ns.Leaf.LeafTests", "Ns.Other.OtherTests"]
              },
              "tests": {
                "Ns.Leaf.LeafTests": { "file": "tests/Leaf.Tests/LeafTests.cs", "covers": {} },
                "Ns.Other.OtherTests": { "file": "tests/Other.Tests/OtherTests.cs", "covers": {} }
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

    private sealed class BlankFilterSelector : ITestSelector
    {
        public TestSelectionDecision Select(TestSelectionRequest request)
            => new(new TestSelection([" "]), "blank filter");
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
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-enforcing-wiring-");
        private string _dbPath => _scratch.DbPath("enforcing-wiring.db");
        private readonly IReadOnlyDictionary<string, string?> _extra;

        public EnforcingWiringFactory(IReadOnlyDictionary<string, string?>? extra = null)
        {
            _extra = extra ?? new Dictionary<string, string?>();
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
