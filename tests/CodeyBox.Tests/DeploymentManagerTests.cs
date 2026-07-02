using CodeyBox.Core;
using CodeyBox.Deployment;

namespace CodeyBox.Tests;

public sealed class DeploymentManagerTests
{
    private static DeploymentContext Ctx(FakeDeploymentSandboxProvider provider) => new()
    {
        SubstrateProvider = new SandboxDeploymentSubstrateProvider(provider),
    };

    [Fact]
    public async Task StartAsync_TracksDeployment_AndUntracksOnDispose()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        var startedAt = new DateTimeOffset(2026, 7, 2, 1, 2, 3, TimeSpan.Zero);
        var manager = new DeploymentManager(
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeploymentManager>.Instance,
            () => startedAt);
        var projectId = new ProjectId("jobtrack");

        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu",
            BuildCommand = "dotnet pack",
            ArtifactPath = "/lib/out.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };

        var handle = await manager.StartAsync(
            recipe,
            Ctx(provider) with { ProjectId = projectId },
            CancellationToken.None);

        var active = manager.GetActive();
        Assert.Single(active);
        Assert.Equal(handle.Id, active[0].Id);
        Assert.Equal(DeploymentKinds.Library, active[0].Kind);
        Assert.Equal(projectId, active[0].ProjectId);
        Assert.Equal(handle.SubstrateId, active[0].SubstrateId);
        Assert.Equal(startedAt, active[0].StartedAt);

        await handle.DisposeAsync();
        Assert.Empty(manager.GetActive());
    }

    [Fact]
    public async Task DisposeFailure_KeepsDeploymentHandleAliveForRetry()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        var manager = new DeploymentManager(registry);

        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu",
            BuildCommand = "dotnet pack",
            ArtifactPath = "/lib/out.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };

        var handle = await manager.StartAsync(
            recipe,
            Ctx(provider),
            CancellationToken.None);
        provider.SandboxDisposeThrowsFor.Add(handle.SubstrateId!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handle.DisposeAsync());

        Assert.True(handle.IsAlive);
        Assert.Single(manager.GetActive());

        provider.SandboxDisposeThrowsFor.Remove(handle.SubstrateId!);
        await handle.DisposeAsync();

        Assert.False(handle.IsAlive);
        Assert.Empty(manager.GetActive());
    }

    [Fact]
    public async Task StartAsync_ValidatesRecipeBeforeDeploying()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new ValidationThrowingDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        var manager = new DeploymentManager(registry);
        var recipe = new DeploymentRecipe { Kind = driver.Kind, ImageReference = "x" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.StartAsync(
                recipe,
                Ctx(provider),
                CancellationToken.None));

        Assert.True(driver.ValidateCalled);
        Assert.False(driver.DeployCalled);
    }

    [Fact]
    public async Task StartAsync_UnknownKind_Throws()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var registry = new DeploymentDriverRegistry(Array.Empty<IDeploymentDriver>());
        var manager = new DeploymentManager(registry);
        var recipe = new DeploymentRecipe { Kind = "nonsense", ImageReference = "x" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(
                recipe,
                Ctx(provider),
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_DeployFailure_DoesNotTrackDeployment()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new DeployThrowingDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        var manager = new DeploymentManager(registry);
        var recipe = new DeploymentRecipe { Kind = driver.Kind, ImageReference = "x" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(
                recipe,
                Ctx(provider),
                CancellationToken.None));

        Assert.Contains("deploy failed", ex.Message);
        Assert.True(driver.ValidateCalled);
        Assert.True(driver.DeployCalled);
        Assert.Empty(manager.GetActive());
    }

    [Fact]
    public void Registry_DuplicateKind_Throws()
    {
        var d1 = new LibraryDeploymentDriver();
        var d2 = new LibraryDeploymentDriver();
        Assert.Throws<InvalidOperationException>(() => new DeploymentDriverRegistry([d1, d2]));
    }

    [Fact]
    public void Registry_NullDriver_IsSkipped()
    {
        var driver = new LibraryDeploymentDriver();
        var registry = new DeploymentDriverRegistry(new IDeploymentDriver[] { driver, null! });
        Assert.True(registry.TryGet(DeploymentKinds.Library, out var resolved));
        Assert.Same(driver, resolved);
    }

    [Fact]
    public void Registry_BlankKind_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new DeploymentDriverRegistry([new BlankKindDriver()]));
    }

    [Fact]
    public void Registry_TryGet_CaseInsensitiveLookup()
    {
        var driver = new WebAppDeploymentDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        Assert.True(registry.TryGet("WEB-APP", out var resolved));
        Assert.Same(driver, resolved);
    }

    private sealed class DeployThrowingDriver : IDeploymentDriver
    {
        public bool ValidateCalled { get; private set; }
        public bool DeployCalled { get; private set; }
        public string Kind => "deploy-failure-test";

        public void ValidateRecipe(DeploymentRecipe recipe) => ValidateCalled = true;

        public Task<IDeploymentHandle> DeployAsync(
            DeploymentRecipe recipe,
            DeploymentContext context,
            CancellationToken ct = default)
        {
            DeployCalled = true;
            throw new InvalidOperationException("deploy failed");
        }
    }

    private sealed class ValidationThrowingDriver : IDeploymentDriver
    {
        public bool ValidateCalled { get; private set; }
        public bool DeployCalled { get; private set; }
        public string Kind => "validation-test";

        public void ValidateRecipe(DeploymentRecipe recipe)
        {
            ValidateCalled = true;
            throw new ArgumentException("invalid recipe", nameof(recipe));
        }

        public Task<IDeploymentHandle> DeployAsync(
            DeploymentRecipe recipe,
            DeploymentContext context,
            CancellationToken ct = default)
        {
            DeployCalled = true;
            throw new NotSupportedException();
        }
    }

    private sealed class BlankKindDriver : IDeploymentDriver
    {
        public string Kind => " ";
        public void ValidateRecipe(DeploymentRecipe recipe) { }
        public Task<IDeploymentHandle> DeployAsync(
            DeploymentRecipe recipe,
            DeploymentContext context,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
