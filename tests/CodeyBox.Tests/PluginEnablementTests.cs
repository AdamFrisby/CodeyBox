using System.Reflection;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Enablement-gate tests: a disabled plugin is not loaded — its assembly is
/// never handed to a load context, its types are never registered, its
/// instances are never constructed. A newly added plugin is off until the
/// operator enables it.
/// </summary>
public sealed class PluginEnablementTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private static PluginLoader MakeLoader(PluginOptions opts, IPluginAssemblyLoader? assemblyLoader = null)
        => new(opts, EmptyConfig(), NullLogger<PluginLoader>.Instance, assemblyLoader: assemblyLoader);

    /// <summary>Load-context gate fake: records every load instead of creating an ALC.</summary>
    private sealed class RecordingPluginAssemblyLoader : IPluginAssemblyLoader
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<string> LoadedPaths => Calls;

        public Assembly Load(string absolutePath)
        {
            Calls.Add(absolutePath);
            throw new InvalidOperationException($"Assembly load must not happen in this test: {absolutePath}");
        }
    }

    [Fact]
    public void DisabledPlugin_AssemblyIsNeverLoaded()
    {
        // Explicitly empty Enabled: everything is disabled even with a
        // wildcard allowlist. The recording loader proves at the load-context
        // level that no AssemblyLoadContext creation is ever attempted.
        var toolPath = PluginTestHelpers.GetToolSamplePluginAssemblyPath();
        var recorder = new RecordingPluginAssemblyLoader();
        var loader = MakeLoader(
            new PluginOptions
            {
                AssemblyPaths = [toolPath],
                Allowlist = ["*"],
                Enabled = [],
            },
            recorder);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(recorder.Calls);
        Assert.Empty(plugins);
        Assert.All(
            loader.GetDiscoveryStatuses(),
            static s => Assert.Equal(PluginSkipReason.Disabled, s.SkipReason));
    }

    [Fact]
    public void AllowlistedButDisabledPlugin_StaysUnloaded()
    {
        // Enablement is a separate axis from the allowlist: allowlisted but
        // disabled stays unloaded.
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var recorder = new RecordingPluginAssemblyLoader();
        var loader = MakeLoader(
            new PluginOptions
            {
                AssemblyPaths = [samplePath],
                Allowlist = ["sample.auditor"],
                Enabled = [],
            },
            recorder);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(recorder.Calls);
        Assert.Empty(plugins);
        var status = Assert.Single(loader.GetDiscoveryStatuses(), s => s.PluginId == "sample.auditor");
        Assert.True(status.Allowlisted);
        Assert.False(status.Enabled);
        Assert.False(status.Loaded);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);
    }

    [Fact]
    public void EnabledButNotAllowlistedPlugin_StaysUnloaded()
    {
        var samplePath = PluginTestHelpers.GetSamplePluginAssemblyPath();
        var recorder = new RecordingPluginAssemblyLoader();
        var loader = MakeLoader(
            new PluginOptions
            {
                AssemblyPaths = [samplePath],
                Allowlist = [],
                Enabled = ["sample.auditor"],
            },
            recorder);

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(recorder.Calls);
        Assert.Empty(plugins);
        var status = Assert.Single(loader.GetDiscoveryStatuses(), s => s.PluginId == "sample.auditor");
        Assert.True(status.Enabled);
        Assert.False(status.Allowlisted);
        Assert.Equal(PluginSkipReason.NotAllowlisted, status.SkipReason);
    }

    [Fact]
    public void NewPlugin_IsOffUntilEnabled()
    {
        // The tool fixture IDs are not in the default enabled set, so a
        // wildcard allowlist alone must not load them.
        var toolPath = PluginTestHelpers.GetToolSamplePluginAssemblyPath();
        var defaulted = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [toolPath],
            Allowlist = ["*"],
        });

        Assert.Empty(defaulted.DiscoverPlugins());
        Assert.DoesNotContain(
            new PluginOptions().Enabled,
            id => string.Equals(id, "sample.tool-auditor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitlyEnabledPlugin_Loads()
    {
        var toolPath = PluginTestHelpers.GetToolSamplePluginAssemblyPath();
        var loader = MakeLoader(new PluginOptions
        {
            AssemblyPaths = [toolPath],
            Allowlist = ["*"],
            Enabled = ["sample.tool-auditor", "sample.plain-auditor"],
        });

        var plugins = loader.DiscoverPlugins();

        var ids = plugins.Select(static p => p.PluginId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("sample.tool-auditor", ids);
        Assert.Contains("sample.plain-auditor", ids);
        // sample.evil-tool is neither enabled nor valid: absent either way.
        Assert.DoesNotContain("sample.evil-tool", ids);
    }

    [Fact]
    public void MixedAssembly_LoadsAssemblyForEnabledSibling_ButNotDisabledPlugin()
    {
        // One assembly, one enabled plugin: the file must load (the enabled
        // sibling needs it) but the disabled plugin contributes no type.
        var toolPath = PluginTestHelpers.GetToolSamplePluginAssemblyPath();
        var realLoader = new PluginAssemblyLoadContextLoader();
        var loader = MakeLoader(
            new PluginOptions
            {
                AssemblyPaths = [toolPath],
                Allowlist = ["*"],
                Enabled = ["sample.plain-auditor"],
            },
            realLoader);

        var plugins = loader.DiscoverPlugins();

        Assert.Equal([toolPath], realLoader.LoadedPaths);
        var plugin = Assert.Single(plugins);
        Assert.Equal("sample.plain-auditor", plugin.PluginId);
        Assert.DoesNotContain(plugin.RegisteredTypes, t => t.Name.Contains("Tool", StringComparison.Ordinal));
        var disabled = loader.GetDiscoveryStatuses().First(s => s.PluginId == "sample.tool-auditor");
        Assert.Equal(PluginSkipReason.Disabled, disabled.SkipReason);
    }

    [Theory]
    [InlineData("codeybox.file-size-limits", true)]
    [InlineData("codeybox.statistics", true)]
    [InlineData("codeybox.quota-reset-notifier", true)]
    [InlineData("codeybox.opencode-go-quota", true)]
    [InlineData("codeybox.dotnet-test-runner", false)]
    [InlineData("codeybox.pytest-test-runner", false)]
    [InlineData("some.future-auditor", false)]
    public void DefaultEnabledSet_CoversBundledPluginsOnly(string pluginId, bool expected)
    {
        Assert.Equal(expected, new PluginOptions().IsEnabled(pluginId));
    }

    [Fact]
    public void IsEnabled_WildcardEnablesAll_EmptyDisablesAll_CaseInsensitive()
    {
        Assert.True(new PluginOptions { Enabled = ["*"] }.IsEnabled("anything.at-all"));
        Assert.False(new PluginOptions { Enabled = [] }.IsEnabled("codeybox.statistics"));
        Assert.True(new PluginOptions { Enabled = ["CodeyBox.Statistics"] }.IsEnabled("codeybox.statistics"));
        Assert.False(new PluginOptions().IsEnabled(""));
        Assert.False(new PluginOptions().IsEnabled("   "));
    }
}
