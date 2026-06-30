using System.Net;
using System.Net.Sockets;
using CodeyBox.Core;
using CodeyBox.Deployment;
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
    public async Task WebApp_JobTrackPilotShape_Deploys_HealthChecks_Exposes_AndTearsDown_OnProcessSubstrate()
    {
        var port = ReserveLoopbackPort();
        var provider = new RoutableProcessSandboxProvider();
        var driver = new WebAppDeploymentDriver();
        var recipe = new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = "ignored",
            RunCommand = $$"""
                python3 - <<'PY' >/dev/null 2>&1 &
                import socket
                port = {{port}}
                body = b"jobtrack pilot ok"
                with socket.socket() as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", port))
                    server.listen(5)
                    for _ in range(2):
                        conn, _ = server.accept()
                        with conn:
                            conn.recv(4096)
                            conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
                PY
                """,
            Ports = [port],
            HealthEndpoint = "/health",
            Environment = new Dictionary<string, string>
            {
                ["JOBTRACK_DB_PATH"] = "./jobtrack-smoke.db",
            },
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.05",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
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
                (
                  deadline=$(( $(date +%s) + 120 ))
                  while [ -f daemon.keepalive ] && [ "$(date +%s)" -lt "$deadline" ]; do
                    sleep 1
                  done
                ) >/dev/null 2>&1 < /dev/null &
                echo $! > daemon.pid
                """,
            StartupTimeout = TimeSpan.FromSeconds(15),
            Settings = new Dictionary<string, string>
            {
                [DaemonDeploymentDriver.SettingsKeyLivenessCommand] = "test -r daemon.pid && kill -0 $(cat daemon.pid)",
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

        var result = await handle.ExecAsync(new SandboxExec
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
            BuildCommand = "mkdir -p pkg && printf 'package bytes' > pkg/sample.nupkg",
            ArtifactPath = "./pkg/sample.nupkg",
            Settings = new Dictionary<string, string>
            {
                [LibraryDeploymentDriver.SettingsKeyHarnessCommand] = "test -s {artifact}",
            },
        };

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Library, handle.Endpoint.Kind);
        Assert.Equal("./pkg/sample.nupkg", handle.Endpoint.Path);
        await handle.HealthCheckAsync();
    }

    private static DeploymentContext Ctx(ISandboxProvider provider) => new()
    {
        SandboxProvider = provider,
        WorkingDirectory = "/work",
    };

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

    private sealed class RoutableProcessSandbox(ISandbox inner) : IRoutableSandbox
    {
        public string Id => inner.Id;
        public string? HostAddress => "127.0.0.1";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => inner.ExecAsync(exec, ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => inner.KillActiveExecsAsync(ct);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}
