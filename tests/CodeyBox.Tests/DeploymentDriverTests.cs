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
        Assert.Null(handle.Endpoint.Path);
        Assert.Equal("/healthz", handle.Endpoint.Metadata["http.health-path"]);
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

    /// <summary>
    /// Regression for the IDeploymentHandle.HealthCheckAsync(CancellationToken)
    /// contract: callers cancelling a hung readiness check must actually abort
    /// the probe loop instead of waiting forever. Previously the stored
    /// delegate ignored its CancellationToken parameter (Func&lt;Task&gt;).
    /// </summary>
    [Fact]
    public async Task HealthCheck_HonoursCallerCancellation()
    {
        var provider = new FakeDeploymentSandboxProvider();
        // Initial deploy probe succeeds so we get a handle to test against.
        provider.ExecRules.Add(new ExecRule("curl",
            new[] { new SandboxExecResult(0, "200", "") },
            finalLoop: new SandboxExecResult(7, "", "Connection refused")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [9000],
            HealthEndpoint = "/health",
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.HealthCheckAsync(cts.Token));
    }

    [Fact]
    public async Task HealthCheck_AfterDispose_ThrowsObjectDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            ArtifactPath = "/lib/x.nupkg",
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        await handle.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.HealthCheckAsync());
    }

    // ── SandboxSpec plumbing ────────────────────────────────────────────────

    [Fact]
    public async Task BuildSandboxSpec_Default_IsNetworkDenied()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo built",
            ArtifactPath = "/lib/x.nupkg",
            // NetworkProfile is null → BuildSandboxSpec must default to Denied
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Single(provider.Specs);
        Assert.Same(SandboxNetworkPolicy.Denied, provider.Specs[0].Network);
        Assert.Null(provider.Specs[0].Network.ProfileName);
    }

    [Fact]
    public async Task BuildSandboxSpec_NetworkProfile_FlowsThrough()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo built",
            ArtifactPath = "/lib/x.nupkg",
            NetworkProfile = "egress-restricted",
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal("egress-restricted", provider.Specs[0].Network.ProfileName);
    }

    [Fact]
    public async Task BuildSandboxSpec_Environment_FlowsThrough()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo built",
            ArtifactPath = "/lib/x.nupkg",
            Environment = new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" },
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal("1", provider.Specs[0].Environment["DOTNET_NOLOGO"]);
    }

    // ── Base ValidateRecipe guards ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Base_ValidateRecipe_RejectsEmptyImageReference(string imageRef)
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe { Kind = "library", ImageReference = imageRef, BuildCommand = "x" };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("ImageReference", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsNonPositiveStartupTimeout()
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = "library",
            ImageReference = "x",
            BuildCommand = "x",
            StartupTimeout = TimeSpan.Zero,
        };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("StartupTimeout", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsNonPositiveMaxLifetime()
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = "library",
            ImageReference = "x",
            BuildCommand = "x",
            MaxLifetime = TimeSpan.Zero,
        };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("MaxLifetime", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Base_ValidateRecipe_RejectsOutOfRangePort(int badPort)
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = "library",
            ImageReference = "x",
            BuildCommand = "x",
            Ports = [badPort],
        };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("invalid port", ex.Message);
    }

    // ── Driver-specific validation gaps ─────────────────────────────────────

    [Fact]
    public void WebApp_RecipeWithoutRunCommand_FailsValidation()
    {
        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            Ports = [80],
            HealthEndpoint = "/h",
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    [Fact]
    public void WebApp_RecipeWithoutPorts_FailsValidation()
    {
        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./s",
            HealthEndpoint = "/h",
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    [Fact]
    public void Daemon_RecipeWithoutRunCommand_FailsValidation()
    {
        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            Ports = [5432],
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    /// <summary>
    /// Regression: ValidateRecipe used ContainsKey for the liveness-command
    /// setting, so a recipe with Settings["liveness-command"]="" and no
    /// ports passed validation and then crashed inside ProbeReadyAsync on
    /// recipe.Ports[0] with IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void Daemon_EmptyLivenessCommandAndNoPorts_FailsValidation()
    {
        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./d",
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyLivenessCommand] = "",
            },
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    // ── Daemon probe portability + retry loop ───────────────────────────────

    /// <summary>
    /// Regression: the daemon port-only probe must invoke bash explicitly —
    /// /dev/tcp is bash-only and the default /bin/sh on Ubuntu (dash) does
    /// not implement it.
    /// </summary>
    [Fact]
    public async Task Daemon_PortOnlyProbe_UsesBashExplicitly()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("/dev/tcp", new SandboxExecResult(0, "", "")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./d &",
            Ports = [5432],
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Contains(provider.ExecLog, c => c.StartsWith("bash -c", StringComparison.Ordinal) && c.Contains("/dev/tcp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: the daemon driver retry loop must actually retry. The
    /// WebApp driver had a test for this; the daemon driver did not, so a
    /// regression that one-shotted the loop went uncaught.
    /// </summary>
    [Fact]
    public async Task Daemon_LivenessProbe_RetriesUntilSuccess()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("pgrep",
            new[]
            {
                new SandboxExecResult(1, "", "no match"),
                new SandboxExecResult(1, "", "no match"),
                new SandboxExecResult(0, "12345", ""),
            },
            finalLoop: new SandboxExecResult(0, "12345", "")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "nohup ./d &",
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyLivenessCommand] = "pgrep -f d",
                [DaemonDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
            StartupTimeout = TimeSpan.FromSeconds(30),
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        var pgrepInvocations = provider.ExecLog.Count(c => c.Contains("pgrep", StringComparison.Ordinal));
        Assert.True(pgrepInvocations >= 3, $"expected at least 3 retries; saw {pgrepInvocations}");
    }

    // ── Template substitution paths ─────────────────────────────────────────

    [Fact]
    public async Task Cli_InvocationOverride_SubstitutesArtifactPlaceholder()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("--selftest", new SandboxExecResult(0, "ok", "")));

        var driver = new CliDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Cli,
            ImageReference = "ubuntu-22.04",
            ArtifactPath = "/usr/local/bin/mytool",
            Settings = new Dictionary<string, string>
            {
                [CliDeploymentDriver.SettingsKeyInvocationCommand] = "{artifact} --selftest",
            },
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Contains(provider.ExecLog, c => c.Contains("/usr/local/bin/mytool", StringComparison.Ordinal) && c.Contains("--selftest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_HarnessOverride_SubstitutesArtifactPlaceholder()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("harness", new SandboxExecResult(0, "ok", "")));

        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo built",
            ArtifactPath = "/lib/out.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness restore {artifact}",
            },
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Contains(provider.ExecLog, c => c.Contains("/lib/out.nupkg", StringComparison.Ordinal) && c.Contains("harness", StringComparison.Ordinal));
    }

    // ── StartRuntimeAsync actually runs ─────────────────────────────────────

    [Fact]
    public async Task WebApp_StartRuntimeAsync_ExecutesRunCommand()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "nohup ./signature-runcmd &",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        var runIndex = provider.ExecLog.FindIndex(c => c.Contains("signature-runcmd", StringComparison.Ordinal));
        var probeIndex = provider.ExecLog.FindIndex(c => c.Contains("curl", StringComparison.Ordinal));
        Assert.True(runIndex >= 0, "RunCommand was not executed");
        Assert.True(runIndex < probeIndex, "RunCommand must execute before the readiness probe");
    }
}
