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
        SubstrateProvider = new SandboxDeploymentSubstrateProvider(provider),
    };

    private static WebAppDeploymentDriver NewWebAppDriver(
        Func<Uri, CancellationToken, Task<bool>>? hostHttpProbe = null)
        => new(hostHttpProbe: hostHttpProbe ?? ((_, _) => Task.FromResult(true)));

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

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            RunCommand = "./server",
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
        Assert.Equal("http://10.42.0.10:8080", handle.Endpoint.Url);
        Assert.Equal("10.42.0.10", handle.Endpoint.Host);
        Assert.Equal(8080, handle.Endpoint.Port);
        Assert.Null(handle.Endpoint.Path);
        Assert.Equal("host-routable", handle.Endpoint.Metadata["endpoint.scope"]);
        Assert.Equal("http://127.0.0.1:8080", handle.Endpoint.Metadata["sandbox.local-url"]);
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

        var driver = NewWebAppDriver();
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
    public async Task WebApp_BuildFails_AndDisposeFails_ReleasesActiveTrackingForReaperRetry()
    {
        var provider = new FakeDeploymentSandboxProvider
        {
            SandboxDisposeThrows = true,
        };
        provider.ExecRules.Add(new ExecRule("build-fail", new SandboxExecResult(2, "", "compile error")));

        var driver = NewWebAppDriver();
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

        var sandbox = Assert.Single(provider.Created);
        Assert.False(sandbox.IsDisposed);
        Assert.True(sandbox.ActiveTrackingReleased);

        var cleanupInfo = Assert.Single(await ((IDeploymentCleanupProvider)provider).ListAllManagedAsync(CancellationToken.None));
        Assert.False(cleanupInfo.IsTrackedActive);
    }

    [Fact]
    public async Task DeploymentExecs_SetOutputCaps()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.NotEmpty(provider.ExecInvocations);
        Assert.All(provider.ExecInvocations, exec =>
        {
            Assert.True(exec.MaxStdoutBytes > 0);
            Assert.True(exec.MaxStderrBytes > 0);
            Assert.True(exec.KillOnOutputLimit);
        });
    }

    [Fact]
    public async Task DeploymentExec_OutputLimitExceeded_TearsDownAndThrows()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule(
            "too-chatty",
            new SandboxExecResult(0, new string('x', 1024), "", StdoutLimitExceeded: true)));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "too-chatty",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Contains("output capture limit", ex.Message);
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task DeploymentFailure_RedactsCommandOutput()
    {
        const string Token = "ghp_XYZabc789012345678901234567890";
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("leak-secret", new SandboxExecResult(1, "", $"Authorization: {Token}")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "leak-secret",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains("***", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebApp_StartFails_TearsDownSubstrate()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("start-fail", new SandboxExecResult(42, "", "boom")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "start-fail",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_WithoutRoutableHost_TearsDownAndThrows()
    {
        var provider = new FakeDeploymentSandboxProvider { HostAddress = null };
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Contains("publish HTTP endpoint", ex.Message);
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_ExposedUrlProbeNeverReady_TearsDownAndThrowsTimeout()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = NewWebAppDriver((_, _) => Task.FromResult(false));
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromMilliseconds(100),
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
    public async Task WebApp_PublisherReturnsHttpEndpointWithoutUrl_TearsDownAndThrows()
    {
        var provider = new FakeDeploymentSandboxProvider
        {
            PublishEndpointOverride = request => new DeploymentEndpoint
            {
                Kind = request.Kind,
                Host = "10.42.0.10",
                Port = request.Port,
                Metadata = request.Metadata,
            },
        };
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Contains("without a URL", ex.Message);
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_BuildTimeout_TearsDownAndThrowsTimeout()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule(
            "slow-build",
            new SandboxExecResult(0, "", ""),
            delay: TimeSpan.FromSeconds(5)));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "slow-build",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_StartRuntimeTimeout_TearsDownAndThrowsTimeout()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule(
            "foreground-server",
            new SandboxExecResult(0, "", ""),
            delay: TimeSpan.FromSeconds(5)));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "foreground-server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_ProbeNeverReady_TearsDownAndThrowsTimeout()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(7, "", "Connection refused")));

        var driver = NewWebAppDriver();
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
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [80],
        };
        Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
    }

    [Fact]
    public async Task WebApp_CustomHealthProbeAndHttpsScheme_AreUsed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("custom-probe", new SandboxExecResult(0, "", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8443],
            HealthEndpoint = "/ready",
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyScheme] = "https",
                [WebAppDeploymentDriver.SettingsKeyHealthProbeCommand] = "custom-probe {url}",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal("https://10.42.0.10:8443", handle.Endpoint.Url);
        Assert.Contains(provider.ExecLog, c => c.Contains("custom-probe 'https://127.0.0.1:8443/ready'", StringComparison.Ordinal));
    }

    [Fact]
    public void WebApp_InvalidScheme_FailsValidation()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyScheme] = "http://169.254.169.254/latest",
            },
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("must be 'http' or 'https'", ex.Message);
    }

    [Fact]
    public async Task WebApp_HealthCheck_ProbesOriginalEndpointWithoutRepublishing()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));
        var hostProbeUris = new List<Uri>();
        var driver = NewWebAppDriver((uri, _) =>
        {
            hostProbeUris.Add(uri);
            return Task.FromResult(true);
        });
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        var publishCountAfterDeploy = provider.PublishRequests.Count;

        await handle.HealthCheckAsync(CancellationToken.None);

        Assert.Equal(1, publishCountAfterDeploy);
        Assert.Equal(publishCountAfterDeploy, provider.PublishRequests.Count);
        Assert.All(hostProbeUris, uri => Assert.Equal("http://10.42.0.10:8080/healthz", uri.ToString()));
    }

    [Fact]
    public async Task WebApp_UnprefixedHealthPaths_AreNormalizedForProbesAndMetadata()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));
        var hostProbeUris = new List<Uri>();
        var driver = NewWebAppDriver((uri, _) =>
        {
            hostProbeUris.Add(uri);
            return Task.FromResult(true);
        });
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [5432],
                    HealthEndpoint = "ready",
                },
            ],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Contains(provider.ExecLog, c => c.Contains("http://127.0.0.1:8080/healthz", StringComparison.Ordinal));
        Assert.Contains(provider.ExecLog, c => c.Contains("http://127.0.0.1:5432/ready", StringComparison.Ordinal));
        Assert.Equal("/healthz", handle.Endpoint.Metadata["http.health-path"]);
        Assert.Equal("http://10.42.0.10:5432/ready", handle.Endpoint.Metadata["service.db.url"]);
        Assert.Contains(hostProbeUris, uri => uri.ToString() == "http://10.42.0.10:8080/healthz");
    }

    [Fact]
    public async Task WebApp_StartsAndProbesBackingServicesBeforePrimary()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("postgres-start", new SandboxExecResult(0, "", "")));
        provider.ExecRules.Add(new ExecRule("127.0.0.1:5432", new SandboxExecResult(0, "ok", "")));
        provider.ExecRules.Add(new ExecRule("127.0.0.1:8080", new SandboxExecResult(0, "ok", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "postgres:16",
                    RunCommand = "docker run --rm {image} postgres-start",
                    Ports = [5432],
                    HealthEndpoint = "/ready",
                },
            ],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        var serviceStart = provider.ExecLog.FindIndex(c => c.Contains("postgres-start", StringComparison.Ordinal));
        var serviceProbe = provider.ExecLog.FindIndex(c => c.Contains("127.0.0.1:5432", StringComparison.Ordinal));
        var appStart = provider.ExecLog.FindIndex(c => c.Contains("./server", StringComparison.Ordinal));
        var appProbe = provider.ExecLog.FindIndex(c => c.Contains("127.0.0.1:8080", StringComparison.Ordinal));
        Assert.True(serviceStart >= 0, "service start did not run");
        Assert.True(serviceProbe > serviceStart, "service readiness probe did not run after service start");
        Assert.True(appStart > serviceProbe, "primary app started before backing service became ready");
        Assert.True(appProbe > appStart, "primary readiness probe did not run after primary start");
        Assert.Contains(provider.ExecLog, c => c.Contains("'postgres:16'", StringComparison.Ordinal));
        Assert.Equal("postgres:16", handle.Endpoint.Metadata["service.db.image"]);
        Assert.Equal("http://10.42.0.10:5432/ready", handle.Endpoint.Metadata["service.db.url"]);
    }

    [Fact]
    public async Task WebApp_ServiceEnvironment_OverlaysPrimaryEnvironmentForStartAndReadinessProbe()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("service-start", new SandboxExecResult(0, "", "")));
        provider.ExecRules.Add(new ExecRule("127.0.0.1:5432", new SandboxExecResult(0, "ok", "")));
        provider.ExecRules.Add(new ExecRule("127.0.0.1:8080", new SandboxExecResult(0, "ok", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Environment = new Dictionary<string, string>
            {
                ["BASE"] = "primary",
                ["SHARED"] = "primary",
            },
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [5432],
                    HealthEndpoint = "/ready",
                    Environment = new Dictionary<string, string>
                    {
                        ["SERVICE_ONLY"] = "service",
                        ["SHARED"] = "service",
                    },
                },
            ],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        var serviceInvocations = provider.ExecInvocations
            .Where(exec =>
                string.Join(' ', exec.Argv).Contains("service-start", StringComparison.Ordinal)
                || string.Join(' ', exec.Argv).Contains("127.0.0.1:5432", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, serviceInvocations.Count);
        Assert.All(serviceInvocations, exec =>
        {
            Assert.NotNull(exec.ExtraEnvironment);
            Assert.Equal("primary", exec.ExtraEnvironment!["BASE"]);
            Assert.Equal("service", exec.ExtraEnvironment["SHARED"]);
            Assert.Equal("service", exec.ExtraEnvironment["SERVICE_ONLY"]);
        });
    }

    [Fact]
    public async Task WebApp_ServiceStartFails_TearsDownSubstrate()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("service-start-fail", new SandboxExecResult(2, "", "service boom")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start-fail",
                    Ports = [5432],
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_ServiceReadinessTimeout_TearsDownSubstrate()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("127.0.0.1:5432", new SandboxExecResult(7, "", "refused")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            StartupTimeout = TimeSpan.FromMilliseconds(120),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [5432],
                    HealthEndpoint = "/ready",
                },
            ],
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));
        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task WebApp_ServiceWithoutHealthEndpoint_UsesTcpFallback()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("/dev/tcp/127.0.0.1/5432", new SandboxExecResult(0, "", "")));
        provider.ExecRules.Add(new ExecRule("127.0.0.1:8080", new SandboxExecResult(0, "ok", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [5432],
                },
            ],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Contains(provider.ExecLog, c =>
            c.StartsWith("bash -c", StringComparison.Ordinal)
            && c.Contains("/dev/tcp/127.0.0.1/5432", StringComparison.Ordinal));
        Assert.Equal("127.0.0.1:5432", handle.Endpoint.Metadata["service.db.sandbox-local-endpoint"]);
        Assert.Equal("10.42.0.10:5432", handle.Endpoint.Metadata["service.db.endpoint"]);
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
            RunCommand = "./daemon",
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
            RunCommand = "./daemon",
            Ports = [5432],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Tcp, handle.Endpoint.Kind);
        Assert.Equal(5432, handle.Endpoint.Port);
        Assert.Equal("10.42.0.10", handle.Endpoint.Host);
        Assert.Equal("host-routable", handle.Endpoint.Metadata["endpoint.scope"]);
    }

    [Fact]
    public async Task Daemon_WithHealthEndpoint_UsesHttpHealthProbeInsteadOfRawTcp()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("/healthz", new SandboxExecResult(0, "ok", "")));
        provider.ExecRules.Add(new ExecRule("/dev/tcp", new SandboxExecResult(1, "", "tcp probe should not run")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon",
            Ports = [8080],
            HealthEndpoint = "/healthz",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal(DeploymentEndpointKind.Tcp, handle.Endpoint.Kind);
        Assert.Contains(provider.ExecLog, c => c.Contains("http://127.0.0.1:8080/healthz", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.ExecLog, c => c.Contains("/dev/tcp/127.0.0.1/8080", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daemon_StartFails_TearsDownSubstrate()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("daemon-fail", new SandboxExecResult(2, "", "cannot start")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "daemon-fail",
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyLivenessCommand] = "pgrep -f daemon",
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
    }

    [Fact]
    public async Task Daemon_WithoutPortOrLiveness_UsesManagedProcessSidecar()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal(DeploymentEndpointKind.Process, handle.Endpoint.Kind);
        Assert.Equal("sandbox-process", handle.Endpoint.Metadata["endpoint.scope"]);
        Assert.Contains(provider.ExecLog, c => c.Contains("codeybox_base.pid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daemon_PortFallbackWithoutPublisher_ReturnsSandboxLocalTcpEndpoint()
    {
        var provider = new FakeDeploymentSandboxProvider { HostAddress = null };
        provider.ExecRules.Add(new ExecRule("/dev/tcp", new SandboxExecResult(0, "", "")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon",
            Ports = [5432],
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal(DeploymentEndpointKind.Tcp, handle.Endpoint.Kind);
        Assert.Equal("127.0.0.1", handle.Endpoint.Host);
        Assert.Equal(5432, handle.Endpoint.Port);
        Assert.Equal("sandbox-local", handle.Endpoint.Metadata["endpoint.scope"]);
        Assert.Equal("127.0.0.1:5432", handle.Endpoint.Metadata["sandbox.local-endpoint"]);
    }

    [Fact]
    public async Task Daemon_PortReadinessNeverSucceeds_TimesOutAndTearsDown()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("/dev/tcp", new SandboxExecResult(1, "", "refused")));

        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./daemon",
            Ports = [5432],
            StartupTimeout = TimeSpan.FromMilliseconds(120),
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None));

        Assert.Single(provider.Created);
        Assert.True(provider.Created[0].IsDisposed);
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
        var result = await handle.ExecAsync(new DeploymentCommand
        {
            Argv = [handle.Endpoint.Path!, "--version"],
        });
        Assert.True(result.Success);
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
    public async Task Library_HarnessSucceeds_ExposesArtifact()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("consumer-harness", new SandboxExecResult(0, "ok", "")));

        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "dotnet pack",
            ArtifactPath = "./bin/mylib.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "consumer-harness {artifact}",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Library, handle.Endpoint.Kind);
        Assert.Equal("./bin/mylib.nupkg", handle.Endpoint.Path);
        Assert.Equal("sandbox-artifact", handle.Endpoint.Metadata["endpoint.scope"]);
    }

    [Fact]
    public void Library_RecipeWithoutHarness_FailsValidation()
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "dotnet pack",
            ArtifactPath = "./bin/mylib.nupkg",
        };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("harness-command", ex.Message);
    }

    [Fact]
    public void Library_RecipeWithoutArtifactPath_FailsValidation()
    {
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "dotnet pack",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "consumer-harness",
            },
        };
        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("ArtifactPath", ex.Message);
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        await handle.DisposeAsync();
        Assert.True(provider.Created[0].IsDisposed);

        // Second dispose is a no-op (no exception) and must not call through
        // to the substrate a second time. Tracking flips to !IsAlive.
        await handle.DisposeAsync();
        Assert.False(handle.IsAlive);
        Assert.Equal(1, provider.Created[0].DisposeCallCount);
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
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
        var readinessProbe = new ExecRule("curl", new SandboxExecResult(0, "200", ""));
        provider.ExecRules.Add(readinessProbe);
        var hostProbeCount = 0;

        var driver = NewWebAppDriver((_, _) =>
        {
            hostProbeCount++;
            return Task.FromResult(true);
        });
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [9000],
            HealthEndpoint = "/health",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(1, readinessProbe.InvocationCount);
        Assert.Equal(1, hostProbeCount);

        await handle.HealthCheckAsync();   // does not throw
        Assert.Equal(2, readinessProbe.InvocationCount);
        Assert.Equal(2, hostProbeCount);
    }

    [Fact]
    public async Task HealthCheck_DefaultToken_TimesOutWhenHostExposureFails()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));
        var hostProbeCount = 0;
        var driver = NewWebAppDriver((_, _) =>
        {
            hostProbeCount++;
            return Task.FromResult(hostProbeCount == 1);
        });
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [9000],
            HealthEndpoint = "/health",
            StartupTimeout = TimeSpan.FromMilliseconds(120),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => handle.HealthCheckAsync());
        Assert.True(hostProbeCount > 1, $"expected host probe retries; saw {hostProbeCount}");
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

        var driver = NewWebAppDriver();
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
    public async Task HealthCheck_DefaultToken_TimesOutWhenUnhealthy()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl",
            new[] { new SandboxExecResult(0, "200", "") },
            finalLoop: new SandboxExecResult(7, "", "Connection refused")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [9000],
            HealthEndpoint = "/health",
            StartupTimeout = TimeSpan.FromMilliseconds(75),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.01",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => handle.HealthCheckAsync());
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        await handle.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.HealthCheckAsync());
    }

    [Fact]
    public async Task Exec_AfterDispose_ThrowsObjectDisposed()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            ArtifactPath = "/lib/x.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        await handle.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            handle.ExecAsync(new DeploymentCommand { Argv = ["true"] }));
    }

    [Fact]
    public async Task DisposeFailure_KeepsHandleAliveAndRetryable()
    {
        var provider = new FakeDeploymentSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ubuntu-22.04",
            BuildCommand = "echo build",
            ArtifactPath = "/lib/x.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
        };
        var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        provider.SandboxDisposeThrowsFor.Add(handle.SubstrateId!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handle.DisposeAsync());

        Assert.True(handle.IsAlive);
        Assert.False(provider.Created[0].IsDisposed);

        provider.SandboxDisposeThrowsFor.Remove(handle.SubstrateId!);
        await handle.DisposeAsync();

        Assert.False(handle.IsAlive);
        Assert.True(provider.Created[0].IsDisposed);
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
            // NetworkProfile is null → BuildSandboxSpec must default to Denied
        };
        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Single(provider.Specs);
        Assert.Equal(SandboxPurpose.Deployment, provider.Specs[0].Purpose);
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
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
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "harness {artifact}",
            },
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

    [Fact]
    public void Base_ValidateRecipe_RejectsServiceWithoutRunCommand()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    Ports = [5432],
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("RunCommand", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsNullServiceEntry()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services = [null!],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("null entries", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Base_ValidateRecipe_RejectsServiceWithBlankName(string name)
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = name,
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [5432],
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("Name is required", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Base_ValidateRecipe_RejectsServiceWithBlankImageReference(string imageReference)
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = imageReference,
                    RunCommand = "service-start",
                    Ports = [5432],
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("ImageReference", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsServiceWithoutPorts()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("Ports", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsServiceWithInvalidPort()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "ubuntu-22.04",
                    RunCommand = "service-start",
                    Ports = [70000],
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("invalid port", ex.Message);
    }

    [Fact]
    public void Base_ValidateRecipe_RejectsDistinctServiceImageWithoutPlaceholder()
    {
        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./server",
            Ports = [8080],
            HealthEndpoint = "/healthz",
            Services =
            [
                new DeploymentService
                {
                    Name = "db",
                    ImageReference = "postgres:16",
                    RunCommand = "postgres-start",
                    Ports = [5432],
                },
            ],
        };

        var ex = Assert.Throws<ArgumentException>(() => driver.ValidateRecipe(recipe));
        Assert.Contains("{image}", ex.Message);
    }

    // ── Driver-specific validation gaps ─────────────────────────────────────

    [Fact]
    public void WebApp_RecipeWithoutRunCommand_FailsValidation()
    {
        var driver = NewWebAppDriver();
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
        var driver = NewWebAppDriver();
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

    [Fact]
    public async Task Daemon_EmptyLivenessCommandAndNoPorts_FallsBackToManagedProcessSidecar()
    {
        var provider = new FakeDeploymentSandboxProvider();
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

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);

        Assert.Equal(DeploymentEndpointKind.Process, handle.Endpoint.Kind);
        Assert.Contains(provider.ExecLog, c => c.Contains("codeybox_base.pid", StringComparison.Ordinal));
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
            RunCommand = "./d",
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
            RunCommand = "./d",
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

    // ── Endpoint publisher guards ──────────────────────────────────────────

    [Fact]
    public void DeploymentEndpointPublisher_ForHostPort_RejectsInvalidInputs()
    {
        var valid = new DeploymentEndpointRequest
        {
            Kind = DeploymentEndpointKind.Tcp,
            Port = 1234,
        };

        Assert.Throws<ArgumentException>(() => DeploymentEndpointPublisher.ForHostPort(valid, " "));
        Assert.Throws<ArgumentException>(() => DeploymentEndpointPublisher.ForHostPort(valid with { Port = null }, "127.0.0.1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeploymentEndpointPublisher.ForHostPort(valid with { Port = 0 }, "127.0.0.1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeploymentEndpointPublisher.ForHostPort(valid with { Port = 65536 }, "127.0.0.1"));
    }

    // ── StartRuntimeAsync actually runs ─────────────────────────────────────

    [Fact]
    public async Task WebApp_StartRuntimeAsync_ExecutesRunCommand()
    {
        var provider = new FakeDeploymentSandboxProvider();
        provider.ExecRules.Add(new ExecRule("curl", new SandboxExecResult(0, "200", "")));

        var driver = NewWebAppDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ubuntu-22.04",
            RunCommand = "./signature-runcmd",
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
