using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Minimal test host for the CodeyBox API. Each instance gets an isolated
/// in-memory SQLite store so test methods don't share state. Dispose to clean up.
/// </summary>
internal sealed class WorkItemApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private readonly bool _ownsDbPath;
    private readonly Project[] _projects;

    public SqliteWorkItemStore Store { get; }

    public WorkItemApiFactory(string? dbPath = null, params Project[] projects)
    {
        _dbPath = dbPath ?? Path.Combine(
            Path.GetTempPath(), $"codeybox-httptest-{Guid.NewGuid():N}.db");
        _ownsDbPath = dbPath is null;
        _projects = projects.Length > 0
            ? projects
            :
            [
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                },
                new Project
                {
                    Id = new ProjectId("second-project"),
                    DisplayName = "Second Project",
                    RepositoryUrl = "https://github.com/test/repo2",
                },
            ];
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable bearer-token auth so tests don't need to supply a key.
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                // Temp paths so we don't need /var/lib/codeybox to exist.
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            // Stop the orchestrator background service from running in tests.
            services.RemoveAll<IHostedService>();

            // Replace the persistent store with our pre-created test instance.
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);

            // Replace the file-backed project repository with an in-memory stub.
            // "test-project" is the primary project used by most tests.
            // "second-project" is seeded so cross-project uniqueness tests can verify that
            // the same externalId is allowed in two different projects.
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(_projects));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Store.Dispose();
            if (_ownsDbPath)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
