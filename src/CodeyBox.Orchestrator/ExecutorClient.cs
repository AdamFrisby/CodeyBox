using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Transport failure talking to the orchestrator. Carries the operation and
/// host only — never key material or request bodies.
/// </summary>
public sealed class ExecutorTransportException : Exception
{
    public ExecutorTransportException(string operation, string message)
        : base($"Executor {operation} failed: {message}")
    {
        Operation = operation;
    }

    public ExecutorTransportException(string operation, string message, Exception inner)
        : base($"Executor {operation} failed: {message}", inner)
    {
        Operation = operation;
    }

    public string Operation { get; }
}

/// <summary>
/// Outbound-only client through which an executor host process registers
/// itself, heartbeats into the existing worker registry, provisions sandboxes
/// locally, and runs phases. The client owns an <see cref="HttpClient"/> and
/// never binds a listening socket, so the executor needs no inbound port and
/// can sit behind NAT or a host firewall.
///
/// <para>Liveness reuses the existing dead-worker path: heartbeats flow into
/// <c>IWorkerRegistry</c> via the orchestrator's HTTP surface, and a dead
/// executor is reclaimed by the dead-worker reaper. The heartbeat timer here
/// is only the sender — it is not a second liveness scheme.</para>
///
/// <para>Connection loss never orphans a running sandbox: the tracker retains
/// every in-flight sandbox for resumption, and the next successful heartbeat
/// reconciles local inventory so nothing runs untracked (see
/// <see cref="ExecutorSandboxTracker"/> and
/// <see cref="ExecutorDisconnectPolicy"/>).</para>
/// </summary>
public sealed class ExecutorClient
{
    /// <summary>Maximum orchestrator response body buffered per call. Registration answers are tiny; anything larger is a protocol violation.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Func<ExecutorOptions> _optionsAccessor;
    private readonly ISandboxProvider _sandboxes;
    private readonly IPipelineRunner? _phaseRunner;
    private readonly ExecutorSandboxTracker _tracker;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExecutorClient> _log;

    private ExecutorOptions Options => _optionsAccessor();

    public ExecutorClient(
        HttpClient http,
        Func<ExecutorOptions> optionsAccessor,
        ISandboxProvider sandboxes,
        ExecutorSandboxTracker? tracker = null,
        IPipelineRunner? phaseRunner = null,
        TimeProvider? clock = null,
        ILogger<ExecutorClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _tracker = tracker ?? new ExecutorSandboxTracker();
        _phaseRunner = phaseRunner;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ExecutorClient>.Instance;
    }

    /// <summary>Local sandbox provider this executor provisions through. Never a remote provider: the executor runs work where it runs.</summary>
    public ISandboxProvider SandboxProvider => _sandboxes;

    /// <summary>Sandbox tracker implementing the disconnect retain/reconcile policy.</summary>
    public ExecutorSandboxTracker Tracker => _tracker;

    /// <summary>True when a phase runner is wired. Dispatch (a separate item) supplies it; without one the executor idles after registration.</summary>
    public bool HasPhaseRunner => _phaseRunner is not null;

    /// <summary>
    /// Provisions a sandbox locally through the injected provider abstraction.
    /// The executor never provisions remotely: it runs work on its own machine.
    /// </summary>
    public Task<ISandbox> ProvisionSandboxAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return _sandboxes.CreateAsync(spec, ct);
    }

    /// <summary>
    /// Executes a work-item phase through the phase-execution seam
    /// (<see cref="IPipelineRunner"/>). Throws
    /// <see cref="InvalidOperationException"/> when no runner is wired —
    /// dispatch wiring is a separate item, and failing fast beats pretending
    /// to run a phase that goes nowhere.
    /// </summary>
    public Task RunPhaseAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var runner = _phaseRunner
            ?? throw new InvalidOperationException(
                "No phase runner is wired on this executor; dispatch wiring arrives in a separate item. The executor registers and heartbeats but cannot accept phases yet.");
        return runner.RunAsync(item, ct, hostShutdownToken);
    }

    /// <summary>
    /// Registers (or re-registers) this host with the orchestrator. Outbound
    /// POST only. Safe to call repeatedly: the orchestrator upserts the
    /// registry row keyed by the stable worker id.
    /// </summary>
    public async Task<string> RegisterAsync(CancellationToken ct = default)
    {
        var options = Options;
        options.Validate();
        var registration = options.ToRegistration();
        using var request = new HttpRequestMessage(HttpMethod.Post, "executors/register")
        {
            Content = JsonContent.Create(
                new
                {
                    hostId = registration.HostId,
                    maxConcurrentSandboxes = registration.MaxConcurrentSandboxes,
                    allowedNetworkProfiles = registration.AllowedNetworkProfiles,
                    declaredCredentials = registration.DeclaredCredentials,
                    cordoned = registration.Cordoned,
                    healthy = registration.Healthy,
                    processId = Environment.ProcessId,
                },
                options: JsonOptions),
        };
        using var response = await SendAsync(request, "register", options, ct).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response, "register", ct).ConfigureAwait(false);
        var workerId = ExtractWorkerId(body);
        _log.LogInformation("Executor registered host {HostId} as {WorkerId}", registration.HostId, workerId);
        return workerId;
    }

    /// <summary>
    /// Heartbeats into the worker registry via the orchestrator. Outbound POST
    /// only. A transport failure throws <see cref="ExecutorTransportException"/>
    /// and leaves liveness to the existing dead-worker threshold — the caller
    /// retries on the next interval.
    /// </summary>
    public async Task HeartbeatAsync(string? currentWorkItemId, CancellationToken ct = default)
    {
        var options = Options;
        options.Validate();
        var hostId = options.HostId.Trim();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"executors/{Uri.EscapeDataString(hostId)}/heartbeat")
        {
            Content = JsonContent.Create(
                new { currentWorkItemId = string.IsNullOrWhiteSpace(currentWorkItemId) ? null : currentWorkItemId.Trim() },
                options: JsonOptions),
        };
        using var response = await SendAsync(request, "heartbeat", options, ct).ConfigureAwait(false);
        await ReadBoundedAsync(response, "heartbeat", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the executor session: registers once, then heartbeats until
    /// cancelled. A failed heartbeat marks the connection lost (tracked
    /// sandboxes are retained, never disposed); the first success afterwards
    /// reconciles local inventory so no sandbox runs untracked. Cancellation
    /// stops the loop; shutdown-time teardown belongs to the tracker owner.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var workerId = await RegisterAsync(ct).ConfigureAwait(false);
        var interval = Options.HeartbeatInterval;
        using var timer = new PeriodicTimer(interval, _clock);
        var wasDisconnected = false;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await HeartbeatAsync(null, ct).ConfigureAwait(false);
                if (wasDisconnected)
                {
                    wasDisconnected = false;
                    var outcome = await _tracker.ReconcileOnReconnectAsync(_sandboxes, ct).ConfigureAwait(false);
                    _log.LogInformation(
                        "Executor {WorkerId} reconnected: retained {Retained} sandboxes, reclaimed {Reclaimed} untracked",
                        workerId, outcome.RetainedCount, outcome.ReclaimedCount);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ExecutorTransportException or HttpRequestException or TaskCanceledException)
            {
                if (!wasDisconnected)
                {
                    wasDisconnected = true;
                    var retained = _tracker.MarkConnectionLost();
                    _log.LogWarning(
                        ex,
                        "Executor {WorkerId} lost orchestrator connection; retained {Retained} sandboxes for resumption",
                        workerId, retained.Count);
                }
                else
                {
                    _log.LogDebug(ex, "Executor {WorkerId} heartbeat failed while disconnected", workerId);
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        ExecutorOptions options,
        CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable(options.ApiKeyEnvVar);
        if (string.IsNullOrEmpty(key))
            throw new ExecutorTransportException(operation, $"API key env var '{options.ApiKeyEnvVar}' is not set.");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExecutorTransportException(operation, "request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExecutorTransportException(operation, ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ExecutorTransportException(operation, $"orchestrator answered {(int)response.StatusCode} ({status})");
        }
        return response;
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxResponseBytes)
            throw new ExecutorTransportException(operation, $"response declares {contentLength} bytes, over the {MaxResponseBytes}-byte cap");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
                throw new ExecutorTransportException(operation, $"response exceeds the {MaxResponseBytes}-byte cap");
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        using var reader = new StreamReader(bounded, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static string ExtractWorkerId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("workerId", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString()!;
        }
        catch (JsonException) { }
        throw new ExecutorTransportException("register", "orchestrator acknowledgement carried no worker id");
    }
}
