using CodeyBox.Api;
using CodeyBox.Audit;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the mutation-gate host composition (<see
/// cref="MutationGateRegistration"/>): default-disabled wiring selects the
/// inert null runner, opting into <c>CodeyBox:Mutation:Stryker:Enabled</c>
/// selects the real Stryker runner, and the auditor resolves in both modes.
/// </summary>
public sealed class MutationGateRegistrationTests
{
    private static IServiceProvider BuildProvider(Action<ConfigurationManager> configure)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        var config = new ConfigurationManager();
        configure(config);
        MutationGateRegistration.Configure(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DefaultComposition_SelectsNullRunner_GateDisabled()
    {
        var provider = BuildProvider(_ => { });

        var options = provider.GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>();
        Assert.False(options.CurrentValue.Enabled);
        Assert.False(options.CurrentValue.Stryker.Enabled);

        var selected = MutationGateRegistration.SelectRunner(provider, options.CurrentValue);
        Assert.IsType<NullMutationRunner>(selected);
        // The default IMutationRunner registration stays the null runner even
        // when Stryker is enabled, so operator replacements keep working.
        Assert.IsType<NullMutationRunner>(provider.GetRequiredService<IMutationRunner>());
        Assert.IsType<MutationTestingAuditor>(provider.GetServices<IAuditor>().Single());
    }

    [Fact]
    public void StrykerEnabled_SelectsRealRunner()
    {
        var provider = BuildProvider(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Mutation:Enabled"] = "true",
                ["CodeyBox:Mutation:Stryker:Enabled"] = "true",
            }));

        var options = provider.GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>();
        Assert.True(options.CurrentValue.Stryker.Enabled);

        var selected = MutationGateRegistration.SelectRunner(provider, options.CurrentValue);
        Assert.IsType<StrykerMutationRunner>(selected);
        Assert.IsType<NullMutationRunner>(provider.GetRequiredService<IMutationRunner>());
        Assert.IsType<MutationTestingAuditor>(provider.GetServices<IAuditor>().Single());
    }

    [Fact]
    public void StrykerSection_BindsExpectedKnobs()
    {
        var provider = BuildProvider(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Mutation:Stryker:ExpectedVersion"] = "4.16.0",
                ["CodeyBox:Mutation:Stryker:Concurrency"] = "4",
                ["CodeyBox:Mutation:Stryker:MutationLevel"] = "Advanced",
                ["CodeyBox:Mutation:Stryker:MaxProjectsPerRun"] = "3",
            }));

        var stryker = provider
            .GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>()
            .CurrentValue.Stryker;

        Assert.False(stryker.Enabled);
        Assert.Equal("4.16.0", stryker.ExpectedVersion);
        Assert.Equal(4, stryker.Concurrency);
        Assert.Equal("Advanced", stryker.MutationLevel);
        Assert.Equal(3, stryker.MaxProjectsPerRun);
        Assert.Empty(stryker.Validate());
    }
}
