using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class MechanicalFixerProgramWiringTests
{
    [Fact]
    public async Task ProgramWiresDotnetFormatFixerForCSharpProject()
    {
        using var factory = new MechanicalFixerWiringFactory();

        var project = await factory.Services
            .GetRequiredService<IProjectRepository>()
            .GetAsync(new ProjectId("p"));
        var fixers = factory.Services
            .GetRequiredService<ProjectMechanicalFixerComposer>()
            .Compose(project!);

        Assert.IsType<DotnetFormatMechanicalFixer>(Assert.Single(fixers));
        Assert.IsType<DotnetFormatMechanicalFixerInputProvider>(
            Assert.Single(factory.Services.GetServices<IMechanicalFixerInputProvider>()));

        var runner = factory.Services.GetRequiredService<PipelineRunner>();
        var wiredComposer = typeof(PipelineRunner)
            .GetField("_mechanicalFixerComposer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runner);
        Assert.Same(
            factory.Services.GetRequiredService<ProjectMechanicalFixerComposer>(),
            wiredComposer);

        var wiredInputProviders = (IReadOnlyList<IMechanicalFixerInputProvider>)typeof(PipelineRunner)
            .GetField("_mechanicalFixerInputProviders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runner)!;
        Assert.IsType<DotnetFormatMechanicalFixerInputProvider>(Assert.Single(wiredInputProviders));
    }

    private sealed class MechanicalFixerWiringFactory : WebApplicationFactory<Program>
    {
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-mechanical-wiring-");
        private string _dbPath => _scratch.DbPath("mechanical-wiring.db");

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
                    ["CodeyBox:Projects:0:Id"] = "p",
                    ["CodeyBox:Projects:0:RepositoryUrl"] = "https://example.invalid/repo.git",
                    ["CodeyBox:Projects:0:Audit:Languages:0"] = "csharp",
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
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            base.Dispose(disposing);
        }
    }
}
