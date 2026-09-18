using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Startup-report tests: enabled/disabled state and unmet tool requirements
/// are reported at startup, before anything fails inside a sandbox.
/// </summary>
public sealed class PluginStartupReportTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    private sealed class FakeProbe : IPluginToolAvailabilityProbe
    {
        private readonly HashSet<string> _available;
        public FakeProbe(params string[] available) => _available = [.. available];
        public bool IsAvailable(string binaryName) => _available.Contains(binaryName);
    }

    private static PluginLoader DiscoverToolLoader(params string[] enabled)
    {
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [PluginTestHelpers.GetToolSamplePluginAssemblyPath()],
                Allowlist = ["*"],
                Enabled = enabled.ToList(),
            },
            EmptyConfig(),
            NullLogger<PluginLoader>.Instance);
        _ = loader.DiscoverPlugins();
        return loader;
    }

    private static PluginInitializationService MakeInitService(
        PluginLoader loader,
        IPluginToolAvailabilityProbe probe,
        IServiceProvider services)
        => new(
            loader,
            services,
            EmptyConfig(),
            NullLoggerFactory.Instance,
            NullLogger<PluginInitializationService>.Instance,
            toolProbe: probe);

    [Fact]
    public async Task UnmetTool_IsReportedAtStartup()
    {
        var loader = DiscoverToolLoader("sample.tool-auditor");
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var init = MakeInitService(loader, new FakeProbe(), provider);

        await init.StartAsync(CancellationToken.None);

        var unmet = Assert.Single(init.UnmetToolRequirements);
        Assert.Equal("sample.tool-auditor", unmet.PluginId);
        Assert.Equal("codeybox-sample-scan-tool", unmet.Binary);
    }

    [Fact]
    public async Task MetTool_IsNotReported()
    {
        var loader = DiscoverToolLoader("sample.tool-auditor");
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var init = MakeInitService(loader, new FakeProbe("codeybox-sample-scan-tool"), provider);

        await init.StartAsync(CancellationToken.None);

        Assert.Empty(init.UnmetToolRequirements);
    }

    [Fact]
    public async Task DisabledPlugin_ToolsAreNotReported()
    {
        // sample.tool-auditor stays disabled: nothing about its tool may
        // surface, met or not.
        var loader = DiscoverToolLoader("sample.plain-auditor");
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var init = MakeInitService(loader, new FakeProbe(), provider);

        await init.StartAsync(CancellationToken.None);

        Assert.Empty(init.UnmetToolRequirements);
        Assert.DoesNotContain(
            loader.GetDiscoveryStatuses(),
            static s => s.PluginId == "sample.tool-auditor" && s.Loaded);
    }

    [Fact]
    public void DiscoveryStatuses_DistinguishLoadedFromSkipped()
    {
        var loader = DiscoverToolLoader("sample.tool-auditor", "sample.plain-auditor");

        var statuses = loader.GetDiscoveryStatuses();

        var loaded = statuses.Where(static s => s.Loaded).Select(static s => s.PluginId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("sample.tool-auditor", loaded);
        Assert.Contains("sample.plain-auditor", loaded);
        var toolStatus = statuses.First(static s => s.PluginId == "sample.tool-auditor");
        Assert.Single(toolStatus.RequiredTools ?? []);
    }

    [Fact]
    public void InvalidToolDeclaration_FailsClosedWithStatus()
    {
        var loader = DiscoverToolLoader("sample.evil-tool");

        var plugins = loader.DiscoverPlugins();

        Assert.Empty(plugins);
        Assert.Empty(loader.GetEnabledPluginTools());
        var status = Assert.Single(loader.GetDiscoveryStatuses(), s => s.PluginId == "sample.evil-tool");
        Assert.False(status.Loaded);
        Assert.Equal(PluginSkipReason.InvalidToolDeclaration, status.SkipReason);
        Assert.Empty(status.RequiredTools ?? []);
    }

    [Fact]
    public void OptionsWatcher_DetectsEnabledSetChange()
    {
        var before = PluginOptionsChangeWatcher.EnabledSet(new PluginOptions { Enabled = ["a", "b"] });
        var same = PluginOptionsChangeWatcher.EnabledSet(new PluginOptions { Enabled = ["b", "A"] });
        var changed = PluginOptionsChangeWatcher.EnabledSet(new PluginOptions { Enabled = ["a", "c"] });

        Assert.True(PluginOptionsChangeWatcher.EnabledSetsEqual(before, same));
        Assert.False(PluginOptionsChangeWatcher.EnabledSetsEqual(before, changed));
    }

    [Fact]
    public void PathProbe_FindsExecutables_RejectsUnsafeNames()
    {
        if (OperatingSystem.IsWindows())
        Skip.If(OperatingSystem.IsWindows(), "exercises Unix execute-bit semantics");
        var scratch = Path.Combine(Path.GetTempPath(), "codeybox-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var exe = Path.Combine(scratch, "probe-tool");
            File.WriteAllText(exe, "#!/bin/sh\n");
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var nonExe = Path.Combine(scratch, "probe-data");
            File.WriteAllText(nonExe, "data");

            var probe = new PathPluginToolAvailabilityProbe(() => scratch);

            Assert.True(probe.IsAvailable("probe-tool"));
            Assert.False(probe.IsAvailable("probe-data"));
            Assert.False(probe.IsAvailable("missing-tool"));
            Assert.False(probe.IsAvailable("../probe-tool"));
            Assert.False(probe.IsAvailable("a/b"));
            Assert.False(probe.IsAvailable(""));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
