using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Process;
using CodeyBox.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests.Uat.ProjectsAndConfiguration;

/// <summary>
/// UAT coverage for "API configuration and startup validation - Binds options
/// and refuses unsafe or inconsistent settings".
/// Plan anchor: docs/uat/00-plan.md#projects-and-configuration
/// </summary>
public sealed class ApiConfigurationStartupValidationUatTests
{
    [Fact]
    public void WorkerPoolConfigBindsAndLegacyConcurrencyOverridesNewWorkerPool()
    {
        var logger = new UatLogCapture();

        var options = OrchestratorOptionsFactory.Build(
            legacyConcurrency: 3,
            workerPool: new WorkerPoolOptions
            {
                MaxConcurrentWorkers = 8,
                MinSpawnInterval = TimeSpan.FromMilliseconds(250),
            },
            log: logger);

        Assert.Equal(3, options.MaxConcurrentWorkers);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.MinSpawnInterval);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("CodeyBox:Concurrency", warning.Message);
        Assert.Contains("Ignoring WorkerPool:MaxConcurrentWorkers=8", warning.Message);
    }

    [Theory]
    [InlineData(null, "grpc", "OtlpEndpoint")]
    [InlineData("not-a-url", "grpc", "OtlpEndpoint")]
    [InlineData("http://localhost:4317", "bad-protocol", "ExportProtocol")]
    public void OtelEnabledRejectsMissingEndpointInvalidUrlAndInvalidProtocol(
        string? endpoint,
        string protocol,
        string expectedMessage)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OtelOptions.Validate(new OtelOptions
            {
                Enabled = true,
                OtlpEndpoint = endpoint,
                ExportProtocol = protocol,
            }));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("201", "outside the valid range")]
    [InlineData("-1", "outside the valid range")]
    public void AgentClassQualityScoreValidationRejectsOutOfRangeValues(
        string qualityScore,
        string expectedMessage)
    {
        using var factory = new ProjectsAndConfigurationApiFactory(configuration: new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = qualityScore,
        }, projects: new InMemoryProjectRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void AgentScoreModifierValidationRejectsNonTiebreakerMagnitude()
    {
        using var factory = new ProjectsAndConfigurationApiFactory(configuration: new Dictionary<string, string?>
        {
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Agent"] = "claude",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Modifier"] = "6",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:Days:0"] = "Mon",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:StartUtc"] = "14:00",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:EndUtc"] = "22:00",
        }, projects: new InMemoryProjectRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains("absolute value must be", ex.Message);
    }
}

[Collection("GlobalSerilog")]
public sealed class SandboxStartupConfigurationUatTests
{
    [Fact]
    public void ProductionWithoutSandboxProviderFailsBeforeSelectingDevelopmentDefault()
    {
        using var env = ConfigureRequiredProductionApiSecrets();
        using var factory = new ProjectsAndConfigurationApiFactory(
            environment: "Production",
            disableAuth: false,
            configuration: new Dictionary<string, string?>
            {
                ["CodeyBox:SandboxProvider"] = null,
            },
            projects: new InMemoryProjectRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<ISandboxProvider>());

        Assert.Contains("SandboxProvider must be set", ex.Message);
        Assert.Contains("non-Development", ex.Message);
    }

    [Fact]
    public void ProductionProcessSandboxRequiresExplicitUnsafeOptIn()
    {
        using var env = ConfigureRequiredProductionApiSecrets();
        using var factory = new ProjectsAndConfigurationApiFactory(
            environment: "Production",
            disableAuth: false,
            configuration: new Dictionary<string, string?>
            {
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "false",
            },
            projects: new InMemoryProjectRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<ISandboxProvider>());

        Assert.Contains("UNSAFE outside Development", ex.Message);
        Assert.Contains("DangerouslyAllowProcessSandbox", ex.Message);
    }

    [Fact]
    public void ProductionProcessSandboxWithUnsafeOptInResolvesProvider()
    {
        using var env = ConfigureRequiredProductionApiSecrets();
        using var factory = new ProjectsAndConfigurationApiFactory(
            environment: "Production",
            disableAuth: false,
            configuration: new Dictionary<string, string?>
            {
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            },
            projects: new InMemoryProjectRepository());

        var provider = factory.Services.GetRequiredService<ISandboxProvider>();

        Assert.IsType<ProcessSandboxProvider>(provider);
    }

    private static IDisposable ConfigureRequiredProductionApiSecrets()
        => new CompositeDisposable(
            new EnvironmentVariableScope("CODEYBOX_API_KEY", HealthCheckAndApiAuthUatTests.ValidToken),
            new EnvironmentVariableScope("CodeyBox__Changelog__GitHubWebhookSecretEnvVar", "CODEYBOX_CHANGELOG_SECRET_UAT"),
            new EnvironmentVariableScope("CODEYBOX_CHANGELOG_SECRET_UAT", "uat-secret"));

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _items;

        public CompositeDisposable(params IDisposable[] items) => _items = items;

        public void Dispose()
        {
            foreach (var item in _items.Reverse())
                item.Dispose();
        }
    }
}

/// <summary>
/// UAT coverage for "API health check endpoint - Exposes an anonymous liveness probe".
/// Plan anchor: docs/uat/00-plan.md#projects-and-configuration
/// </summary>
[Collection("GlobalSerilog")]
public sealed class HealthCheckAndApiAuthUatTests
{
    internal const string ValidToken = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task HealthzIsAnonymousWithStableOkBodyEvenWhenAuthIsEnabled()
    {
        using var env = new EnvironmentVariableScope("CODEYBOX_API_KEY", ValidToken);
        using var factory = AuthenticatedFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthzResponse>();
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task HealthzPrefixAllowsExtraSegmentsButProtectedEndpointsStillRequireBearerToken()
    {
        using var env = new EnvironmentVariableScope("CODEYBOX_API_KEY", ValidToken);
        using var factory = AuthenticatedFactory();
        using var client = factory.CreateClient();

        var healthWithSegment = await client.GetAsync("/healthz/ready");
        var protectedWithoutToken = await client.GetAsync("/projects");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);
        var protectedWithToken = await client.GetAsync("/projects");

        Assert.Equal(HttpStatusCode.NotFound, healthWithSegment.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedWithoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, protectedWithToken.StatusCode);
    }

    [Fact]
    public async Task InvalidBearerTokenIsRejectedForProtectedEndpoint()
    {
        using var env = new EnvironmentVariableScope("CODEYBOX_API_KEY", ValidToken);
        using var factory = AuthenticatedFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.GetAsync("/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    private static ProjectsAndConfigurationApiFactory AuthenticatedFactory()
        => new(
            disableAuth: false,
            projects: new InMemoryProjectRepository(
                ProjectsAndConfigurationFixtures.Project(
                    "auth-project",
                    "Auth Project",
                    "https://github.com/example/auth-project.git")));

    private sealed record HealthzResponse(string Status);
}
