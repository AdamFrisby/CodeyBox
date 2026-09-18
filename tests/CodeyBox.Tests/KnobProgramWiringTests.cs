using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator.Knobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class KnobProgramWiringTests
{
    [Fact]
    public async Task ProgramWiresProjectRepositoryWithConfiguredKnobsAndRealRegistry()
    {
        using var factory = new KnobWiringFactory();

        var registry = factory.Services.GetRequiredService<IKnobRegistry>();
        Assert.True(registry.TryGet(ChangeScopeKnob.KeyName, out _));

        var project = await factory.Services
            .GetRequiredService<IProjectRepository>()
            .GetAsync(new ProjectId("p"));

        Assert.NotNull(project);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, project!.Knobs[ChangeScopeKnob.KeyName]);
    }

    private sealed class KnobWiringFactory : WebApplicationFactory<Program>
    {
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-knob-wiring-");
        private string _dbPath => _scratch.DbPath("knob-wiring.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
                    ["CodeyBox:Defaults:Knobs:changeScope"] = ChangeScopeKnob.ValueSurgical,
                    ["CodeyBox:Projects:0:Id"] = "p",
                    ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.invalid/repo.git",
                    ["CodeyBox:Projects:0:Knobs:changeScope"] = ChangeScopeKnob.ValueRefactor,
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
                try { File.Delete(_dbPath); } catch { }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            base.Dispose(disposing);
        }
    }
}
