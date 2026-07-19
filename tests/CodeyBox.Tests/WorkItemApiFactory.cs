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
/// Minimal test host for the CodeyBox API. By default each instance owns an isolated
/// file-backed SQLite store under its temp root; a caller-supplied database path remains caller-owned.
/// </summary>
internal sealed class WorkItemApiFactory : CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath;
    private readonly bool _ownsDbPath;
    private readonly Project[] _projects;

    public SqliteWorkItemStore Store { get; }
    public List<IKnob> AdditionalKnobs { get; } = new();
    public string? TemplateDirectory { get; set; }
    public int? MaxTemplateChecks { get; set; }
    public Func<SqliteWorkItemStore, IWorkItemStore>? WorkItemStoreDecorator { get; set; }

    public WorkItemApiFactory(string? dbPath = null, params Project[] projects)
    {
        _dbPath = dbPath ?? TempDatabasePath("codeybox-httptest");
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
            var values = new Dictionary<string, string?>
            {
                // Disable bearer-token auth so tests don't need to supply a key.
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                // Temp paths so we don't need /var/lib/codeybox to exist.
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Temp.NewDirectoryPath("test-git-"),
                ["CodeyBox:AuditLog:Path"] = Temp.NewLogPath("test-log"),
                ["CodeyBox:AuditLog:AuditPath"] = Temp.NewLogPath("test-audit"),
                ["CodeyBox:AgentStreams:Path"] = Temp.NewDirectoryPath("test-agent-streams-"),
                ["CodeyBox:TemplateDirectory"] = TemplateDirectory,
            };
            if (MaxTemplateChecks is { } maxTemplateChecks)
                values["CodeyBox:MaxTemplateChecks"] = maxTemplateChecks.ToString();
            cfg.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            // Stop the orchestrator background service from running in tests.
            services.RemoveAll<IHostedService>();

            // Replace the persistent store with our pre-created test instance.
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStoreDecorator?.Invoke(Store) ?? Store);

            // Replace the file-backed project repository with an in-memory stub.
            // "test-project" is the primary project used by most tests.
            // "second-project" is seeded so cross-project uniqueness tests can verify that
            // the same externalId is allowed in two different projects.
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(_projects));

            foreach (var knob in AdditionalKnobs)
                services.AddSingleton(knob);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        TestTempArtifacts.CleanupAll(
            Store.Dispose,
            () => base.Dispose(disposing),
            () =>
            {
                if (_ownsDbPath)
                    TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
            });
    }
}
