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
        using var fixture = CreateJobTrackPilotSource();
        var provider = new RoutableProcessSandboxProvider();
        var driver = new WebAppDeploymentDriver();
        var recipe = JobTrackPilotRecipe(fixture.Path);

        await using var handle = await driver.DeployAsync(
            recipe,
            Ctx(provider, [new DeploymentMount
            {
                SubstratePath = "/work",
                HostPath = fixture.Path,
                ReadOnly = false,
            }]),
            CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Http, handle.Endpoint.Kind);
        Assert.Equal("http://127.0.0.1:5080", handle.Endpoint.Url);
        await handle.HealthCheckAsync();

        var seeded = File.ReadAllText(Path.Combine(fixture.Path, ".codeybox-harness", "jobtrack.db"));
        Assert.Contains("seed=1", seeded, StringComparison.Ordinal);
        Assert.Contains("source=JobTrack.SeedFixtures", seeded, StringComparison.Ordinal);

        var artifactCheck = await handle.ExecAsync(new DeploymentCommand
        {
            Argv = ["sh", "-c", "test -f src/JobTrack.Api/bin/Release/net10.0/JobTrack.Api.dll"],
        });
        Assert.True(artifactCheck.Success, artifactCheck.Stderr);

        var stop = await handle.ExecAsync(new DeploymentCommand
        {
            Argv = ["sh", "-c", "rm -f .codeybox-harness/server.keepalive"],
        });
        Assert.True(stop.Success, stop.Stderr);

        var stopped = await handle.ExecAsync(new DeploymentCommand
        {
            Argv =
            [
                "sh",
                "-c",
                "for i in $(seq 1 100); do test -f .codeybox-harness/runtime/primary.exit && exit 0; sleep 0.1; done; exit 1",
            ],
        });
        Assert.True(stopped.Success, stopped.Stderr);
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
            StartupTimeout = TimeSpan.FromSeconds(45),
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

    private static DeploymentRecipe JobTrackPilotRecipe(string sourceRoot) => new()
    {
        Kind = DeploymentKinds.WebApp,
        ImageReference = "ignored",
        BuildCommand = """
            dotnet build src/JobTrack.Api/JobTrack.Api.csproj -c Release &&
            dotnet run --project tools/JobTrack.SeedFixtures/JobTrack.SeedFixtures.csproj -- --seed 1
            """,
        RunCommand = "mkdir -p .codeybox-harness && touch .codeybox-harness/server.keepalive && dotnet run --no-build -c Release --project src/JobTrack.Api/JobTrack.Api.csproj",
        Ports = [5080],
        HealthEndpoint = "healthz",
        StartupTimeout = TimeSpan.FromSeconds(90),
        Environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:5080",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["JOBTRACK_DB_PATH"] = Path.Combine(sourceRoot, ".codeybox-harness", "jobtrack.db"),
            ["CODEYBOX_DEPLOYMENT_RUNTIME_DIR"] = Path.Combine(sourceRoot, ".codeybox-harness", "runtime"),
        },
        Settings = new Dictionary<string, string>
        {
            [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] = "0.05",
        },
    };

    private static TempDirectory CreateJobTrackPilotSource()
    {
        var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "src", "JobTrack.Api"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "tools", "JobTrack.SeedFixtures"));
        Directory.CreateDirectory(Path.Combine(dir.Path, ".codeybox-harness"));

        File.WriteAllText(Path.Combine(dir.Path, "src", "JobTrack.Api", "JobTrack.Api.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "src", "JobTrack.Api", "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            var dbPath = Environment.GetEnvironmentVariable("JOBTRACK_DB_PATH") ?? ".codeybox-harness/jobtrack.db";
            var keepalivePath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? ".codeybox-harness",
                "server.keepalive");
            var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
            _ = Task.Run(async () =>
            {
                while (File.Exists(keepalivePath))
                    await Task.Delay(100);
                lifetime.StopApplication();
            });

            app.MapGet("/", () => Results.Text("JobTrack deployment ready", "text/plain"));
            app.MapGet("/healthz", () =>
                File.Exists(dbPath)
                    ? Results.Ok(new { status = "ready", app = "JobTrack" })
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            app.MapGet("/api/jobs", () =>
                File.Exists(dbPath)
                    ? Results.Text(File.ReadAllText(dbPath), "text/plain")
                    : Results.NotFound());

            app.Run();
            """);
        File.WriteAllText(Path.Combine(dir.Path, "tools", "JobTrack.SeedFixtures", "JobTrack.SeedFixtures.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir.Path, "tools", "JobTrack.SeedFixtures", "Program.cs"), """
            var seed = "0";
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--seed")
                    seed = args[i + 1];
            }

            var dbPath = Environment.GetEnvironmentVariable("JOBTRACK_DB_PATH") ?? ".codeybox-harness/jobtrack.db";
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.WriteAllText(dbPath, $"seed={seed}{Environment.NewLine}source=JobTrack.SeedFixtures{Environment.NewLine}");
            """);
        return dir;
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

    private sealed class RoutableProcessSandbox(ISandbox inner) : IRoutableSandbox, ISandboxPortPublisher
    {
        public string Id => inner.Id;
        public string? HostAddress => "127.0.0.1";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => inner.ExecAsync(exec, ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => inner.KillActiveExecsAsync(ct);

        public bool CanPublishPort(int port) => port is >= 1 and <= 65535;

        public SandboxPublishedPort PublishPort(int port)
            => new(
                HostAddress!,
                port,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["endpoint.scope"] = "host-routable",
                });

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}
