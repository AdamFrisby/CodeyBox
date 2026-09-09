using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the regression-test-selection seam: the default selector is
/// byte-identical to the legacy full-suite command, the seam is DI-registered and
/// consumes the <see cref="ITestRunnerAuditor"/> capability (not a raw argv), the
/// mode hot-reloads through <see cref="IOptionsMonitor{T}"/>, and the merge/release
/// verification path never consults the selector.
/// </summary>
public sealed class TestSelectorTests
{
    private static readonly string[] BaseDotnetTest = ["dotnet", "test", "--no-build"];

    private static DotnetTestAuditor NewRunner()
        => new(new DotnetTestAuditorOptions { Name = "csharp:test-pass", BaseArgv = BaseDotnetTest });

    private static TestSelectionRequest NewRequest(ITestRunnerAuditor runner)
        => new(runner, baseRef: "main", changedFiles:
        [
            new TestSelectionChangedFile("src/Foo.cs", [new ChangedLineRange(10, 3)]),
        ]);

    // ---- Invariant: default (Mode=all) emits the byte-identical dotnet-test command.

    [Fact]
    public void RunAllSelector_ReturnsWholeSuite_ByteIdenticalInvocation()
    {
        var runner = NewRunner();
        var decision = new RunAllTestSelector().Select(NewRequest(runner));

        Assert.True(decision.Selection.IsAll);
        Assert.Equal(RunAllTestSelector.FullSuiteJustification, decision.Justification);

        // The auditor applies the selection through BuildInvocation — the full
        // suite yields exactly the legacy command with no --filter.
        var argv = runner.BuildInvocation(decision.Selection, TestRunOptions.Default);
        Assert.Equal<string[]>(BaseDotnetTest, [.. argv]);
    }

    [Fact]
    public void NarrowingSelection_FlowsThroughBuildInvocation_NotStringEditedArgv()
    {
        // A selector narrows by returning a TestSelection; the auditor turns it into
        // a --filter via BuildInvocation. This proves the seam's contract: the
        // selector never string-edits argv — it hands a selection to the auditor.
        var runner = NewRunner();
        ITestSelector selector = new FixedSelector(new TestSelection(["My.Suite.OneTest"]));

        var decision = selector.Select(NewRequest(runner));
        var argv = runner.BuildInvocation(decision.Selection, TestRunOptions.Default);

        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build", "--filter", "FullyQualifiedName=My.Suite.OneTest"],
            [.. argv]);
    }

    // ---- Invariant: config hot-reloads via a live mode accessor / IOptionsMonitor.

    [Fact]
    public void ConfiguredSelector_ReadsModeLive_AndSwitchesDispatchOnReload()
    {
        // Two distinct selectors keyed by two mode values prove the dispatcher
        // re-reads the accessor on EVERY call (the hot-reload contract): flipping
        // the backing value routes the next Select to the other selector, rather
        // than a value captured once at construction.
        const TestSelectionMode second = (TestSelectionMode)1;
        var forAll = new CountingSelector(TestSelection.All);
        var forSecond = new CountingSelector(new TestSelection(["Only.When.Second"]));
        var currentMode = TestSelectionMode.All;
        var configured = new ConfiguredTestSelector(
            () => currentMode,
            new Dictionary<TestSelectionMode, ITestSelector>
            {
                [TestSelectionMode.All] = forAll,
                [second] = forSecond,
            });

        var runner = NewRunner();

        Assert.True(configured.Select(NewRequest(runner)).Selection.IsAll);
        Assert.Equal(1, forAll.Calls);
        Assert.Equal(0, forSecond.Calls);

        currentMode = second; // operator hot-reloads Audit:TestSelection:Mode.
        Assert.False(configured.Select(NewRequest(runner)).Selection.IsAll);
        Assert.Equal(1, forAll.Calls);
        Assert.Equal(1, forSecond.Calls);
    }

    [Fact]
    public void ConfiguredSelector_ThrowsForUnregisteredMode()
    {
        var configured = new ConfiguredTestSelector(
            () => (TestSelectionMode)999,
            new Dictionary<TestSelectionMode, ITestSelector> { [TestSelectionMode.All] = new RunAllTestSelector() });

        Assert.Throws<InvalidOperationException>(() => configured.Select(NewRequest(NewRunner())));
    }

    // ---- Mode parsing (single source of truth shared by the accessor and validator).

    [Theory]
    [InlineData("all", TestSelectionMode.All)]
    [InlineData("ALL", TestSelectionMode.All)]
    [InlineData("  all  ", TestSelectionMode.All)]
    public void ModeParser_ParsesKnownModes(string value, TestSelectionMode expected)
    {
        Assert.True(TestSelectionModeParser.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
        Assert.Equal(expected, TestSelectionModeParser.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("changed")]
    [InlineData("impacted")]
    public void ModeParser_RejectsUnknownModes(string? value)
    {
        Assert.False(TestSelectionModeParser.TryParse(value, out _));
        Assert.Throws<FormatException>(() => TestSelectionModeParser.Parse(value));
    }

    // ---- Request / decision guards (make invalid states unrepresentable).

    [Fact]
    public void Request_RejectsBlankBaseRefAndNullCollaborators()
    {
        var runner = NewRunner();
        Assert.Throws<ArgumentException>(() => new TestSelectionRequest(runner, " ", []));
        Assert.Throws<ArgumentNullException>(() => new TestSelectionRequest(null!, "main", []));
        Assert.Throws<ArgumentNullException>(() => new TestSelectionRequest(runner, "main", null!));
    }

    [Fact]
    public void ChangedLineRange_RejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangedLineRange(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangedLineRange(1, -1));
        var r = new ChangedLineRange(5, 3);
        Assert.Equal(8, r.EndLineExclusive);
    }

    [Fact]
    public void Decision_RejectsBlankJustification()
        => Assert.Throws<ArgumentException>(() => new TestSelectionDecision(TestSelection.All, " "));

    // ---- DI wiring: the seam resolves, consumes ITestRunnerAuditor, and defaults to all.

    [Fact]
    public void Program_RegistersTestSelector_DefaultingToFullSuite()
    {
        using var factory = new SelectorWiringFactory();

        var selector = factory.Services.GetRequiredService<ITestSelector>();

        // Consumes the DI-registered ITestRunnerAuditor CAPABILITY — the request
        // carries the auditor, and the selector's output flows through the
        // auditor's BuildInvocation (never a hand-edited argv).
        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();
        var decision = selector.Select(new TestSelectionRequest(runner, "main", []));

        Assert.True(decision.Selection.IsAll);
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build"],
            [.. runner.BuildInvocation(decision.Selection, runner.CurrentRunOptions)]);
    }

    [Fact]
    public void Program_BindsModeFromConfig_ViaOptionsMonitor()
    {
        using var factory = new SelectorWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "all",
        });

        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<TestSelectionOptions>>();
        Assert.Equal("all", monitor.CurrentValue.Mode);
        Assert.Equal(TestSelectionMode.All, TestSelectionModeParser.Parse(monitor.CurrentValue.Mode));
    }

    [Fact]
    public void Program_DefaultsModeToAll_WhenSectionAbsent()
    {
        using var factory = new SelectorWiringFactory();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<TestSelectionOptions>>();
        Assert.Equal(TestSelectionModeParser.DefaultModeName, monitor.CurrentValue.Mode);
    }

    [Fact]
    public void Program_RejectsUnknownMode_FailFast()
    {
        using var factory = new SelectorWiringFactory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TestSelectionOptions.SectionName}:Mode"] = "does-not-exist",
        });

        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<TestSelectionOptions>>();
        // The validator rejects the value the moment the options are materialised,
        // so a bad config edit fails loudly instead of silently narrowing.
        Assert.Throws<OptionsValidationException>(() => _ = monitor.CurrentValue);
    }

    // ---- Invariant: the merge/release verification path ignores the selector.

    [Fact]
    public void RequiredBuildVerification_StructurallyCannotConsultSelector()
    {
        // The merge/release verifier (process:required-build) must always run the
        // full surface, so it takes NO ITestSelector dependency at all: a future
        // agent cannot accidentally route the merge gate through the narrowing seam.
        AssertNoTestSelectorDependency(typeof(SandboxRequiredBuildVerifier));

        var gate = typeof(SandboxRequiredBuildVerifier).Assembly
            .GetType("CodeyBox.Orchestrator.RequiredBuildGate");
        Assert.NotNull(gate);
        AssertNoTestSelectorDependency(gate!);
    }

    [Fact]
    public async Task RequiredBuildVerification_NeverInvokesSelector()
    {
        var recording = new CountingSelector(new TestSelection(["Should.Never.Run"]));
        using var factory = new SelectorWiringFactory(configureServices: services =>
        {
            services.RemoveAll<ITestSelector>();
            services.AddSingleton<ITestSelector>(recording);
        });

        var verifier = factory.Services.GetRequiredService<IRequiredBuildVerifier>();

        // A repository that does not exist resolves to Skipped/Unavailable before
        // any sandbox work — enough to exercise the merge/release entry point with a
        // recording selector present in the graph.
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

    private static void AssertNoTestSelectorDependency(Type type)
    {
        foreach (var ctor in type.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType == typeof(ITestSelector));
        }

        foreach (var field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            Assert.NotEqual(typeof(ITestSelector), field.FieldType);
        }
    }

    private sealed class FixedSelector(TestSelection selection) : ITestSelector
    {
        public TestSelectionDecision Select(TestSelectionRequest request)
            => new(selection, "fixed test selection");
    }

    private sealed class CountingSelector(TestSelection selection) : ITestSelector
    {
        public int Calls { get; private set; }

        public TestSelectionDecision Select(TestSelectionRequest request)
        {
            Calls++;
            return new TestSelectionDecision(selection, "counting selector");
        }
    }

    // (No fake ITestRunnerAuditor: the pure tests drive the real DotnetTestAuditor
    //  so the seam's output genuinely flows through its BuildInvocation.)

    /// <summary>
    /// Minimal test host that boots the real composition root so the DI-registered
    /// <see cref="ITestSelector"/> and its options wiring are exercised end to end.
    /// </summary>
    private sealed class SelectorWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-selector-wiring-{Guid.NewGuid():N}.db");
        private readonly IReadOnlyDictionary<string, string?> _extra;
        private readonly Action<IServiceCollection>? _configureServices;

        public SelectorWiringFactory(
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
                var tmp = Path.GetTempPath();
                var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
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
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
