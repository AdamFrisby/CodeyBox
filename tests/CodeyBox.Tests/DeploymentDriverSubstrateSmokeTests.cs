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
    [SkippableFact]
    public async Task WebApp_JobTrackPilot_Deploys_HealthChecks_Exposes_AndTearsDown_OnProcessSubstrate()
    {
        var source = Environment.GetEnvironmentVariable("JOBTRACK_SOURCE");
        Skip.If(
            string.IsNullOrWhiteSpace(source) || !Directory.Exists(source),
            "Set JOBTRACK_SOURCE to a real JobTrack checkout to run the deployment-driver JobTrack pilot.");

        var jobTrack = JobTrackRecipe.Default(Path.GetFullPath(source!));
        var provider = new RoutableProcessSandboxProvider();
        var driver = new WebAppDeploymentDriver();
        var recipe = ToDeploymentRecipe(jobTrack);

        await using var handle = await driver.DeployAsync(recipe, Ctx(provider, jobTrack.Mounts), CancellationToken.None);
        Assert.Equal(DeploymentEndpointKind.Http, handle.Endpoint.Kind);
        Assert.Equal(JobTrackRecipe.DefaultEntryUrl.Replace("localhost", "127.0.0.1", StringComparison.Ordinal), handle.Endpoint.Url);
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

    private static DeploymentContext Ctx(ISandboxProvider provider, IReadOnlyList<SandboxMount>? mounts = null) => new()
    {
        SandboxProvider = provider,
        Mounts = mounts ?? [],
        WorkingDirectory = "/work",
    };

    private static DeploymentRecipe ToDeploymentRecipe(WebAppRecipe recipe)
    {
        var entry = new Uri(recipe.EntryUrl);
        var buildCommands = recipe.BuildSteps
            .Where(step => !string.Equals(step.Label, "install-firefox", StringComparison.Ordinal))
            .Concat(recipe.SeedSteps)
            .Where(step => step.Command.Count > 0)
            .Select(step => string.Join(' ', step.Command.Select(QuoteShellWord)));
        var runCommand = string.Join(' ', recipe.RunCommand.Command.Select(QuoteShellWord));
        return new DeploymentRecipe
        {
            Kind = DeploymentKinds.WebApp,
            ImageReference = string.IsNullOrWhiteSpace(recipe.ImageReference) ? "ignored" : recipe.ImageReference,
            BuildCommand = string.Join(" && ", buildCommands),
            RunCommand = $"nohup {runCommand} >/tmp/jobtrack-deployment.log 2>&1 &",
            Ports = [entry.Port],
            HealthEndpoint = string.IsNullOrWhiteSpace(entry.AbsolutePath) ? "/" : entry.AbsolutePath,
            StartupTimeout = recipe.ReadinessTimeout,
            Environment = recipe.Environment,
            NetworkProfile = recipe.NetworkProfile,
            Settings = new Dictionary<string, string>
            {
                [WebAppDeploymentDriver.SettingsKeyProbeIntervalSeconds] =
                    Math.Max(0.05, recipe.ReadinessPollInterval.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private static string QuoteShellWord(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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
