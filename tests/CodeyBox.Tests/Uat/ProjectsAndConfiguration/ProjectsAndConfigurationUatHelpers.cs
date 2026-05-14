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
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests.Uat.ProjectsAndConfiguration;

internal sealed class ProjectsAndConfigurationApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly bool _disableAuth;
    private readonly Dictionary<string, string?> _configuration;
    private readonly IProjectRepository? _projects;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-projects-config-uat-{Guid.NewGuid():N}.db");

    public ProjectsAndConfigurationApiFactory(
        string environment = "Development",
        bool disableAuth = true,
        Dictionary<string, string?>? configuration = null,
        IProjectRepository? projects = null)
    {
        _environment = environment;
        _disableAuth = disableAuth;
        _configuration = configuration ?? [];
        _projects = projects;
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        ReleaseStore = new SqliteReleaseStore(_dbPath);
    }

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteReleaseStore ReleaseStore { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.Sources.Clear();
            var tmp = Path.GetTempPath();
            var config = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = _disableAuth ? "true" : "false",
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                ["CodeyBox:Changelog:Enabled"] = "false",
            };

            foreach (var (key, value) in _configuration)
                config[key] = value;

            cfg.AddInMemoryCollection(config);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);
            services.RemoveAll<IReleaseStore>();
            services.AddSingleton<IReleaseStore>(ReleaseStore);

            if (_projects is not null)
            {
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton(_projects);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            ReleaseStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}

internal sealed class UatLogCapture : ILogger
{
    public sealed record Entry(LogLevel Level, string Message);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

internal static class ProjectsAndConfigurationFixtures
{
    public static Project Project(
        string id,
        string displayName,
        string repositoryUrl,
        ProjectReleaseConfig? releaseConfig = null)
        => new()
        {
            Id = new ProjectId(id),
            DisplayName = displayName,
            RepositoryUrl = repositoryUrl,
            ReleaseConfig = releaseConfig ?? new ProjectReleaseConfig(),
        };
}
