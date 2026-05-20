using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class PipelineOptionsWiringTests
{
    [Fact]
    public void ProgramMapsConfiguredPhaseAbsoluteTimeoutMultiplierIntoPipelineOptions()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PhaseAbsoluteTimeoutMultiplier"] = "2.5",
        });

        var options = factory.Services.GetRequiredService<PipelineOptions>();

        Assert.Equal(2.5, options.PhaseAbsoluteTimeoutMultiplier);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ProgramRejectsInvalidPhaseAbsoluteTimeoutMultiplier(string value)
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PhaseAbsoluteTimeoutMultiplier"] = value,
        });

        var ex = Assert.Throws<OptionsValidationException>(() =>
            _ = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value);

        Assert.Contains("PhaseAbsoluteTimeoutMultiplier", ex.Message);
    }

    private sealed class PipelineOptionsWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-pipeline-options-{Guid.NewGuid():N}.db");

        public PipelineOptionsWiringFactory(Dictionary<string, string?> extraConfig)
        {
            _extraConfig = extraConfig;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                var baseConfig = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                };
                foreach (var kvp in _extraConfig)
                    baseConfig[kvp.Key] = kvp.Value;
                cfg.AddInMemoryCollection(baseConfig);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
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
