using CodeyBox.Core;
using CodeyBox.Deployment;

namespace CodeyBox.Tests;

public sealed class DeploymentManagerTests
{
    [Fact]
    public async Task StartAsync_TracksDeployment_AndUntracksOnDispose()
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
        };

        var handle = await manager.StartAsync(
            recipe,
            new DeploymentContext { SandboxProvider = provider },
            CancellationToken.None);

        var active = manager.GetActive();
        Assert.Single(active);
        Assert.Equal(handle.Id, active[0].Id);
        Assert.Equal(DeploymentKinds.Library, active[0].Kind);
        Assert.Equal(handle.SandboxId, active[0].SandboxId);

        await handle.DisposeAsync();
        Assert.Empty(manager.GetActive());
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
                new DeploymentContext { SandboxProvider = provider },
                CancellationToken.None));
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
    public void Registry_TryGet_CaseInsensitiveLookup()
    {
        var driver = new WebAppDeploymentDriver();
        var registry = new DeploymentDriverRegistry([driver]);
        Assert.True(registry.TryGet("WEB-APP", out var resolved));
        Assert.Same(driver, resolved);
    }
}
