using CodeyBox.Deployment;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

public sealed class DeploymentRecipeBinderTests
{
    [Fact]
    public void ToRecipe_NullConfig_ReturnsNull()
    {
        Assert.Null(DeploymentRecipeBinder.ToRecipe(null));
    }

    [Fact]
    public void ToRecipe_MissingKind_Throws()
    {
        var cfg = new DeploymentRecipeConfig { ImageReference = "x" };
        Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
    }

    [Fact]
    public void ToRecipe_MissingImageReference_Throws()
    {
        var cfg = new DeploymentRecipeConfig { Kind = "web-app" };
        Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
    }

    [Fact]
    public void ToRecipe_MapsAllFields()
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "web-app",
            ImageReference = "ubuntu-22.04",
            BuildCommand = "make",
            RunCommand = "./server",
            ArtifactPath = "/bin/app",
            HealthEndpoint = "/healthz",
            NetworkProfile = "egress-restricted",
            StartupTimeoutSeconds = 30,
            MaxLifetimeMinutes = 5,
            Ports = new() { 8080, 8443 },
            Environment = new() { ["FOO"] = "bar" },
            Settings = new() { ["scheme"] = "https" },
            Services = new()
            {
                new DeploymentServiceConfig
                {
                    Name = "db",
                    ImageReference = "postgres:16",
                    RunCommand = "postgres",
                    Ports = new() { 5432 },
                    Environment = new() { ["POSTGRES_PASSWORD"] = "x" },
                    HealthEndpoint = "/ready",
                },
            },
        };

        var recipe = DeploymentRecipeBinder.ToRecipe(cfg);
        Assert.NotNull(recipe);
        Assert.Equal("web-app", recipe!.Kind);
        Assert.Equal("ubuntu-22.04", recipe.ImageReference);
        Assert.Equal("make", recipe.BuildCommand);
        Assert.Equal("./server", recipe.RunCommand);
        Assert.Equal("/bin/app", recipe.ArtifactPath);
        Assert.Equal("/healthz", recipe.HealthEndpoint);
        Assert.Equal("egress-restricted", recipe.NetworkProfile);
        Assert.Equal(TimeSpan.FromSeconds(30), recipe.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), recipe.MaxLifetime);
        Assert.Equal(new[] { 8080, 8443 }, recipe.Ports);
        Assert.Equal("bar", recipe.Environment["FOO"]);
        Assert.Equal("https", recipe.Settings["scheme"]);
        Assert.Single(recipe.Services);
        var svc = recipe.Services[0];
        Assert.Equal("db", svc.Name);
        Assert.Equal("postgres:16", svc.ImageReference);
        Assert.Equal(new[] { 5432 }, svc.Ports);
        Assert.Equal("x", svc.Environment["POSTGRES_PASSWORD"]);
    }

    [Fact]
    public void ToRecipe_AppliesDefaults_WhenTimeoutsOmitted()
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "cli",
            ImageReference = "ubuntu-22.04",
            ArtifactPath = "/usr/local/bin/x",
        };
        var recipe = DeploymentRecipeBinder.ToRecipe(cfg);
        Assert.NotNull(recipe);
        Assert.Equal(TimeSpan.FromMinutes(5), recipe!.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), recipe.MaxLifetime);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ToRecipe_BadStartupTimeoutSeconds_Throws(double bad)
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "cli",
            ImageReference = "x",
            ArtifactPath = "/y",
            StartupTimeoutSeconds = bad,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
        Assert.Contains("StartupTimeoutSeconds", ex.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ToRecipe_BadMaxLifetimeMinutes_Throws(double bad)
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "cli",
            ImageReference = "x",
            ArtifactPath = "/y",
            MaxLifetimeMinutes = bad,
        };
        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
        Assert.Contains("MaxLifetimeMinutes", ex.Message);
    }

    [Fact]
    public void ToRecipe_ServiceMissingName_Throws()
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "web-app",
            ImageReference = "x",
            Services = new List<DeploymentServiceConfig>
            {
                new() { ImageReference = "postgres:16" /* Name missing */ },
            },
        };
        Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
    }

    [Fact]
    public void ToRecipe_ServiceMissingImageReference_Throws()
    {
        var cfg = new DeploymentRecipeConfig
        {
            Kind = "web-app",
            ImageReference = "x",
            Services = new List<DeploymentServiceConfig>
            {
                new() { Name = "db" /* ImageReference missing */ },
            },
        };
        Assert.Throws<InvalidOperationException>(() => DeploymentRecipeBinder.ToRecipe(cfg));
    }

    [Fact]
    public void ToRecipe_BindsFromConfigurationBuilder()
    {
        // End-to-end: appsettings-shaped dict → IConfiguration → DeploymentRecipeConfig → DeploymentRecipe.
        var inMemory = new Dictionary<string, string?>
        {
            ["Deployment:Kind"] = "web-app",
            ["Deployment:ImageReference"] = "ubuntu",
            ["Deployment:RunCommand"] = "./server",
            ["Deployment:HealthEndpoint"] = "/health",
            ["Deployment:Ports:0"] = "8080",
            ["Deployment:Environment:FOO"] = "bar",
            ["Deployment:Settings:scheme"] = "https",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var bound = cfg.GetSection("Deployment").Get<DeploymentRecipeConfig>();
        var recipe = DeploymentRecipeBinder.ToRecipe(bound);

        Assert.NotNull(recipe);
        Assert.Equal("web-app", recipe!.Kind);
        Assert.Equal("ubuntu", recipe.ImageReference);
        Assert.Equal("./server", recipe.RunCommand);
        Assert.Equal("/health", recipe.HealthEndpoint);
        Assert.Equal(new[] { 8080 }, recipe.Ports);
        Assert.Equal("bar", recipe.Environment["FOO"]);
        Assert.Equal("https", recipe.Settings["scheme"]);
    }
}
