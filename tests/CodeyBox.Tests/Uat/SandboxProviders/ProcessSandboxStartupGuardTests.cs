using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT startup-guard coverage for the unsafe process sandbox provider.
/// Plan anchor: docs/uat/00-plan.md#process-sandbox-provider---unsafe-local-development-runner
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ProcessSandboxStartupGuardTests
{
    [Fact]
    public void NonDevelopmentWithoutExplicitProvider_RefusesToSelectProcessDefault()
    {
        using var env = ConfigureRequiredProductionChangelogSecret();
        using var factory = new SandboxProviderApiFactory(environment: "Production");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<ISandboxProvider>());

        Assert.Contains("SandboxProvider must be set", ex.Message);
        Assert.Contains("non-Development", ex.Message);
    }

    [Fact]
    public void NonDevelopmentProcessProviderWithoutUnsafeOptIn_Throws()
    {
        using var env = ConfigureRequiredProductionChangelogSecret();
        using var factory = new SandboxProviderApiFactory(
            environment: "Production",
            configuration: new Dictionary<string, string?>
            {
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "false",
            });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<ISandboxProvider>());

        Assert.Contains("UNSAFE outside Development", ex.Message);
        Assert.Contains("DangerouslyAllowProcessSandbox", ex.Message);
    }

    [Fact]
    public void NonDevelopmentProcessProviderWithUnsafeOptIn_ResolvesProcessProvider()
    {
        using var env = ConfigureRequiredProductionChangelogSecret();
        using var factory = new SandboxProviderApiFactory(
            environment: "Production",
            configuration: new Dictionary<string, string?>
            {
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            });

        var provider = factory.Services.GetRequiredService<ISandboxProvider>();
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(provider);

        Assert.Equal("process", provider.Name);
        Assert.True(admission.MaxConcurrentSandboxes >= 1);
    }

    private static IDisposable ConfigureRequiredProductionChangelogSecret()
    {
        const string configKey = "CodeyBox__Changelog__GitHubWebhookSecretEnvVar";
        const string secretKey = "CODEYBOX_CHANGELOG_SECRET_UAT";
        var oldConfig = Environment.GetEnvironmentVariable(configKey);
        var oldSecret = Environment.GetEnvironmentVariable(secretKey);
        Environment.SetEnvironmentVariable(configKey, secretKey);
        Environment.SetEnvironmentVariable(secretKey, "test-secret");
        return new EnvScope(() =>
        {
            Environment.SetEnvironmentVariable(configKey, oldConfig);
            Environment.SetEnvironmentVariable(secretKey, oldSecret);
        });
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Action _restore;
        public EnvScope(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}
