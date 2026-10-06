using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.RoslynatorAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Roslynator auditor's Unity analyzer preset
/// (<c>Microsoft.Unity.Analyzers</c>, UNT* diagnostics) without a separate
/// assembly or parser:
/// - A real Unity-specific diagnostic (UNT0001, empty Unity message —
///   <see href="https://github.com/microsoft/Microsoft.Unity.Analyzers"/>)
///   flows through the production auditor and SARIF parser into a structured
///   finding with rule id, location, and mapped severity; warnings stay
///   advisory (the hybrid gate does not fail on them).
/// - The preset's <c>AnalyzerAssemblies</c> knob is validated
///   (repository-relative .dll, no traversal, bounded count), presence-probed
///   in the audited tree, and emitted as structured
///   <c>--analyzer-assemblies</c> argv before the <c>ProjectPath</c>
///   positional; raw <c>ExtraArguments</c> spellings defer the knob.
/// - Unity coverage is explicit: UNT* rules without an analyzer, the preset
///   without <c>ProjectPath</c>, or a missing assembly is deterministic
///   coverage-unavailable infrastructure — never a pass. Partial coverage
///   (one of several assemblies missing) likewise fails closed.
/// - A local synthetic Unity project fixture mirrors the documented pinned
///   setup (generated project plus pinned analyzer reference); full Unity
///   compilation and editor validation are out of scope and never claimed.
/// </summary>
public sealed class RoslynatorUnityPresetTests
{
    private const string UnityAnalyzerDll = "tools/analyzers/Microsoft.Unity.Analyzers.dll";
    private const string UnityProject = "src/UnityGame.sln";

    // Shape mirrors real `roslynator analyze --analyzer-assemblies
    // Microsoft.Unity.Analyzers.dll -o /dev/stdout --output-format sarif`:
    // UNT0001 (empty Unity message) at warning, relative artifact URI.
    private const string UnitySarifUntWarning = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Roslynator", "version": "1.0.0.0" } },
              "results": [
                {
                  "ruleId": "UNT0001",
                  "level": "warning",
                  "message": { "text": "Empty Unity message 'Update' in 'Player'. Unity messages are called by the runtime even if they're empty." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Assets/Scripts/Player.cs" },
                        "region": { "startLine": 12 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task UnityFinding_FlowsThroughSharedParser_WithRuleIdLocationAndAdvisorySeverity()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(EchoRequestedPaths(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // UNT0001 is a warning: advisory under the hybrid gate, so the audit
        // passes but the finding still surfaces with stable identity.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("UNT0001", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("Assets/Scripts/Player.cs:12", finding.Location);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task UnityPreset_EmitsAnalyzerAssemblies_BeforeProjectPositional()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(EchoRequestedPaths(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("roslynator", argv[0]);
        Assert.Contains("analyze", argv);
        var flagIndex = argv.ToList().IndexOf("--analyzer-assemblies");
        Assert.True(flagIndex >= 0 && flagIndex + 1 < argv.Count);
        Assert.Equal("./" + UnityAnalyzerDll, argv[flagIndex + 1]);
        Assert.Equal("./" + UnityProject, argv[^1]);
        Assert.True(flagIndex < argv.Count - 1);
    }

    [Fact]
    public async Task UnityPreset_WithoutProjectPath_IsCoverageUnavailable_NeverAPass()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(RoslynatorAuditor.ProjectPathKey, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task UntIncludedRules_WithoutAnalyzer_IsCoverageUnavailable_NeverSilentPass()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "UNT0001",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(RoslynatorAuditor.AnalyzerAssembliesKey, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task UntIncludedRules_WithAnalyzerAndProject_FiltersToUntFindings()
    {
        const string mixedSarif = """
            {
              "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "Roslynator", "version": "1.0.0.0" } },
                  "results": [
                    {
                      "ruleId": "UNT0001",
                      "level": "warning",
                      "message": { "text": "Empty Unity message." },
                      "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "Assets/Scripts/Player.cs" }, "region": { "startLine": 12 } } }]
                    },
                    {
                      "ruleId": "CS0219",
                      "level": "warning",
                      "message": { "text": "The variable 'unused' is assigned but its value is never used" },
                      "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "Assets/Scripts/Player.cs" }, "region": { "startLine": 20 } } }]
                    }
                  ]
                }
              ]
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(EchoRequestedPaths(exec));
            return Task.FromResult(new SandboxExecResult(1, mixedSarif, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
                ["Scoped:IncludedRules"] = "UNT0001",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("UNT0001", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAnalyzerAssembly_IsCoverageUnavailable_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
            {
                // Only the Unity-generated project exists; the analyzer DLL
                // was never vendored into the tree.
                var echoed = exec.Argv.Skip(4).Where(a => !a.EndsWith(".dll", StringComparison.Ordinal));
                return Task.FromResult(new SandboxExecResult(0, string.Join("\n", echoed) + "\n", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task PartialCoverage_OneAssemblyMissing_FailsClosed_NeverPartialPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
            {
                // The Unity analyzer is present but the companion analyzer
                // is not: partial coverage must not become a partial pass.
                var echoed = exec.Argv.Skip(4).Where(a => a.Contains("Unity", StringComparison.Ordinal));
                return Task.FromResult(new SandboxExecResult(0, string.Join("\n", echoed) + "\n", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll + ", tools/analyzers/Extra.Analyzers.dll",
            }),
            CancellationToken.None);

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AnalyzerAssembly_Traversal_IsRejected_BeforeAnyExec()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = "../outside/Microsoft.Unity.Analyzers.dll",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(RoslynatorAuditor.AnalyzerAssembliesKey, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task AnalyzerAssembly_AbsolutePath_IsRejected()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(new SandboxExecResult(0, "", "")));

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = "/abs/tools/Microsoft.Unity.Analyzers.dll",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task AnalyzerAssembly_NonDll_IsRejected()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(new SandboxExecResult(0, "", "")));

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = "tools/analyzers/notes.txt",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(".dll", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task AnalyzerAssemblies_OverBound_IsRejected()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(new SandboxExecResult(0, "", "")));

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = string.Join(
                    ",",
                    Enumerable.Range(0, RoslynatorAuditor.MaxAnalyzerAssemblies + 1)
                        .Select(i => $"tools/a{i}.dll")),
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task ExtraArgumentsAnalyzerAssemblies_DefersKnobEmission_StillRequiresProject()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(EchoRequestedPaths(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, UnitySarifUntWarning, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = UnityProject,
                ["Scoped:AnalyzerAssemblies"] = UnityAnalyzerDll,
                ["Scoped:ExtraArguments"] = "--analyzer-assemblies, ./custom/Custom.Analyzers.dll",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        // The operator spelling wins: the knob DLL is not emitted twice.
        Assert.DoesNotContain("./" + UnityAnalyzerDll, argv);
        Assert.Contains("./custom/Custom.Analyzers.dll", argv);
    }

    [Fact]
    public async Task ExtraArgumentsUnityAnalyzer_WithoutProjectPath_IsCoverageUnavailable()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(Ok(exec)));

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--analyzer-assemblies, ./tools/Microsoft.Unity.Analyzers.dll",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(RoslynatorAuditor.ProjectPathKey, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task GenericAnalyzerAssemblies_WithoutProjectPath_AllowsDiscovery()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(EchoRequestedPaths(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "0 diagnostics found\n", ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AnalyzerAssemblies"] = "tools/analyzers/Custom.Analyzers.dll",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("--analyzer-assemblies", scanExec!.Argv);
    }

    [Fact]
    public async Task SyntheticUnityFixture_MatchesDocumentedPinnedSetup()
    {
        var fixtureDir = await SeedSyntheticUnityFixtureAsync();

        try
        {
            var csproj = await File.ReadAllTextAsync(Path.Combine(fixtureDir, "UnityGame.csproj"));
            var player = await File.ReadAllTextAsync(Path.Combine(fixtureDir, "Assets", "Scripts", "Player.cs"));

            // Pinned analyzer package reference the auditor verifies against
            // via AnalyzerAssemblies (PackageReference Version is the
            // operator-owned pin; the audit never restores it).
            Assert.Contains("Microsoft.Unity.Analyzers", csproj, StringComparison.Ordinal);
            Assert.Contains("1.28.0", csproj, StringComparison.Ordinal);
            // The UNT0001 pattern the SARIF fixture reports: an empty Unity
            // message on a MonoBehaviour.
            Assert.Contains("MonoBehaviour", player, StringComparison.Ordinal);
            Assert.Contains("void Update()", player, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static async Task<string> SeedSyntheticUnityFixtureAsync()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-unity-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "Assets", "Scripts"));

        // Synthetic stand-in for a Unity-generated project: correct-shape
        // references (UnityEngine stub plus the pinned analyzer package) for
        // local preset validation only — never a claim of full Unity
        // compilation or editor validation, which needs the Unity editor.
        await File.WriteAllTextAsync(Path.Combine(dir, "UnityGame.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <Nullable>disable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Unity.Analyzers" Version="1.28.0" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "Assets", "Scripts", "Player.cs"),
            "using UnityEngine;\npublic class Player : MonoBehaviour\n{\n    private void Update()\n    {\n    }\n}\n");

        return dir;
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: RoslynatorAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Roslynator C# Static Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, RoslynatorAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("roslynator", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "roslynator" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    // Simulates the repository-file presence probe with every requested
    // path present: the probe echoes each present path one per line.
    private static SandboxExecResult EchoRequestedPaths(SandboxExec exec)
        => new(0, string.Join("\n", exec.Argv.Skip(4)) + "\n", "");

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

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
