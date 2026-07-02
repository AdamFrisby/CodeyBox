using CodeyBox.Core;
using CodeyBox.Deployment;
using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Recipes;
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
        var jobTrack = JobTrackRecipe.Default(fixture.Path);
        var provider = new RoutableProcessSandboxProvider();
        var driver = new WebAppDeploymentDriver();
        var recipe = ToDeploymentRecipe(jobTrack);

        await using var handle = await driver.DeployAsync(
            recipe,
            Ctx(provider, ToDeploymentMounts(jobTrack.Mounts)),
            CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Http, handle.Endpoint.Kind);
        Assert.Equal("http://127.0.0.1:5080", handle.Endpoint.Url);
        await handle.HealthCheckAsync();

        var log = File.ReadAllText(Path.Combine(fixture.Path, ".codeybox-harness", "jobtrack-recipe.log"));
        Assert.Contains("apt-get install", log, StringComparison.Ordinal);
        Assert.Contains("restore JobTrack.sln", log, StringComparison.Ordinal);
        Assert.Contains("build JobTrack.sln --no-restore -c Release", log, StringComparison.Ordinal);
        Assert.Contains("ef database update --project src/JobTrack.Api", log, StringComparison.Ordinal);
        Assert.Contains("run --project tools/JobTrack.SeedFixtures -- --seed 1", log, StringComparison.Ordinal);
        Assert.Contains("run --no-build -c Release --project src/JobTrack.Api", log, StringComparison.Ordinal);
        await handle.ExecAsync(new DeploymentCommand
        {
            Argv = ["rm", "-f", ".codeybox-harness/server.keepalive"],
        });
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

    private static DeploymentRecipe ToDeploymentRecipe(WebAppRecipe recipe) => new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "ignored",
        BuildCommand = string.Join(
            " && ",
            recipe.BuildSteps.Concat(recipe.SeedSteps).Select(step => "PATH=fake-bin:$PATH " + QuoteArgv(step.Command))),
        RunCommand = "PATH=fake-bin:$PATH " + QuoteArgv(recipe.RunCommand.Command),
        Ports = [5080],
        HealthEndpoint = "/",
        StartupTimeout = TimeSpan.FromSeconds(15),
        Environment = new Dictionary<string, string>(recipe.Environment, StringComparer.Ordinal)
        {
            ["JOBTRACK_DB_PATH"] = ".codeybox-harness/jobtrack.db",
        },
        Settings = new Dictionary<string, string>
        {
            [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.05",
        },
    };

    private static IReadOnlyList<DeploymentMount> ToDeploymentMounts(IReadOnlyList<SandboxMount> mounts)
        => mounts.Select(m => new DeploymentMount
        {
            SubstratePath = m.SandboxPath,
            HostPath = m.HostPath,
            ReadOnly = m.ReadOnly,
            Tmpfs = m.Tmpfs,
            SizeBytes = m.SizeBytes,
        }).ToList();

    private static string QuoteArgv(IReadOnlyList<string> argv)
        => string.Join(' ', argv.Select(QuoteShellWord));

    private static string QuoteShellWord(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static TempDirectory CreateJobTrackFixture()
    {
        var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "fake-bin"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "src", "JobTrack.Api"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "tools", "JobTrack.SeedFixtures"));
        Directory.CreateDirectory(Path.Combine(dir.Path, ".codeybox-harness"));

        File.WriteAllText(Path.Combine(dir.Path, "JobTrack.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        File.WriteAllText(Path.Combine(dir.Path, "src", "JobTrack.Api", "JobTrack.Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");
        File.WriteAllText(Path.Combine(dir.Path, "tools", "JobTrack.SeedFixtures", "JobTrack.SeedFixtures.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        File.WriteAllText(Path.Combine(dir.Path, "src", "JobTrack.Api", "jobtrack_server.py"), """
            import os
            from http.server import BaseHTTPRequestHandler, HTTPServer

            class Handler(BaseHTTPRequestHandler):
                def do_GET(self):
                    if self.path in ("/", "/health", "/healthz"):
                        body = b"JobTrack deployment ready"
                        self.send_response(200)
                        self.send_header("Content-Type", "text/plain")
                        self.send_header("Content-Length", str(len(body)))
                        self.end_headers()
                        self.wfile.write(body)
                    else:
                        self.send_response(404)
                        self.end_headers()

                def log_message(self, format, *args):
                    return

            server = HTTPServer(("127.0.0.1", 5080), Handler)
            server.timeout = 0.2
            while os.path.exists(".codeybox-harness/server.keepalive"):
                server.handle_request()
            """);

        WriteExecutable(Path.Combine(dir.Path, "fake-bin", "sudo"), """
            #!/usr/bin/env sh
            set -eu
            mkdir -p .codeybox-harness
            printf '%s\n' "$*" >> .codeybox-harness/jobtrack-recipe.log
            if [ "${1:-}" = "apt-get" ]; then
              touch .codeybox-harness/firefox-installed
              exit 0
            fi
            exec "$@"
            """);
        WriteExecutable(Path.Combine(dir.Path, "fake-bin", "dotnet"), """
            #!/usr/bin/env sh
            set -eu
            mkdir -p .codeybox-harness
            printf '%s\n' "$*" >> .codeybox-harness/jobtrack-recipe.log
            case "$*" in
              "restore JobTrack.sln")
                test -f JobTrack.sln
                ;;
              "build JobTrack.sln --no-restore -c Release")
                test -f src/JobTrack.Api/JobTrack.Api.csproj
                touch .codeybox-harness/build.ok
                ;;
              "ef database update --project src/JobTrack.Api")
                test -d src/JobTrack.Api
                : > "${JOBTRACK_DB_PATH:-.codeybox-harness/jobtrack.db}"
                ;;
              "run --project tools/JobTrack.SeedFixtures -- --seed 1")
                test -f "${JOBTRACK_DB_PATH:-.codeybox-harness/jobtrack.db}"
                test -f tools/JobTrack.SeedFixtures/JobTrack.SeedFixtures.csproj
                touch .codeybox-harness/seed.ok
                ;;
              "run --no-build -c Release --project src/JobTrack.Api")
                test -f .codeybox-harness/build.ok
                test -f .codeybox-harness/seed.ok
                touch .codeybox-harness/server.keepalive
                exec timeout 30 python3 src/JobTrack.Api/jobtrack_server.py
                ;;
              *)
                echo "unexpected dotnet invocation: $*" >&2
                exit 64
                ;;
            esac
            """);
        WriteExecutable(Path.Combine(dir.Path, "fake-bin", "firefox"), """
            #!/usr/bin/env sh
            exit 0
            """);
        return dir;
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // Non-Unix test hosts ignore mode bits.
        }
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
