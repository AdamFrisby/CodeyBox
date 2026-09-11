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
    public List<IKnob> AdditionalKnobs { get; } = new();
    public string? TemplateDirectory { get; set; }
    public int? MaxTemplateChecks { get; set; }

    /// <summary>
    /// Optional override for the worker-progress watchdog's per-turn progress
    /// budget (<c>CodeyBox:WorkerProgressWatchdog:ProgressTimeout</c>). Set
    /// alongside <see cref="ItemStaleTimeoutOverride"/> when a test needs a
    /// shorter-than-default stale window: the shipped ordering validation
    /// requires the item-stale window to stay above the per-turn budget, so
    /// both must move together. Null (default) leaves the configured value
    /// untouched.
    /// </summary>
    public TimeSpan? WorkerProgressTimeoutOverride { get; set; }

    /// <summary>
    /// Optional override for the item-stale window
    /// (<c>CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout</c>). Tests that
    /// exercise the stale-worker retry fence must pin this explicitly rather
    /// than relying on the shipped default, which moves for operational
    /// reasons (e.g. audit-budget ordering). Null (default) leaves the
    /// configured value untouched.
    /// </summary>
    public TimeSpan? ItemStaleTimeoutOverride { get; set; }
    public Func<SqliteWorkItemStore, IWorkItemStore>? WorkItemStoreDecorator { get; set; }

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
            var values = new Dictionary<string, string?>
            {
                // Disable bearer-token auth so tests don't need to supply a key.
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                // Temp paths so we don't need /var/lib/codeybox to exist.
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                ["CodeyBox:TemplateDirectory"] = TemplateDirectory,
            };
            if (MaxTemplateChecks is { } maxTemplateChecks)
                values["CodeyBox:MaxTemplateChecks"] = maxTemplateChecks.ToString();
            if (WorkerProgressTimeoutOverride is { } progressTimeout)
                values["CodeyBox:WorkerProgressWatchdog:ProgressTimeout"] = progressTimeout.ToString();
            if (ItemStaleTimeoutOverride is { } itemStaleTimeout)
                values["CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout"] = itemStaleTimeout.ToString();
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
        if (disposing)
        {
            Store.Dispose();
            if (_ownsDbPath)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
