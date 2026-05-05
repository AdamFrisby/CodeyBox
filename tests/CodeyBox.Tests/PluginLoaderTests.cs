using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class PluginLoaderTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private static PluginLoader MakeLoader(PluginOptions opts)
        => new(opts, EmptyConfig(), NullLogger<PluginLoader>.Instance);

    // ── Discovery ─────────────────────────────────────────────────────────────

    [Fact]
    public void DiscoverPlugins_WithAllowlistWildcard_LoadsBothSampleTypes()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["*"],
        });

        var plugins = loader.DiscoverPlugins();

        Assert.Equal(2, plugins.Count);
        var ids = plugins.Select(p => p.PluginId).ToHashSet();
        Assert.Contains("sample.auditor", ids);
        Assert.Contains("sample.blocked-auditor", ids);
    }

    [Fact]
    public void DiscoverPlugins_EmptyAllowlist_LoadsNothing()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = [],   // empty = deny all
        });

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
    }

    [Fact]
    public void DiscoverPlugins_Allowlist_FiltersToSpecificId()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["sample.auditor"],   // only the primary auditor
        });

        var plugins = loader.DiscoverPlugins();

        Assert.Single(plugins);
        Assert.Equal("sample.auditor", plugins[0].PluginId);
    }

    [Fact]
    public void DiscoverPlugins_NonExistentPath_SkipsGracefully()
    {
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = ["/does/not/exist.dll"],
            Allowlist = ["*"],
        });

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
    }

    [Fact]
    public void DiscoverPlugins_MinHostApiVersionTooHigh_SkipsPlugin()
    {
        // SampleFutureAuditor declares minHostApiVersion = "99.0". Even with a
        // wildcard allowlist it must be skipped by the loader's version check.
        // The other two plugins (v1.0) must still load, proving the rejection is
        // selective and not caused by an allowlist or path error.
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [samplePath],
            Allowlist = ["*"],   // allow all IDs so rejection is purely from version mismatch
        });

        var plugins = loader.DiscoverPlugins();

        var ids = plugins.Select(p => p.PluginId).ToHashSet();
        Assert.DoesNotContain("sample.future-auditor", ids);
        Assert.Equal(2, plugins.Count);   // sample.auditor + sample.blocked-auditor only
    }

    // ── DI Registration ───────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterPlugins_LoadedPlugin_RunAsyncExecutesEndToEnd()
    {
        // Integration test: load the sample plugin through the full stack
        // (ALC isolation → DI registration → method invocation) and verify
        // that RunAsync executes correctly on the resolved instance.
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

        var result = await auditor.RunAsync(
            sandbox: null!,
            workingDirectory: "/tmp",
            context: context);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task RegisterPlugins_RegistersTypeUnderIAuditor()
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
        var auditors = provider.GetServices<IAuditor>().ToList();

        Assert.Single(auditors);
        Assert.Equal("sample-auditor", auditors[0].Name);
    }

    [Fact]
    public async Task RegisterPlugins_BothPluginsAllowed_RegistersTwoAuditors()
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

        Assert.Equal(2, auditors.Count);
    }

    [Fact]
    public void RegisterPlugins_EmptyPluginList_RegistersNothing()
    {
        var loader = MakeLoader(new PluginOptions());
        var services = new ServiceCollection();
        loader.RegisterPlugins(services, []);

        // No IAuditor should be resolvable (not even the default None case)
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IAuditor>());
    }

    // ── IPluginLoader (runtime introspection) ─────────────────────────────────

    [Fact]
    public async Task DiscoverAndLoadAsync_ReturnsCachedResult()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions { AssemblyPaths = [samplePath], Allowlist = ["*"] },
            EmptyConfig(),
            NullLogger<PluginLoader>.Instance);

        var first = await loader.DiscoverAndLoadAsync(CancellationToken.None);
        var second = await loader.DiscoverAndLoadAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task DiscoverAndLoadAsync_WithPreloaded_ReturnsThat()
    {
        var preloaded = (IReadOnlyList<LoadedPlugin>)[
            new LoadedPlugin("preloaded.plugin", "Pre-loaded", "/fake.dll", [])
        ];

        var loader = new PluginLoader(
            new PluginOptions(),
            EmptyConfig(),
            NullLogger<PluginLoader>.Instance,
            preloaded: preloaded);

        var result = await loader.DiscoverAndLoadAsync(CancellationToken.None);

        Assert.Same(preloaded, result);
    }
}
