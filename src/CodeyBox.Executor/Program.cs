using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ── Executor host process ────────────────────────────────────────────────────
// Runs on a machine other than the orchestrator. It connects OUTBOUND to the
// orchestrator (plain HTTPS POSTs via ExecutorClient) and never opens an
// inbound listening port, so it can sit behind NAT or a host firewall.
//
// This item delivers the process, its registration and its liveness:
// register + heartbeat into the existing worker registry, local sandbox
// provisioning through ISandboxProvider, and the retain-for-resume
// disconnect policy. Dispatching work to this host is a separate item — an
// executor that registers and heartbeats but is never sent work is the
// acceptance state, so no phase runner is wired here yet.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ExecutorOptions>(builder.Configuration.GetSection("CodeyBox:Executor"));
builder.Services.AddSingleton<Func<ExecutorOptions>>(sp =>
    () => sp.GetRequiredService<IOptionsMonitor<ExecutorOptions>>().CurrentValue);

builder.Services.AddSingleton<ISandboxProvider>(sp =>
{
    var options = sp.GetRequiredService<Func<ExecutorOptions>>()();
    var logFactory = sp.GetRequiredService<ILoggerFactory>();
    return (options.LocalSandboxProvider ?? "").Trim().ToLowerInvariant() switch
    {
        "bubblewrap" => new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions(),
            logFactory.CreateLogger<BubblewrapSandboxProvider>()),
        "process" => new ProcessSandboxProvider(
            logFactory.CreateLogger<ProcessSandboxProvider>()),
        var other => throw new InvalidOperationException(
            $"CodeyBox:Executor:LocalSandboxProvider must be 'process' or 'bubblewrap', not '{other}'."),
    };
});

builder.Services.AddSingleton<ExecutorSandboxTracker>();
builder.Services.AddHttpClient("executor");
builder.Services.AddSingleton<ExecutorClient>(sp =>
{
    var options = sp.GetRequiredService<Func<ExecutorOptions>>()();
    if (string.IsNullOrWhiteSpace(options.OrchestratorBaseUrl))
        throw new InvalidOperationException("CodeyBox:Executor:OrchestratorBaseUrl is required to run executor mode.");
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("executor");
    http.BaseAddress = new Uri(options.OrchestratorBaseUrl.Trim(), UriKind.Absolute);
    return new ExecutorClient(
        http,
        sp.GetRequiredService<Func<ExecutorOptions>>(),
        sp.GetRequiredService<ISandboxProvider>(),
        sp.GetRequiredService<ExecutorSandboxTracker>(),
        phaseRunner: null,
        log: sp.GetRequiredService<ILogger<ExecutorClient>>());
});
builder.Services.AddHostedService<ExecutorWorker>();
builder.Services.AddHostedService<ExecutorStartupValidator>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

/// <summary>
/// Fail-fast startup validation: a bad host id or orchestrator URL surfaces
/// here instead of registering garbage. Runs before <see cref="ExecutorWorker"/>
/// because hosted services start in registration order.
/// </summary>
internal sealed class ExecutorStartupValidator : IHostedService
{
    private readonly Func<ExecutorOptions> _options;
    private readonly ILogger<ExecutorWorker> _log;

    public ExecutorStartupValidator(Func<ExecutorOptions> options, ILogger<ExecutorWorker> log)
    {
        _options = options;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var options = _options();
        options.Validate();
        _log.LogInformation(
            "Executor host {HostId} starting against {Orchestrator} (provider {Provider}, capacity {Capacity})",
            options.HostId.Trim(),
            options.OrchestratorBaseUrl.Trim(),
            (options.LocalSandboxProvider ?? "").Trim().ToLowerInvariant(),
            options.MaxConcurrentSandboxes?.ToString() ?? "uncapped");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Runs the executor session (register once, then heartbeat until stopped).
/// No phase runner is wired in this item, so after registration the worker
/// idles: it registers and heartbeats but is never sent work. Graceful stop
/// tears down tracked sandboxes — retention applies to connection loss, not
/// to process exit, where nothing could resume them.
/// </summary>
internal sealed class ExecutorWorker : BackgroundService
{
    private readonly ExecutorClient _client;
    private readonly ExecutorSandboxTracker _tracker;
    private readonly ILogger<ExecutorWorker> _log;

    public ExecutorWorker(ExecutorClient client, ExecutorSandboxTracker tracker, ILogger<ExecutorWorker> log)
    {
        _client = client;
        _tracker = tracker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_client.HasPhaseRunner)
            _log.LogInformation("No phase runner wired; executor will register and heartbeat but accept no phases (dispatch is a separate item).");
        await _client.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct).ConfigureAwait(false);
        await _tracker.DisposeAsync().ConfigureAwait(false);
    }
}
