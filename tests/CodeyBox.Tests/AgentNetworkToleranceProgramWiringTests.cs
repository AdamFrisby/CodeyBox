using System.Reflection;
using CodeyBox.Agents;
using CodeyBox.Agents.Codex;
using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class AgentNetworkToleranceProgramWiringTests
{
    [Fact]
    public async Task ProgramBindsAgentNetworkToleranceAndInjectsSameSnapshotIntoCodexRunner()
    {
        using var factory = new AgentNetworkToleranceWiringFactory();

        var snapshot = factory.Services.GetRequiredService<AgentNetworkToleranceSnapshot>();
        var codex = factory.Services.GetServices<IAgentRunner>().OfType<CodexAgentRunner>().Single();

        Assert.Same(snapshot, Field<AgentNetworkToleranceSnapshot>(codex, "_networkTolerance"));

        var tolerance = snapshot.GetTolerance("codex");
        Assert.NotNull(tolerance);
        Assert.Equal(21, tolerance!.RequestMaxRetries);
        Assert.Equal(22, tolerance.StreamMaxRetries);
        Assert.Equal(230000, tolerance.StreamIdleTimeoutMs);
        Assert.Equal("azure", tolerance.Provider);

        var sandbox = new CapturingSandbox();
        await codex.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        Assert.Contains("model_providers.azure.request_max_retries=21", argv);
        Assert.Contains("model_providers.azure.stream_max_retries=22", argv);
        Assert.Contains("model_providers.azure.stream_idle_timeout_ms=230000", argv);
    }

    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private sealed class AgentNetworkToleranceWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-network-tolerance-wiring-{Guid.NewGuid():N}.db");

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
                    ["CodeyBox:AgentNetworkTolerance:codex:RequestMaxRetries"] = "21",
                    ["CodeyBox:AgentNetworkTolerance:codex:StreamMaxRetries"] = "22",
                    ["CodeyBox:AgentNetworkTolerance:codex:StreamIdleTimeoutMs"] = "230000",
                    ["CodeyBox:AgentNetworkTolerance:codex:Provider"] = "azure",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
