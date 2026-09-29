using System.Text.Json;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.TestSelectionProducer;
using ProducerProgram = CodeyBox.TestSelectionProducer.Program;

namespace CodeyBox.Tests;

/// <summary>
/// Producer of <c>codeybox-test-selection-baseline/1</c>: round-trip through
/// the production strict reader, size-cap refusal, and path normalisation that
/// matches the <c>tests:coverage</c> gate.
/// </summary>
public sealed class TestSelectionBaselineProducerTests : IDisposable
{
    private static readonly BaselineReadLimits Limits =
        new(1024 * 1024, MaxTests: 10_000, MaxCoveredLines: 1_000_000);

    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-tsb-").FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void JsonWriter_RoundTripsThroughStrictReader()
    {
        var baseline = new TestSelectionBaseline(
            "abc123",
            new DateTimeOffset(2026, 9, 12, 5, 0, 0, TimeSpan.Zero),
            new BaselineProjectGraph(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["src/Foo/Bar.cs"] = "src/Foo/Foo.csproj",
                },
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["src/Foo/Foo.csproj"] = ["Ns.Foo.BarTests"],
                }),
            new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal)
            {
                ["Ns.Foo.BarTests"] = new BaselineTestEntry(
                    "tests/Foo.Tests/BarTests.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        ["src/Foo/Bar.cs"] = [12, 10, 10, 11],
                    }),
                ["Ns.Foo.NewTests"] = new BaselineTestEntry(
                    "tests/Foo.Tests/NewTests.cs",
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)),
            });

        var json = TestSelectionBaselineJson.Serialize(baseline, Limits);
        var parsed = TestSelectionBaselineParser.Parse(json, Limits);

        Assert.Contains(TestSelectionBaseline.FormatMarker, json, StringComparison.Ordinal);
        Assert.Equal("abc123", parsed.Commit);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 5, 0, 0, TimeSpan.Zero), parsed.ProducedAtUtc);
        Assert.Equal("src/Foo/Foo.csproj", parsed.ProjectGraph.FileProject["src/Foo/Bar.cs"]);
        Assert.Equal(["Ns.Foo.BarTests"], parsed.ProjectGraph.AffectedTestsByProject["src/Foo/Foo.csproj"]);
        Assert.Equal([10, 11, 12], parsed.Tests["Ns.Foo.BarTests"].Covers["src/Foo/Bar.cs"]);
        Assert.Empty(parsed.Tests["Ns.Foo.NewTests"].Covers);
    }

    [Fact]
    public void JsonWriter_SizeCap_DoesNotWriteAFile()
    {
        var path = Path.Combine(_root, "baseline.json");
        var baseline = new TestSelectionBaseline(
            "abc123",
            DateTimeOffset.UtcNow,
            new BaselineProjectGraph(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
            new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal)
            {
                ["T"] = new BaselineTestEntry("t.cs", new Dictionary<string, IReadOnlyList<int>>()),
            });

        var ex = Assert.Throws<FormatException>(() =>
            TestSelectionBaselineJson.WriteAtomic(path, baseline, new BaselineReadLimits(8, 10, 100)));
        Assert.Contains("size cap", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp*", SearchOption.AllDirectories));
    }

    [Fact]
    public void JsonWriter_SizeCap_DoesNotOverwriteExistingFile()
    {
        var path = Path.Combine(_root, "existing.json");
        File.WriteAllText(path, "sentinel");
        var baseline = new TestSelectionBaseline(
            "abc123",
            DateTimeOffset.UtcNow,
            new BaselineProjectGraph(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
            new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal)
            {
                ["T"] = new BaselineTestEntry("t.cs", new Dictionary<string, IReadOnlyList<int>>()),
            });

        var cap = Assert.Throws<FormatException>(() =>
            TestSelectionBaselineJson.WriteAtomic(path, baseline, new BaselineReadLimits(8, 10, 100)));
        Assert.Contains("size cap", cap.Message, StringComparison.Ordinal);
        Assert.Equal("sentinel", File.ReadAllText(path));
    }

    [Fact]
    public void CoverageMap_PathNormalisationMatchesCoverageGate()
    {
        const string repo = "/work";
        var xml =
            """
            <coverage>
              <sources><source>/work</source></sources>
              <packages><package><classes>
                <class filename="/work/src/Foo.cs">
                  <lines>
                    <line number="10" hits="3" />
                    <line number="11" hits="0" />
                  </lines>
                </class>
                <class filename="src\Bar.cs">
                  <lines>
                    <line number="4" hits="1" />
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        var report = CoberturaParser.Parse(xml);
        var gate = CoberturaParser.BuildLineMap([report], repo);
        var covers = TestSelectionCoverageMap.FromReports([report], repo);

        Assert.Equal(
            "src/Foo.cs",
            CoberturaParser.ToRepositoryRelative("/work/src/Foo.cs", ["/work"], repo));
        Assert.Equal(
            "src/Bar.cs",
            CoberturaParser.ToRepositoryRelative("src\\Bar.cs", ["/work"], repo));

        Assert.True(gate.ContainsKey("src/Foo.cs"));
        Assert.True(gate.ContainsKey("src/Bar.cs"));
        Assert.True(covers.ContainsKey("src/Foo.cs"));
        Assert.True(covers.ContainsKey("src/Bar.cs"));
        Assert.Equal(
            gate.Keys.OrderBy(k => k, StringComparer.Ordinal),
            covers.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal([10], covers["src/Foo.cs"]);
        Assert.Equal([4], covers["src/Bar.cs"]);
        Assert.True(gate["src/Foo.cs"].ContainsKey(11));
        Assert.DoesNotContain(11, covers["src/Foo.cs"]);
    }

    [Fact]
    public void CoverageMap_CoercesCoverletPathMissingLeadingSlash()
    {
        const string repo = "/tmp/codeybox-tsb-abc/live";
        var xml =
            """
            <coverage>
              <sources><source>/tmp/codeybox-tsb-abc/live</source></sources>
              <packages><package><classes>
                <class filename="tmp/codeybox-tsb-abc/live/src/Lib/Adder.cs">
                  <lines><line number="7" hits="1" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        var report = CoberturaParser.Parse(xml);
        var covers = TestSelectionCoverageMap.FromReports([report], repo);
        Assert.True(covers.ContainsKey("src/Lib/Adder.cs"),
            "Keys: " + string.Join(",", covers.Keys));
        Assert.Equal([7], covers["src/Lib/Adder.cs"]);
        Assert.Equal(
            "src/Lib/Adder.cs",
            CoberturaParser.ToRepositoryRelative("/tmp/codeybox-tsb-abc/live/src/Lib/Adder.cs", [repo], repo));
    }

    [Fact]
    public void ListParser_ReadsIndentedNamesAndEnforcesCaps()
    {
        var stdout =
            """
            Test run for Lib.Tests.dll
            The following Tests are available:
                Lib.Tests.AdderTests.Adds
                Lib.Tests.UntouchedTests.AlwaysTrue
            """;
        var names = DotnetTestListParser.Parse(stdout, maxTests: 10, maxNameChars: 1024);
        Assert.Equal(["Lib.Tests.AdderTests.Adds", "Lib.Tests.UntouchedTests.AlwaysTrue"], names);

        var cap = Assert.Throws<TestSelectionBaselineProduceException>(() =>
            DotnetTestListParser.Parse(stdout, maxTests: 1, maxNameChars: 1024));
        Assert.Contains("test cap", cap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AffectedTestsGraph_IncludesTransitiveDependents()
    {
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["src/Bar/Bar.csproj"] = ["src/Foo/Foo.csproj"],
            ["tests/Lib.Tests/Lib.Tests.csproj"] = ["src/Bar/Bar.csproj"],
        };
        var owning = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Lib.Tests.AdderTests.Adds"] = "tests/Lib.Tests/Lib.Tests.csproj",
        };

        var affected = AffectedTestsGraph.Compute(references, owning);

        Assert.Contains("Lib.Tests.AdderTests.Adds", affected["src/Foo/Foo.csproj"]);
        Assert.Contains("Lib.Tests.AdderTests.Adds", affected["src/Bar/Bar.csproj"]);
        Assert.Contains("Lib.Tests.AdderTests.Adds", affected["tests/Lib.Tests/Lib.Tests.csproj"]);
    }

    [Fact]
    public async Task Producer_SizeCapExceeded_DoesNotWriteFile()
    {
        var repo = Path.Combine(_root, "repo");
        WriteTinyFixture(repo);
        var output = Path.Combine(_root, "out", "baseline.json");
        var runner = new ScriptedCommandRunner();
        runner.Handle = argv => Respond(argv, repo);
        var coverage = new StubCoverageCollector
        {
            Maps =
            {
                ["Lib.Tests.AdderTests.Adds"] = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                {
                    ["src/Lib/Adder.cs"] = [5, 6, 7],
                },
                ["Lib.Tests.UntouchedTests.AlwaysTrue"] =
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal),
            },
        };

        var producer = new TestSelectionBaselineProducer(runner, coverage);
        var options = new TestSelectionProducerOptions
        {
            RepoRoot = repo,
            OutputPath = output,
            Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            MaxParallelism = 1,
            MaxBaselineCoveredLines = 1,
            Clock = TimeProvider.System,
        };

        var ex = await Assert.ThrowsAsync<FormatException>(() => producer.ProduceAsync(options, CancellationToken.None));
        Assert.Contains("covered-line cap", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Producer_MaxTestsCap_DoesNotWriteFile()
    {
        var repo = Path.Combine(_root, "cap-tests");
        WriteTinyFixture(repo);
        var output = Path.Combine(_root, "cap-tests-out.json");
        var runner = new ScriptedCommandRunner { Handle = argv => Respond(argv, repo) };
        var producer = new TestSelectionBaselineProducer(runner, new StubCoverageCollector());
        var options = new TestSelectionProducerOptions
        {
            RepoRoot = repo,
            OutputPath = output,
            Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            MaxParallelism = 1,
            MaxBaselineTests = 1,
        };

        var ex = await Assert.ThrowsAsync<TestSelectionBaselineProduceException>(
            () => producer.ProduceAsync(options, CancellationToken.None));
        Assert.Contains("test cap", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact(Timeout = 180_000)]
    public async Task Producer_FixtureRoundTrip_ParsesWithStrictReader()
    {
        var repo = Path.Combine(_root, "live");
        WriteTinyFixture(repo);
        File.Copy(Path.Combine(FindRepoRoot(), "nuget.config"), Path.Combine(repo, "nuget.config"), overwrite: true);
        var output = Path.Combine(_root, "live-baseline.json");
        var producer = new TestSelectionBaselineProducer(new HostCommandRunner());
        var options = new TestSelectionProducerOptions
        {
            RepoRoot = repo,
            OutputPath = output,
            Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            MaxParallelism = 1,
            Clock = TimeProvider.System,
        };

        var produced = await producer.ProduceAsync(options, CancellationToken.None);
        Assert.True(File.Exists(output));

        var parsed = TestSelectionBaselineParser.Parse(await File.ReadAllTextAsync(output), Limits);
        Assert.Equal(TestSelectionBaseline.FormatMarker,
            JsonDocument.Parse(await File.ReadAllTextAsync(output)).RootElement.GetProperty("format").GetString());
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", parsed.Commit);
        Assert.Equal("src/Lib/Lib.csproj", parsed.ProjectGraph.FileProject["src/Lib/Adder.cs"]);
        Assert.Contains("Lib.Tests.AdderTests.Adds",
            parsed.ProjectGraph.AffectedTestsByProject["src/Lib/Lib.csproj"]);

        Assert.True(parsed.Tests.ContainsKey("Lib.Tests.AdderTests.Adds"));
        Assert.True(parsed.Tests.ContainsKey("Lib.Tests.UntouchedTests.AlwaysTrue"));
        Assert.Equal("tests/Lib.Tests/AdderTests.cs", parsed.Tests["Lib.Tests.AdderTests.Adds"].DefiningFile);
        Assert.Equal("tests/Lib.Tests/UntouchedTests.cs", parsed.Tests["Lib.Tests.UntouchedTests.AlwaysTrue"].DefiningFile);

        var adderCovers = parsed.Tests["Lib.Tests.AdderTests.Adds"].Covers;
        Assert.True(adderCovers.ContainsKey("src/Lib/Adder.cs"),
            "AdderTests should cover src/Lib/Adder.cs. Keys: " + string.Join(",", adderCovers.Keys));
        Assert.Contains(7, adderCovers["src/Lib/Adder.cs"]);

        Assert.Empty(parsed.Tests["Lib.Tests.UntouchedTests.AlwaysTrue"].Covers);

        var gateKeys = adderCovers.Keys;
        foreach (var file in gateKeys)
        {
            var viaParser = CoberturaParser.ToRepositoryRelative(
                Path.Combine(repo, file.Replace('/', Path.DirectorySeparatorChar)),
                [repo],
                repo);
            Assert.Equal(file, viaParser);
        }

        Assert.Equal(produced.Tests.Count, parsed.Tests.Count);
    }

    [Fact]
    public async Task Program_Help_ReturnsUsage()
    {
        var error = new StringWriter();
        var rc = await ProducerProgram.RunAsync(["--help"], new StringWriter(), error, CancellationToken.None);
        Assert.Equal(ProducerProgram.ExitUsage, rc);
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArgs_RequiresOutput()
    {
        var ex = Assert.Throws<TestSelectionBaselineProduceException>(() => ProducerProgram.ParseArgs(["--repo", "/tmp"]));
        Assert.Contains("--output", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_Produce_RoundTripsThroughStrictReader()
    {
        var repo = Path.Combine(_root, "cli-repo");
        WriteTinyFixture(repo);
        var output = Path.Combine(_root, "cli-baseline.json");
        var runner = new ScriptedCommandRunner { Handle = argv => Respond(argv, repo) };
        var coverage = new StubCoverageCollector
        {
            Maps =
            {
                ["Lib.Tests.AdderTests.Adds"] = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                {
                    ["src/Lib/Adder.cs"] = [7],
                },
                ["Lib.Tests.UntouchedTests.AlwaysTrue"] =
                    new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal),
            },
        };

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var rc = await ProducerProgram.RunAsync(
            [
                "produce",
                "--repo", repo,
                "--output", output,
                "--commit", "cccccccccccccccccccccccccccccccccccccccc",
                "--skip-build",
            ],
            stdout,
            stderr,
            runner,
            coverage,
            CancellationToken.None);

        Assert.Equal(ProducerProgram.ExitOk, rc);
        Assert.True(File.Exists(output));
        var parsed = TestSelectionBaselineParser.Parse(await File.ReadAllTextAsync(output), Limits);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", parsed.Commit);
        Assert.Equal([7], parsed.Tests["Lib.Tests.AdderTests.Adds"].Covers["src/Lib/Adder.cs"]);
        Assert.Empty(parsed.Tests["Lib.Tests.UntouchedTests.AlwaysTrue"].Covers);
        Assert.Contains(TestSelectionBaseline.FormatMarker, stdout.ToString(), StringComparison.Ordinal);
    }

    private static HostCommandResult Respond(IReadOnlyList<string> argv, string repo)
    {
        if (argv.Contains("--list-tests", StringComparer.Ordinal))
        {
            return new HostCommandResult(0,
                "The following Tests are available:\n    Lib.Tests.AdderTests.Adds\n    Lib.Tests.UntouchedTests.AlwaysTrue\n",
                "");
        }

        if (argv.Contains("sln", StringComparer.Ordinal) && argv.Contains("list", StringComparer.Ordinal))
        {
            return new HostCommandResult(0,
                "Project(s)\n----------\nsrc/Lib/Lib.csproj\ntests/Lib.Tests/Lib.Tests.csproj\n", "");
        }

        if (argv.Any(a => a.Contains("TargetPath", StringComparison.Ordinal)))
        {
            var dll = Path.Combine(repo, "tests", "Lib.Tests", "bin", "Debug", "net10.0", "Lib.Tests.dll");
            return new HostCommandResult(0, dll + "\n", "");
        }

        if (argv.Contains("-getItem:Compile", StringComparer.Ordinal))
        {
            var files = argv.Any(a => a.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal))
                ? new[] { "tests/Lib.Tests/AdderTests.cs", "tests/Lib.Tests/UntouchedTests.cs" }
                : new[] { "src/Lib/Adder.cs" };
            var items = string.Join(",", files.Select(f => JsonSerializer.Serialize(new
            {
                Identity = f,
                FullPath = Path.Combine(repo, f.Replace('/', Path.DirectorySeparatorChar)),
            })));
            return new HostCommandResult(0, "{\"Items\":{\"Compile\":[" + items + "]}}", "");
        }

        if (argv.Contains("build", StringComparer.Ordinal))
            return new HostCommandResult(0, "", "");
        if (argv.Contains("rev-parse", StringComparer.Ordinal))
            return new HostCommandResult(0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n", "");

        return new HostCommandResult(0, "", "");
    }

    private static void WriteTinyFixture(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(repo, "tests", "Lib.Tests"));
        File.WriteAllText(Path.Combine(repo, "Lib.slnx"),
            """
            <Solution>
              <Project Path="src/Lib/Lib.csproj" />
              <Project Path="tests/Lib.Tests/Lib.Tests.csproj" />
            </Solution>
            """);
        File.WriteAllText(Path.Combine(repo, "src", "Lib", "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(repo, "src", "Lib", "Adder.cs"),
            """
            namespace Lib;

            public static class Adder
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        File.WriteAllText(Path.Combine(repo, "tests", "Lib.Tests", "Lib.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <Using Include="Xunit" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(repo, "tests", "Lib.Tests", "AdderTests.cs"),
            """
            namespace Lib.Tests;

            public sealed class AdderTests
            {
                [Fact]
                public void Adds()
                    => Assert.Equal(5, Adder.Add(2, 3));
            }
            """);
        File.WriteAllText(Path.Combine(repo, "tests", "Lib.Tests", "UntouchedTests.cs"),
            """
            namespace Lib.Tests;

            public sealed class UntouchedTests
            {
                [Fact]
                public void AlwaysTrue()
                    => Assert.True(true);
            }
            """);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeyBox.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class ScriptedCommandRunner : IHostCommandRunner
    {
        public Func<IReadOnlyList<string>, HostCommandResult> Handle { get; set; } =
            _ => new HostCommandResult(0, "", "");

        public Task<HostCommandResult> RunAsync(
            IReadOnlyList<string> argv,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? extraEnvironment,
            int maxStdoutChars,
            int maxStderrChars,
            TimeSpan timeout,
            CancellationToken ct)
            => Task.FromResult(Handle(argv));
    }

    private sealed class StubCoverageCollector : IPerTestCoverageCollector
    {
        public Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>> Maps { get; } =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>> CollectAsync(
            string repoRoot,
            string testTarget,
            IReadOnlyList<string> testNames,
            TestSelectionProducerOptions options,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>>(Maps);
    }
}
