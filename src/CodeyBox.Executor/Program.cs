using CodeyBox.Core;
using CodeyBox.Orchestrator;
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

// Sandbox providers resolve through the shared registry composition — the
// same factory the orchestrator uses — so every registered kind is
// available here with no executor-side switch to extend. Provider options
// bind from the executor's own CodeyBox:Executor section (see
// ExecutorOptions), and one host may declare several kinds.
builder.Services.AddExecutorSandboxProviders();

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
        log: sp.GetRequiredService<ILogger<ExecutorClient>>(),
        providerRegistry: sp.GetRequiredService<ISandboxProviderRegistry>());
});
builder.Services.AddHostedService<ExecutorWorker>();
builder.Services.AddHostedService<ExecutorStartupValidator>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

/// <summary>
/// Fail-fast startup validation: a bad host id, orchestrator URL, provider
/// kind, or unimplemented capability surfaces here instead of registering
/// garbage. Warms every declared provider kind through the shared registry
/// so an unknown or unusable kind fails the host fast with the registered
/// kinds named. Runs before <see cref="ExecutorWorker"/>
/// because hosted services start in registration order.
/// </summary>
internal sealed class ExecutorStartupValidator : IHostedService
{
    private readonly Func<ExecutorOptions> _options;
    private readonly ISandboxProviderRegistry _registry;
    private readonly ILogger<ExecutorWorker> _log;

    public ExecutorStartupValidator(
        Func<ExecutorOptions> options,
        ISandboxProviderRegistry registry,
        ILogger<ExecutorWorker> log)
    {
        _options = options;
        _registry = registry;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var options = _options();
        var providers = ExecutorSandboxStartup.Validate(options, _registry);
        _log.LogInformation(
            "Executor host {HostId} starting against {Orchestrator} (providers {Providers}, capacity {Capacity})",
            options.HostId.Trim(),
            options.OrchestratorBaseUrl.Trim(),
            string.Join(",", options.GetDeclaredKinds()),
            options.MaxConcurrentSandboxes?.ToString() ?? "uncapped");
        _log.LogInformation(
            "Executor sandbox provider capabilities: {Capabilities}",
            SandboxCapabilities.FormatMatrix(providers));
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
