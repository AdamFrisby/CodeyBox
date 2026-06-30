using CodeyBox.Core;
using CodeyBox.Deployment;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Provider-backed smoke coverage for the deployment drivers. The unit tests
/// in <see cref="DeploymentDriverTests"/> use a programmable fake to pin error
/// branches; these tests run real shell commands through an actual
/// <see cref="ISandboxProvider"/> so command quoting, working-directory
/// plumbing, readiness probes, expose, and teardown execute on the substrate
/// contract.
/// </summary>
public sealed class DeploymentDriverSubstrateSmokeTests
{
    [Fact]
    public async Task WebApp_JobTrackPilot_Deploys_HealthChecks_Exposes_AndTearsDown_OnProcessSubstrate()
    {
        using var fixture = CreateJobTrackFixture();
        var port = GetFreeTcpPort();
        var provider = new RoutableProcessSandboxProvider();
        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ignored",
            BuildCommand = "test -f index.html && test -f health",
            RunCommand = $"timeout 30 python3 -m http.server {port} --bind 127.0.0.1",
            Ports = [port],
            HealthEndpoint = "/health",
            StartupTimeout = TimeSpan.FromSeconds(15),
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.05",
            },
        };

        await using var handle = await driver.DeployAsync(
            recipe,
            Ctx(provider,
            [
                new DeploymentMount
                {
                    SubstratePath = "/work",
                    HostPath = fixture.Path,
                    ReadOnly = false,
                },
            ]),
            CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Http, handle.Endpoint.Kind);
        Assert.Equal($"http://127.0.0.1:{port}", handle.Endpoint.Url);
        await handle.HealthCheckAsync();
    }

    [Fact]
    public async Task Daemon_TrivialSample_Deploys_HealthChecks_Exposes_AndTearsDown_OnProcessSubstrate()
    {
        var provider = new RoutableProcessSandboxProvider();
        var driver = new DaemonDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Daemon,
            ImageReference = "ignored",
            RunCommand = """
                touch daemon.keepalive
                deadline=$(( $(date +%s) + 120 ))
                while [ -f daemon.keepalive ] && [ "$(date +%s)" -lt "$deadline" ]; do
                  sleep 1
                done
                """,
            StartupTimeout = TimeSpan.FromSeconds(15),
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.05",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Process, handle.Endpoint.Kind);
        await handle.HealthCheckAsync();
    }

    [Fact]
    public async Task Cli_TrivialSample_Deploys_Exposes_AndCanBeInvoked_OnProcessSubstrate()
    {
        var provider = new RoutableProcessSandboxProvider();
        var driver = new CliDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Cli,
            ImageReference = "ignored",
            BuildCommand = """
                mkdir -p bin
                cat > bin/codeybox-smoke-cli <<'SH'
                #!/usr/bin/env sh
                if [ "${1:-}" = "--version" ]; then
                  echo "codeybox-smoke-cli 1.0"
                  exit 0
                fi
                echo "unexpected args: $*" >&2
                exit 2
                SH
                chmod +x bin/codeybox-smoke-cli
                """,
            ArtifactPath = "./bin/codeybox-smoke-cli",
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Cli, handle.Endpoint.Kind);
        Assert.Equal("./bin/codeybox-smoke-cli", handle.Endpoint.Path);

        var result = await handle.ExecAsync(new DeploymentCommand
        {
            Argv = ["sh", "-c", "./bin/codeybox-smoke-cli --version"],
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("codeybox-smoke-cli 1.0", result.Stdout);
    }

    [Fact]
    public async Task Library_TrivialSample_Deploys_Harnesses_AndExposesArtifact_OnProcessSubstrate()
    {
        var provider = new RoutableProcessSandboxProvider();
        var driver = new LibraryDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.Library,
            ImageReference = "ignored",
            BuildCommand = """
                rm -rf pkg consumer
                mkdir -p pkg/lib
                cat > pkg/lib/sample-tool <<'SH'
                #!/usr/bin/env sh
                echo codeybox-library-ok
                SH
                chmod +x pkg/lib/sample-tool
                tar -czf pkg/sample.nupkg -C pkg/lib sample-tool
                """,
            ArtifactPath = "./pkg/sample.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = """
                    rm -rf consumer &&
                    mkdir consumer &&
                    tar -xzf {artifact} -C consumer &&
                    test "$(consumer/sample-tool)" = "codeybox-library-ok"
                    """,
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Library, handle.Endpoint.Kind);
        Assert.Equal("./pkg/sample.nupkg", handle.Endpoint.Path);
        await handle.HealthCheckAsync();
    }

    private static DeploymentContext Ctx(ISandboxProvider provider, IReadOnlyList<DeploymentMount>? mounts = null) => new()
    {
        SubstrateProvider = new SandboxDeploymentSubstrateProvider(provider),
        Mounts = mounts ?? [],
        WorkingDirectory = "/work",
    };

    private static TempDirectory CreateJobTrackFixture()
    {
        var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "index.html"), "<!doctype html><title>JobTrack</title>");
        File.WriteAllText(Path.Combine(dir.Path, "health"), "ok");
        return dir;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "codeybox-deployment-jobtrack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private sealed class RoutableProcessSandboxProvider : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner =
            new(NullLogger<ProcessSandboxProvider>.Instance);

        public string Name => _inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new RoutableProcessSandbox(await _inner.CreateAsync(spec, ct).ConfigureAwait(false));

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class RoutableProcessSandbox(ISandbox inner) : IRoutableSandbox, IDeploymentEndpointPublisher
    {
        public string Id => inner.Id;
        public string? HostAddress => "127.0.0.1";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => inner.ExecAsync(exec, ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => inner.KillActiveExecsAsync(ct);

        public bool CanPublishEndpoint(DeploymentEndpointRequest request)
            => request.Port is >= 1 and <= 65535
                && request.Kind is DeploymentEndpointKind.Http or DeploymentEndpointKind.Tcp;

        public DeploymentEndpoint PublishEndpoint(DeploymentEndpointRequest request)
            => DeploymentEndpointPublisher.ForHostPort(request, HostAddress!);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}
