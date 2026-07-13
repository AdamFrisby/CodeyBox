using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Deployment;
using CodeyBox.Projects;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class DeploymentProgramWiringTests
{
    [Fact]
    public async Task ProgramRegistersDeploymentCompositionRoot()
    {
        using var factory = new DeploymentProgramWiringFactory();

        var drivers = factory.Services.GetServices<IDeploymentDriver>()
            .Select(d => d.Kind)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [DeploymentKinds.Cli, DeploymentKinds.Daemon, DeploymentKinds.Library, DeploymentKinds.WebApp],
            drivers);

        var registry = factory.Services.GetRequiredService<IDeploymentDriverRegistry>();
        Assert.True(registry.TryGet(DeploymentKinds.WebApp, out _));
        Assert.True(registry.TryGet(DeploymentKinds.Daemon, out _));
        Assert.True(registry.TryGet(DeploymentKinds.Cli, out _));
        Assert.True(registry.TryGet(DeploymentKinds.Library, out _));

        Assert.IsType<DeploymentManager>(factory.Services.GetRequiredService<IDeploymentManager>());
        Assert.IsType<DeploymentLeakReaper>(factory.Services.GetRequiredService<DeploymentLeakReaper>());
        Assert.True(factory.HadDeploymentLeakReaperHostedRegistration);

        var repo = factory.Services.GetRequiredService<IProjectRepository>();
        Assert.IsType<ProjectRepository>(repo);
        var project = await repo.GetAsync(new ProjectId("alpha"));
        Assert.NotNull(project?.Deployment);
        Assert.Equal(DeploymentKinds.WebApp, project!.Deployment!.Kind);
    }

    [Fact]
    public void ProgramBuildMultipassRemoteOptions_FallsBackToGlobalSandboxNetworkProfiles()
    {
        var options = new CodeyBoxOptions
        {
            MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
            {
                SshTarget = "codeybox@remote.example",
            },
            SandboxNetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deploy-isolated"] = "cb-deploy",
            },
        };

        var remote = MultipassRemoteOptionsMapper.Map(
            options.MultipassRemoteSandbox, options.SandboxNetworkProfiles);

        Assert.Equal("cb-deploy", remote.NetworkProfiles["deploy-isolated"]);
        Assert.Equal("cb-deploy", remote.NetworkProfiles["DEPLOY-ISOLATED"]);
    }

    [Fact]
    public void ProgramBuildMultipassRemoteOptions_UsesRemoteNetworkProfilesWhenConfigured()
    {
        var options = new CodeyBoxOptions
        {
            MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
            {
                SshTarget = "codeybox@remote.example",
                NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["deploy-isolated"] = "remote-deploy-bridge",
                },
            },
            SandboxNetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deploy-isolated"] = "local-deploy-bridge",
            },
        };

        var remote = MultipassRemoteOptionsMapper.Map(
            options.MultipassRemoteSandbox, options.SandboxNetworkProfiles);

        Assert.Equal("remote-deploy-bridge", remote.NetworkProfiles["deploy-isolated"]);
        Assert.Equal("remote-deploy-bridge", remote.NetworkProfiles["DEPLOY-ISOLATED"]);
    }

    private sealed class DeploymentProgramWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Serilog.ILogger _previousLogger = Log.Logger;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-deployment-wiring-{Guid.NewGuid():N}.db");

        public bool HadDeploymentLeakReaperHostedRegistration { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Projects:0:Id"] = "alpha",
                    ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.com/alpha.git",
                    ["CodeyBox:Projects:0:Deployment:Kind"] = DeploymentKinds.WebApp,
                    ["CodeyBox:Projects:0:Deployment:ImageReference"] = "ubuntu-22.04",
                    ["CodeyBox:Projects:0:Deployment:RunCommand"] = "nohup ./server &",
                    ["CodeyBox:Projects:0:Deployment:Ports:0"] = "8080",
                    ["CodeyBox:Projects:0:Deployment:HealthEndpoint"] = "/healthz",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                HadDeploymentLeakReaperHostedRegistration = HasDeploymentLeakReaperHostedRegistration(services);

                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(new FakeDeploymentSandboxProvider());
            });
        }

        private static bool HasDeploymentLeakReaperHostedRegistration(IServiceCollection services)
        {
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = false,
            });
            try
            {
                foreach (var descriptor in services.Where(sd => sd.ServiceType == typeof(IHostedService)))
                {
                    if (descriptor.ImplementationFactory is null)
                        continue;
                    try
                    {
                        if (descriptor.ImplementationFactory(provider) is DeploymentLeakReaper)
                            return true;
                    }
                    catch
                    {
                        // Other hosted-service factories can depend on test-removed
                        // services. They are irrelevant to this assertion.
                    }
                }
                return false;
            }
            finally
            {
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    // Program closes its process-global logger when the test
                    // host stops. Restore the logger that belonged to the
                    // surrounding test so later audit assertions retain their
                    // sink even when xUnit constructs collection members early.
                    Log.Logger = _previousLogger;
                    try { File.Delete(_dbPath); } catch { /* best-effort */ }
                }
            }
        }
    }
}
