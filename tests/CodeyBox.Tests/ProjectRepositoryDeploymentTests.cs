using System.Diagnostics.CodeAnalysis;
using CodeyBox.Core;
using CodeyBox.Deployment;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises <c>ProjectRepository.ResolveDeployment</c> — the deployment-recipe
/// binding + driver-validation handoff invoked from <c>Build()</c>. Three
/// branches matter: unknown Kind raises a registry-citing error, driver
/// validation failures are wrapped with the project id, and the happy path
/// surfaces a fully populated <c>Project.Deployment</c>.
/// </summary>
public sealed class ProjectRepositoryDeploymentTests
{
    private static DeploymentRecipeConfig WebAppRecipeCfg(string kind = DeploymentKinds.WebApp) => new()
    {
        Kind = kind,
        ImageReference = "ubuntu-22.04",
        RunCommand = "nohup ./server &",
        Ports = [8080],
        HealthEndpoint = "/healthz",
    };

    private static ProjectsOptions OptionsWithDeployment(DeploymentRecipeConfig deployment) => new()
    {
        Projects =
        [
            new ProjectConfig
            {
                Id = "alpha",
                RepositoryUrl = "https://example.com/x.git",
                Deployment = deployment,
            },
        ],
    };

    [Fact]
    public async Task NoRegistry_BindsRecipeWithoutDriverValidation()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Deployment = WebAppRecipeCfg(),
                },
            ],
        };
        // No deploymentDrivers passed — the bind succeeds; driver validation is skipped.
        var repo = new ProjectRepository(Options.Create(opts));
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.NotNull(p!.Deployment);
        Assert.Equal(DeploymentKinds.WebApp, p.Deployment!.Kind);
        Assert.Equal("nohup ./server &", p.Deployment.RunCommand);
    }

    [Fact]
    public async Task RegistryAndHappyPath_PopulatesProjectDeployment()
    {
        var driver = new RecordingValidatingDriver(DeploymentKinds.WebApp);
        var registry = new DeploymentDriverRegistry([driver]);
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Deployment = WebAppRecipeCfg(),
                },
            ],
        };

        var repo = new ProjectRepository(
            Options.Create(opts),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry);

        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p);
        Assert.NotNull(p!.Deployment);
        Assert.Equal(DeploymentKinds.WebApp, p.Deployment!.Kind);
        Assert.Equal("ubuntu-22.04", p.Deployment.ImageReference);
        Assert.Equal(1, driver.ValidateCount);
    }

    [Fact]
    public void UnknownKind_ThrowsListingAvailableKinds()
    {
        var driver = new RecordingValidatingDriver(DeploymentKinds.WebApp);
        var registry = new DeploymentDriverRegistry([driver]);
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Deployment = WebAppRecipeCfg(kind: "no-such-kind"),
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(
            Options.Create(opts),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry));

        Assert.Contains("Project 'alpha'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no-such-kind", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DeploymentKinds.WebApp, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverValidationArgumentException_IsWrappedWithProjectAndKind()
    {
        var driver = new RecordingValidatingDriver(
            DeploymentKinds.WebApp,
            onValidate: _ => throw new ArgumentException("RunCommand must include nohup", "recipe"));
        var registry = new DeploymentDriverRegistry([driver]);
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Deployment = WebAppRecipeCfg(),
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(
            Options.Create(opts),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry));

        Assert.Contains("Project 'alpha'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"kind '{DeploymentKinds.WebApp}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("failed driver validation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RunCommand must include nohup", ex.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void DriverValidationInvalidOperationException_IsWrappedWithProjectAndKind()
    {
        var driver = new RecordingValidatingDriver(
            DeploymentKinds.WebApp,
            onValidate: _ => throw new InvalidOperationException("recipe is not consistent"));
        var registry = new DeploymentDriverRegistry([driver]);
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    Deployment = WebAppRecipeCfg(),
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(
            Options.Create(opts),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry));

        Assert.Contains("Project 'alpha'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("failed driver validation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("recipe is not consistent", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task HotReload_ValidDeploymentEdit_UpdatesSnapshot()
    {
        var registry = new DeploymentDriverRegistry([new WebAppDeploymentDriver()]);
        var monitor = new TestProjectsOptionsMonitor(OptionsWithDeployment(WebAppRecipeCfg()));
        using var repo = new ProjectRepository(
            monitor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry);

        var updated = WebAppRecipeCfg();
        updated.Ports = [9090];
        updated.HealthEndpoint = "/ready";

        monitor.Push(OptionsWithDeployment(updated));

        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p?.Deployment);
        Assert.Equal(new[] { 9090 }, p!.Deployment!.Ports);
        Assert.Equal("/ready", p.Deployment.HealthEndpoint);
    }

    [Fact]
    public async Task HotReload_InvalidDeploymentEdit_PreservesPreviousSnapshot()
    {
        var registry = new DeploymentDriverRegistry([new WebAppDeploymentDriver()]);
        var monitor = new TestProjectsOptionsMonitor(OptionsWithDeployment(WebAppRecipeCfg()));
        using var repo = new ProjectRepository(
            monitor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRepository>.Instance,
            presetCatalogOptions: null,
            knobRegistry: null,
            deploymentDrivers: registry);

        var invalid = WebAppRecipeCfg();
        invalid.Ports = [];

        monitor.Push(OptionsWithDeployment(invalid));

        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(p?.Deployment);
        Assert.Equal(new[] { 8080 }, p!.Deployment!.Ports);
        Assert.Equal("/healthz", p.Deployment.HealthEndpoint);
    }

    /// <summary>
    /// Stub driver that records validation calls and lets the test choose
    /// whether validation should throw. Does NOT participate in deploy — the
    /// repository's validation path never invokes <see cref="DeployAsync"/>.
    /// </summary>
    private sealed class RecordingValidatingDriver : IDeploymentDriver
    {
        private readonly Action<DeploymentRecipe>? _onValidate;

        public RecordingValidatingDriver(string kind, Action<DeploymentRecipe>? onValidate = null)
        {
            Kind = kind;
            _onValidate = onValidate;
        }

        public string Kind { get; }
        public int ValidateCount { get; private set; }

        public void ValidateRecipe(DeploymentRecipe recipe)
        {
            ValidateCount++;
            _onValidate?.Invoke(recipe);
        }

        [ExcludeFromCodeCoverage]
        public Task<IDeploymentHandle> DeployAsync(
            DeploymentRecipe recipe, DeploymentContext context, CancellationToken ct = default)
            => throw new NotSupportedException("Test driver does not deploy.");
    }
}
