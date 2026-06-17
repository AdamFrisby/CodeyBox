using CodeyBox.Core;
using CodeyBox.Deployment;

namespace CodeyBox.Tests;

/// <summary>
/// Driver lifecycle tests. Each driver exercises Provision → Deploy →
/// HealthCheck → Expose using the fake sandbox provider; teardown
/// invariants (idempotent dispose + teardown on lifecycle failure) are
/// asserted explicitly.
/// </summary>
public sealed class DeploymentDriverTests
{
    private static DeploymentContext Ctx(FakeDeploymentSandboxProvider provider) => new()
    {
        SandboxProvider = provider,
    };

    // ── WebApp ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WebApp_FullLifecycle_ReachesHttpEndpoint()
    {
        var provider = new FakeDeploymentSandboxProvider();
        // Probe fails twice then succeeds — exercises the retry loop.
        provider.ExecRules.Add(new ExecRule(
            "curl",
            new[]
            {
                new SandboxExecResult(7, "", "Connection refused"),
                new SandboxExecResult(7, "", "Connection refused"),
                new SandboxExecResult(0, "200", ""),
            },
            finalLoop: new SandboxExecResult(0, "200", "")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            RunCommand = "nohup ./server &",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromSeconds(30),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.True(handle.IsAlive);
        Assert.Equal(DeploymentEndpointKind.Http, handle.Endpoint.Kind);
        Assert.Equal("http://127.0.0.1:8080", handle.Endpoint.Url);
        Assert.Equal(8080, handle.Endpoint.Port);
        Assert.Equal("/healthz", handle.Endpoint.Path);
        Assert.Equal(DeploymentKinds.WebApp, handle.Kind);
        Assert.Single(provider.Created);
        Assert.False(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_BuildFails_TearsDownSubstrate()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("build-fail", new SandboxExecResult(2, "", "compile error")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "build-fail",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_ProbeNeverReady_TearsDownAndThrowsTimeout()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(7, "", "Connection refused")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromMilliseconds(200),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public void WebApp_RecipeWithoutHealthEndpoint_FailsValidation()
    {
        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [80],
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    // ── Daemon ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Daemon_LivenessCommandProbe_Succeeds()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("pgrep", new SandboxExecResult(0, "1234", "")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "nohup ./daemon &",
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyLivenessCommand] = "pgrep -f daemon",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal(DeploymentEndpointKind.Process, handle.Endpoint.Kind);
        Assert.Equal(DeploymentKinds.Daemon, handle.Kind);
        Assert.True(handle.IsAlive);
    }

    [Fact]
    public async Task Daemon_WithPortOnly_ExposesTcpEndpoint()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("/dev/tcp", new SandboxExecResult(0, "", "")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon &",
            Ports = [5432],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Tcp, handle.Endpoint.Kind);
        Assert.Equal(5432, handle.Endpoint.Port);
        Assert.Equal("127.0.0.1", handle.Endpoint.Host);
    }

    [Fact]
    public void Daemon_WithoutPortOrLiveness_FailsValidation()
    {
        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon",
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    // ── Cli ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cli_InvocationSucceeds_ExposesBinaryPath()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("--version", new SandboxExecResult(0, "1.0.0", "")));

        var driver = new CliDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Cli,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "make install",
            ArtifactPath = "/usr/local/bin/mytool",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Cli, handle.Endpoint.Kind);
        Assert.Equal("/usr/local/bin/mytool", handle.Endpoint.Path);
    }

    [Fact]
    public async Task Cli_InvocationFails_TearsDown()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("--version", new SandboxExecResult(127, "", "not found")));

        var driver = new CliDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Cli,
            ImageReference = "ubuntu-22.04",
            ArtifactPath = "/usr/local/bin/mytool",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public void Cli_RecipeWithoutArtifactPath_FailsValidation()
    {
        var driver = new CliDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Cli,
            ImageReference = "ubuntu-22.04",
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    // ── Library ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Library_BuildSucceeds_NoHarness_ExposesArtifact()
    {
        var provider = new FakeDeploymentSandboxProvider();
        // No exec rules: build returns success by default; readiness is no-op when no harness configured.

        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "dotnet pack",
            ArtifactPath = "./bin/mylib.nupkg",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Library, handle.Endpoint.Kind);
        Assert.Equal("./bin/mylib.nupkg", handle.Endpoint.Path);
    }

    [Fact]
    public async Task Library_HarnessFails_TearsDown()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("harness", new SandboxExecResult(1, "", "test failure")));

        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "dotnet pack",
            ArtifactPath = "./bin/mylib.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness run",
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public void Library_RecipeWithoutBuildCommand_FailsValidation()
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    // ── Idempotent teardown ─────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_IsIdempotent_SecondCallIsNoOp()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo built",
            ArtifactPath = "/lib/out.nupkg",
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        await handle.DisposeAsync();
        Assert.True(provider.Created[0].IsDisposed);

        // Second dispose is a no-op (no exception). Tracking flips to !IsAlive.
        await handle.DisposeAsync();
        Assert.False(handle.IsAlive);
    }

    [Fact]
    public async Task ProvisionFails_DriverPropagatesAndNoCleanupNeeded()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.SetCreateThrows();

        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            ArtifactPath = "/lib/out.nupkg",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Empty(provider.Created);
    }

    // ── HealthCheckAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_RunsAgainstLiveDeployment()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [9000],
            HealthEndpoint = "/health",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        await handle.HealthCheckAsync();   // does not throw
    }
}
