using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Tests.Uat.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Discovery-reporting tests: every configured plugin path produces a visible
/// outcome — found/loaded/ids/contracts per path, a named reason for every
/// skip or failure, stale host-contracts builds identified specifically, and
/// the administrative surface matching discovery exactly.
/// </summary>
public sealed class PluginDiscoveryReportingTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private sealed class StaleThrowingAssemblyLoader : IPluginAssemblyLoader
    {
        public IReadOnlyList<string> LoadedPaths => [];

        public Assembly Load(string absolutePath) =>
            throw new FileLoadException(
                "Could not load file or assembly 'CodeyBox.Core, Version=99.0.0.0, " +
                "Culture=neutral, PublicKeyToken=null'. The located assembly's manifest " +
                "definition does not match the assembly reference.");
    }

    [Fact]
    public void MissingFile_ProducesWarningNamingThePath_AndAssemblyReport()
    {
        const string missingPath = "/does/not/exist-missing-plugin.dll";
        var logger = new CapturingLogger<PluginLoader>();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [missingPath],
                Allowlist = ["*"],
                Enabled = ["*"],
            },
            EmptyConfig(),
            logger);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(missingPath, StringComparison.Ordinal));
        var report = Assert.Single(loader.GetAssemblyReports());
        Assert.Equal(missingPath, report.AssemblyPath);
        Assert.False(report.Found);
        Assert.False(report.Loaded);
        Assert.Equal(PluginSkipReason.FileMissing, report.SkipReason);
    }

    [Fact]
    public void StaleHostContracts_LoadFailure_NamesStaleContractsSpecifically()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var logger = new CapturingLogger<PluginLoader>();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [samplePath],
                Allowlist = ["*"],
                Enabled = ["*"],
            },
            EmptyConfig(),
            logger,
            assemblyLoader: new StaleThrowingAssemblyLoader());

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
        // The stale-assembly case gets its own message, not a generic failure.
        Assert.Contains(
            logger.Entries,
            e => e.Message.Contains("host contracts", StringComparison.Ordinal)
                && e.Message.Contains(samplePath, StringComparison.Ordinal));
        var report = Assert.Single(loader.GetAssemblyReports());
        Assert.Equal(PluginSkipReason.StaleHostContracts, report.SkipReason);
        // Statuses that optimistically passed the metadata gate are corrected.
        Assert.All(
            loader.GetDiscoveryStatuses().Where(static s => s.PluginId is "sample.auditor" or "sample.blocked-auditor"),
            static s =>
            {
                Assert.False(s.Loaded);
                Assert.Equal(PluginSkipReason.StaleHostContracts, s.SkipReason);
            });
    }

    [Fact]
    public void StaleContractClassifier_DistinguishesBindFailuresFromGenericOnes()
    {
        Assert.True(PluginContractStaleness.IsStaleContractFailure(
            new FileLoadException("Could not load file or assembly 'CodeyBox.Core, Version=2.0.0.0'")));
        Assert.True(PluginContractStaleness.IsStaleContractFailure(
            new InvalidOperationException("wrapper",
                new MissingMethodException("Method not found on 'CodeyBox.PluginSdk.Something'"))));
        Assert.False(PluginContractStaleness.IsStaleContractFailure(
            new InvalidOperationException("boom")));
        Assert.False(PluginContractStaleness.IsStaleContractFailure(
            new FileNotFoundException("Could not find file '/tmp/x.dll'")));
        Assert.False(PluginContractStaleness.IsStaleContractFailure(null));
    }

    [Fact]
    public void SuccessfulLoad_ReportsPluginIdAndContracts()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var logger = new CapturingLogger<PluginLoader>();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [samplePath],
                Allowlist = ["*"],
                Enabled = ["*"],
            },
            EmptyConfig(),
            logger);

        var plugins = loader.DiscoverPlugins();

        Assert.Equal(2, plugins.Count);
        var status = loader.GetDiscoveryStatuses().Single(s => s.PluginId == "sample.auditor");
        Assert.True(status.Loaded);
        Assert.Equal(PluginSkipReason.None, status.SkipReason);
        Assert.Contains("IAuditor", status.Contracts ?? []);
        var report = Assert.Single(loader.GetAssemblyReports());
        Assert.True(report.Found);
        Assert.True(report.Loaded);
        Assert.Contains("sample.auditor", report.PluginIds);
        Assert.Contains("IAuditor", report.Contracts);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("sample.auditor", StringComparison.Ordinal)
                && e.Message.Contains("IAuditor", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPluginEntryAssembly_WarnsWithPathAndReport()
    {
        var corePath = typeof(IAuditor).Assembly.Location;
        var logger = new CapturingLogger<PluginLoader>();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [corePath],
                Allowlist = ["*"],
                Enabled = ["*"],
            },
            EmptyConfig(),
            logger);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(corePath, StringComparison.Ordinal));
        var report = Assert.Single(loader.GetAssemblyReports());
        Assert.True(report.Found);
        Assert.False(report.Loaded);
        Assert.Equal(PluginSkipReason.NoPluginEntry, report.SkipReason);
    }

    [Fact]
    public void StartupSummary_StaysBounded()
    {
        var statuses = Enumerable.Range(0, 50)
            .Select(i => new PluginDiscoveryStatus(
                $"plugin.{i:000}", $"Plugin {i}", $"/fake/{i}.dll",
                Enabled: true, Allowlisted: true, Loaded: true,
                SkipReason: PluginSkipReason.None, [],
                Contracts: ["IAuditor"]))
            .ToList();

        var summary = PluginStartupSummary.Build(statuses, [], [], maxEntries: 5);

        Assert.Equal(50, summary.Loaded);
        Assert.True(summary.Details.Count <= 5);
        Assert.True(summary.Omitted > 0);
        Assert.Contains("50 loaded", summary.Header, StringComparison.Ordinal);
    }

    [CodeyBoxPlugin("reporting.throwing", "Reporting Throwing Plugin")]
    private sealed class ThrowingInitAuditor : IAuditor, IPluginInitializer
    {
        public string Name => "reporting-throwing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));

        public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated init failure");
    }

    private static PluginInitializationService MakeInitService(
        IPluginLoader loader,
        IServiceProvider services,
        CapturingLogger<PluginInitializationService> logger,
        bool? failOnInitializationError = null)
        => new(
            loader,
            services,
            EmptyConfig(),
            NullLoggerFactory.Instance,
            logger,
            toolProbe: null,
            failOnInitializationError: failOnInitializationError);

    [Fact]
    public async Task InitializationThrowing_IsReported_NotSilent_FatalByDefault()
    {
        var plugin = new LoadedPlugin(
            "reporting.throwing", "Reporting Throwing Plugin", "/fake/throwing.dll",
            [typeof(ThrowingInitAuditor)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(ThrowingInitAuditor));
        var loader = new PluginLoader(
            new PluginOptions(), EmptyConfig(), NullLogger<PluginLoader>.Instance,
            preloaded: [plugin]);
        var logger = new CapturingLogger<PluginInitializationService>();
        await using var provider = services.BuildServiceProvider();
        var init = MakeInitService(loader, provider, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => init.StartAsync(CancellationToken.None));

        Assert.Equal("Simulated init failure", ex.Message);
        // Reported, not silent: error log naming the plugin plus a recorded failure.
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("reporting.throwing", StringComparison.Ordinal));
        var failure = Assert.Single(init.InitializationFailures);
        Assert.Equal("reporting.throwing", failure.PluginId);
    }

    [Fact]
    public async Task InitializationThrowing_NonFatalPolicy_ContinuesAndRecords()
    {
        var plugin = new LoadedPlugin(
            "reporting.throwing", "Reporting Throwing Plugin", "/fake/throwing.dll",
            [typeof(ThrowingInitAuditor)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(ThrowingInitAuditor));
        var loader = new PluginLoader(
            new PluginOptions(), EmptyConfig(), NullLogger<PluginLoader>.Instance,
            preloaded: [plugin]);
        var logger = new CapturingLogger<PluginInitializationService>();
        await using var provider = services.BuildServiceProvider();
        var init = MakeInitService(loader, provider, logger, failOnInitializationError: false);

        // Does not throw under the opt-out policy…
        await init.StartAsync(CancellationToken.None);

        // …but the failure is still recorded and logged, never silent.
        var failure = Assert.Single(init.InitializationFailures);
        Assert.Equal("reporting.throwing", failure.PluginId);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("reporting.throwing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginStatusEndpoint_MatchesDiscovery()
    {
        using var factory = new PluginEndpointFactory();
        using var client = factory.CreateClient();

        var loaded = await factory.Services.GetRequiredService<IPluginLoader>()
            .DiscoverAndLoadAsync(CancellationToken.None);
        var response = await client.GetAsync("/plugins/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PluginStatusPayload>();
        Assert.NotNull(body);

        // The endpoint lists the full loaded set — including the non-auditor
        // credential plugin that GET /plugins deliberately omits.
        Assert.Equal(
            loaded.Select(static p => p.PluginId).Order(StringComparer.Ordinal).ToArray(),
            body!.Loaded.Select(static p => p.PluginId).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(body.Loaded, static p => p.PluginId == "uat.endpoint-credential");
        Assert.Equal(
            loaded.Count,
            body.Discovery.Count(static d => d.Loaded));
        Assert.All(
            body.Assemblies,
            static a => Assert.True(a.Loaded));
    }

    private sealed record PluginStatusPayload(
        List<LoadedEntry> Loaded,
        List<DiscoveryEntry> Discovery,
        List<AssemblyEntry> Assemblies);
    private sealed record LoadedEntry(string PluginId, string DisplayName, string AssemblyPath, List<string> Contracts);
    private sealed record DiscoveryEntry(
        string PluginId, string DisplayName, string AssemblyPath,
        bool Enabled, bool Allowlisted, bool Loaded, string SkipReason,
        List<string> Contracts, string? Detail);
    private sealed record AssemblyEntry(
        string AssemblyPath, bool Found, bool Loaded,
        List<string> PluginIds, List<string> Contracts, string SkipReason, string? Detail);
}
