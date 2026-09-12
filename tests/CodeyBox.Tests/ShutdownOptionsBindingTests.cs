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

    [Fact]
    public void SandboxTeardownTimeout_DefaultsToServiceBudget_WhenShutdownConfigKeyAbsent()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["CodeyBox:StateDatabasePath"] = "state.db",
        });

        Assert.Equal(
            SandboxShutdownTeardownService.DefaultTeardownBudget,
            options.Shutdown.SandboxTeardownTimeout);
    }

    [Fact]
    public void SandboxTeardownTimeout_BindsConfiguredValue()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["CodeyBox:Shutdown:SandboxTeardownTimeout"] = "00:00:45",
        });

        Assert.Equal(TimeSpan.FromSeconds(45), options.Shutdown.SandboxTeardownTimeout);
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
