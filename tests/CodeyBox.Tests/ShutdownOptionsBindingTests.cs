using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class ShutdownOptionsBindingTests
{
    [Fact]
    public void SandboxTeardownMode_DefaultsToStop_WhenShutdownConfigKeyAbsent()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["CodeyBox:StateDatabasePath"] = "state.db",
        });

        Assert.Equal(SandboxTeardownMode.Stop, options.Shutdown.SandboxTeardownMode);
    }

    [Theory]
    [InlineData("Suspend", SandboxTeardownMode.Suspend)]
    [InlineData("Stop", SandboxTeardownMode.Stop)]
    [InlineData("Dispose", SandboxTeardownMode.Dispose)]
    public void SandboxTeardownMode_BindsConfiguredMode(
        string configValue,
        SandboxTeardownMode expected)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["CodeyBox:Shutdown:SandboxTeardownMode"] = configValue,
        });

        Assert.Equal(expected, options.Shutdown.SandboxTeardownMode);
    }

    private static CodeyBoxOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>()
            .Bind(config.GetSection("CodeyBox"));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
    }
}
