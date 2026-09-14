using System.Reflection;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.DotnetTestRunnerPlugin;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Packaging coverage for the test-runner plugin seam: the bundled dotnet
/// runner ships as a <c>[CodeyBoxPlugin]</c> entry with the canonical
/// byte-identical command, and the pytest reference stub demonstrates a peer
/// <see cref="ITestRunnerAuditor"/> without touching <c>CodeyBox.Core</c>.
/// The safety-critical merge-gate behaviour itself stays covered by the
/// existing soundness tests (selection, telemetry, wiring) driving the same
/// <see cref="DotnetTestAuditor"/> implementation this package owns.
/// </summary>
public sealed class TestRunnerPluginPackagingTests
{
    [Fact]
    public void DotnetEntry_CarriesPluginAttributeSatisfiedByHost()
    {
        var attr = typeof(DotnetTestRunner).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(DotnetTestRunner.PluginId, attr.Id);
        Assert.True(CodeyBoxApiVersion.Satisfies(attr.MinHostApiVersion));
    }

    [Fact]
    public void DotnetEntry_DefaultOptions_EmitByteIdenticalLegacyCommand()
    {
        ITestRunnerAuditor runner = new DotnetTestRunner();
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build"],
            [.. runner.BuildInvocation(TestSelection.All, TestRunOptions.Default)]);
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build", "--list-tests"],
            [.. runner.TestSuite.EnumerationArgv]);
        Assert.Equal(TestFramework.DotnetTest, runner.TestSuite.Framework);
    }

    [Fact]
    public void DotnetEntry_MirrorsCanonicalGateIdentity()
    {
        ITestRunnerAuditor runner = new DotnetTestRunner();
        Assert.Equal("csharp:test-pass", runner.Name);
        Assert.Equal("shell", runner.Kind);
        Assert.Equal(AuditCapabilities.None, runner.Required);
        Assert.True(runner.CanShortCircuitOnBlockingFinding);
        Assert.Equal(AuditorRole.BuildTestGate, runner.Role);
        Assert.Equal(BuildTestGateEvidence.Test, runner.BuildTestGateEvidence);
        Assert.IsType<DotnetTestCommandResultClassifier>(runner.ResultClassifier);
        Assert.Equal(TestRunOptions.Default, runner.CurrentRunOptions);
    }

    [Fact]
    public async Task DotnetEntry_ScopedConfig_NarrowsRunOptions()
    {
        var runner = new DotnetTestRunner();
        await runner.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["BlameHangTimeout"] = "00:03:00",
            ["AuditorIdleTimeout"] = "00:07:00",
        }));

        Assert.Equal(TimeSpan.FromMinutes(3), runner.CurrentRunOptions.BlameHangTimeout);
        Assert.Equal(TimeSpan.FromMinutes(7), runner.CurrentRunOptions.IdleTimeout);
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build", "--blame-hang", "--blame-hang-timeout", "180s"],
            [.. runner.BuildInvocation(TestSelection.All, runner.CurrentRunOptions)]);
    }

    [Fact]
    public async Task DotnetEntry_InvalidScopedConfig_KeepsDefaults()
    {
        var runner = new DotnetTestRunner();
        await runner.InitializeAsync(BuildPluginContext(new Dictionary<string, string?>
        {
            ["BlameHangTimeout"] = "not-a-timespan",
            ["AuditorIdleTimeout"] = "-00:01:00",
        }));

        // Fail-safe: a bad value never breaks load and never leaks into argv.
        Assert.Equal(TestRunOptions.Default, runner.CurrentRunOptions);
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build"],
            [.. runner.BuildInvocation(TestSelection.All, runner.CurrentRunOptions)]);
    }

    [Fact]
    public void PytestStub_CarriesPluginAttributeSatisfiedByHost()
    {
        var attr = typeof(PytestTestRunner).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(PytestTestRunner.PluginId, attr.Id);
        Assert.True(CodeyBoxApiVersion.Satisfies(attr.MinHostApiVersion));
    }

    [Fact]
    public void PytestStub_DescribesPytestSuite()
    {
        ITestRunnerAuditor runner = new PytestTestRunner();
        Assert.Equal("pytest:test-pass", runner.Name);
        Assert.Equal(TestFramework.Pytest, runner.TestSuite.Framework);
        Assert.Equal<string[]>(
            ["pytest", "--collect-only", "-q"],
            [.. runner.TestSuite.EnumerationArgv]);
        Assert.Equal<string[]>(["pytest"], [.. runner.BuildInvocation(TestSelection.All, TestRunOptions.Default)]);
        Assert.Equal<string[]>(
            ["pytest", "-k", "test_login or test_checkout"],
            [.. runner.BuildInvocation(new TestSelection(["test_login", "test_checkout"]), TestRunOptions.Default)]);
    }

    [Fact]
    public async Task PytestStub_RunAsync_ThrowsRatherThanFakingAPass()
    {
        ITestRunnerAuditor runner = new PytestTestRunner();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(null!, "/repo", null!));
        Assert.Contains("reference stub", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestFramework_DeclaresPeerRunners()
    {
        Assert.True(Enum.IsDefined(typeof(TestFramework), TestFramework.DotnetTest));
        Assert.True(Enum.IsDefined(typeof(TestFramework), TestFramework.Pytest));
        Assert.True(Enum.IsDefined(typeof(TestFramework), TestFramework.GoTest));
        Assert.True(Enum.IsDefined(typeof(TestFramework), TestFramework.CargoTest));
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> values)
    {
        var settings = values.ToDictionary(
            kv => $"CodeyBox:Plugins:{DotnetTestRunner.PluginId}:{kv.Key}",
            kv => kv.Value);
        var root = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var host = new TestPluginHost(root.GetSection($"CodeyBox:Plugins:{DotnetTestRunner.PluginId}"));
        return new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: DotnetTestRunner.PluginId,
            PluginDisplayName: "CodeyBox: dotnet test runner",
            Host: host);
    }

    private sealed class TestPluginHost : IPluginHost
    {
        public TestPluginHost(IConfigurationSection scoped) => ScopedConfig = scoped;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; }
    }
}
