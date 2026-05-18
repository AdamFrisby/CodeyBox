using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests.Uat.SandboxProviders;

internal sealed class RecordingLogger<T> : ILogger<T>, ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToList();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

internal sealed class RecordingMultipassRunner : IProcessRunner
{
    private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<RunResult>> _handler;

    public RecordingMultipassRunner(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<RunResult>> handler)
    {
        _handler = handler;
    }

    public ConcurrentQueue<MultipassCall> Calls { get; } = new();

    public async Task<RunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null)
    {
        Calls.Enqueue(new MultipassCall(argv.ToArray(), stdin, maxStdoutBytes, maxStderrBytes));
        return await _handler(argv, stdin, ct);
    }
}

internal sealed record MultipassCall(
    IReadOnlyList<string> Argv,
    string? Stdin,
    int? MaxStdoutBytes = null,
    int? MaxStderrBytes = null);

internal sealed class SandboxProviderApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _configuration;
    private readonly ISandboxProvider? _sandboxProvider;
    private readonly SandboxLeakReaper? _reaper;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-uat-sandbox-api-{Guid.NewGuid():N}.db");

    public SandboxProviderApiFactory(
        string environment = "Development",
        Dictionary<string, string?>? configuration = null,
        ISandboxProvider? sandboxProvider = null,
        SandboxLeakReaper? reaper = null,
        IWebhookDispatcher? webhooks = null)
    {
        _environment = environment;
        _configuration = configuration ?? [];
        _sandboxProvider = sandboxProvider;
        _reaper = reaper;
        _webhooks = webhooks;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            var defaults = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                ["CodeyBox:Changelog:Enabled"] = "false",
            };

            foreach (var (key, value) in _configuration)
                defaults[key] = value;

            cfg.AddInMemoryCollection(defaults);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(_ => new SqliteWorkItemStore(_dbPath));
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(new Project
            {
                Id = new ProjectId("sandbox-uat"),
                DisplayName = "Sandbox UAT",
                RepositoryUrl = "https://example.invalid/repo.git",
            }));

            if (_sandboxProvider is not null)
            {
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton(_sandboxProvider);
            }

            if (_reaper is not null)
            {
                services.RemoveAll<SandboxLeakReaper>();
                services.AddSingleton(_reaper);
            }

            if (_webhooks is not null)
            {
                services.RemoveAll<IWebhookDispatcher>();
                services.AddSingleton(_webhooks);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            try { File.Delete(_dbPath); } catch { }
    }
}

internal sealed class UatSandboxProvider : ISandboxProvider
{
    private readonly List<ManagedSandboxInfo> _managed = [];
    private readonly HashSet<string> _throwOnDispose = new(StringComparer.Ordinal);
    private bool _throwOnList;

    public string Name => "uat";
    public List<string> DisposedNames { get; } = [];

    public void Add(ManagedSandboxInfo info) => _managed.Add(info);
    public void ThrowOnDispose(string name) => _throwOnDispose.Add(name);
    public void ThrowOnList() => _throwOnList = true;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("UAT provider does not create sandboxes");

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        if (_throwOnList)
            throw new InvalidOperationException("list failed");
        return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_managed.ToList());
    }

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (_throwOnDispose.Contains(name))
            throw new InvalidOperationException("dispose failed");
        DisposedNames.Add(name);
        return Task.CompletedTask;
    }
}
