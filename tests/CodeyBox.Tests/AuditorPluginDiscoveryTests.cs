using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that IAuditor plugins discovered via the plugin loader are
/// registered in the DI container and resolvable as <c>IEnumerable&lt;IAuditor&gt;</c>.
/// Uses the same sample assembly as <see cref="PluginLoaderTests"/> so no
/// extra assembly is required.
/// </summary>
public sealed class AuditorPluginDiscoveryTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private static PluginLoader MakeLoader(PluginOptions opts)
        => new(opts, EmptyConfig(), NullLogger<PluginLoader>.Instance);

    [Fact]
    public async Task AuditorPlugin_AppearsInDiCollection_AfterRegistration()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["sample.auditor"],
        });

        var plugins = loader.DiscoverPlugins();
        Assert.Single(plugins);

        var services = new ServiceCollection();
        loader.RegisterPlugins(services, plugins);

        await using var provider = services.BuildServiceProvider();
        var auditors = provider.GetServices<IAuditor>().ToList();

        Assert.Single(auditors);
        Assert.Equal("sample-auditor", auditors[0].Name);
    }

    [Fact]
    public async Task MultipleAuditorPlugins_AllAppearInDiCollection()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["*"],
        });

        var plugins = loader.DiscoverPlugins();

        var services = new ServiceCollection();
        loader.RegisterPlugins(services, plugins);

        await using var provider = services.BuildServiceProvider();
        var auditors = provider.GetServices<IAuditor>().ToList();

        // sample.auditor + sample.blocked-auditor both implement IAuditor
        Assert.True(auditors.Count >= 2, $"Expected at least 2 auditors, got {auditors.Count}");
        var names = auditors.Select(a => a.Name).ToHashSet();
        Assert.Contains("sample-auditor", names);
        Assert.Contains("sample-blocked-auditor", names);
    }

    [Fact]
    public async Task NoPlugins_DiCollectionIsEmpty()
    {
        var loader = MakeLoader(new PluginOptions { Allowlist = ["*"] });
        var plugins = loader.DiscoverPlugins();

        var services = new ServiceCollection();
        loader.RegisterPlugins(services, plugins);

        await using var provider = services.BuildServiceProvider();
        var auditors = provider.GetServices<IAuditor>().ToList();

        Assert.Empty(auditors);
    }

    [Fact]
    public async Task AuditorPlugin_RunAsync_ExecutesSuccessfully()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["sample.auditor"],
        });

        var plugins = loader.DiscoverPlugins();
        var services = new ServiceCollection();
        loader.RegisterPlugins(services, plugins);

        await using var provider = services.BuildServiceProvider();
        var auditor = provider.GetRequiredService<IAuditor>();

        var context = new AuditContext(
            WorkItemId: WorkItemId.New(),
            WorkBranch: "feat/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "test prompt");

        var result = await auditor.RunAsync(sandbox: null!, workingDirectory: "/tmp", context: context);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }
}
