using CodeyBox.Api;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
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
    }

    private sealed class MechanicalFixerWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-mechanical-wiring-{Guid.NewGuid():N}.db");

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
            base.Dispose(disposing);
        }
    }
}
