using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class PluginConfigurationScopingTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ScopedConfig_ExposesOnlyPluginSection()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:Plugins:my-plugin:TimeoutSeconds"] = "42",
            ["CodeyBox:Plugins:my-plugin:DryRun"] = "true",
            ["CodeyBox:Plugins:other-plugin:SomeKey"] = "should-not-be-visible",
            ["CodeyBox:GlobalSetting"] = "irrelevant",
        });

        var host = new PluginHost("my-plugin", NullLoggerFactory.Instance, config);

        Assert.Equal("42", host.ScopedConfig["TimeoutSeconds"]);
        Assert.Equal("true", host.ScopedConfig["DryRun"]);
        Assert.Null(host.ScopedConfig["SomeKey"]);          // other plugin's key
        Assert.Null(host.ScopedConfig["GlobalSetting"]);    // top-level key
    }

    [Fact]
    public void ScopedConfig_EmptySection_ReturnsNoValues()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:Plugins:other-plugin:Key"] = "value",
        });

        var host = new PluginHost("my-plugin", NullLoggerFactory.Instance, config);

        Assert.Null(host.ScopedConfig["Key"]);
        Assert.False(host.ScopedConfig.GetChildren().Any());
    }

    [Fact]
    public void PluginContext_ConvenienceAccessors_DelegateToHost()
    {
        var config = BuildConfig(new()
        {
            ["CodeyBox:Plugins:test-plugin:Foo"] = "bar",
        });

        var host = new PluginHost("test-plugin", NullLoggerFactory.Instance, config);
        var context = new PluginContext(
            HostApiVersion: CodeyBoxApiVersion.Current,
            PluginId: "test-plugin",
            PluginDisplayName: "Test Plugin",
            Host: host);

        Assert.Same(host.Logger, context.Logger);
        Assert.Same(host.ScopedConfig, context.ScopedConfig);
        Assert.Equal("bar", context.ScopedConfig["Foo"]);
    }

    [Fact]
    public void PluginHost_Logger_IsNamedAfterPlugin()
    {
        // Verify the logger name contains the plugin ID. We test via a capturing
        // logger factory; the exact name is Plugin:<id>.
        var factory = new CapturingLoggerFactory();
        _ = new PluginHost("acme.my-plugin", factory, BuildConfig(new()));

        Assert.Contains("Plugin:acme.my-plugin", factory.CreatedLoggerNames);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> CreatedLoggerNames { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            CreatedLoggerNames.Add(categoryName);
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
