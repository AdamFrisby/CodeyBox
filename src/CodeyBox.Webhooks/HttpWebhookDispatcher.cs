using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Webhooks;

/// <summary>
/// Delivers webhook events to configured HTTP endpoints using a background
/// channel-drain loop. <see cref="PublishAsync"/> is fire-and-forget — it
/// writes to the channel and returns immediately so the pipeline is never
/// blocked by webhook latency or failures.
/// </summary>
public sealed class HttpWebhookDispatcher : IWebhookDispatcher, IAsyncDisposable
{
    private readonly WebhookDispatcherOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpWebhookDispatcher> _log;
    private readonly Channel<WebhookEvent> _channel;
    private readonly Task _backgroundTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpWebhookDispatcher(
        WebhookDispatcherOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpWebhookDispatcher> log)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _log = log;
        _channel = Channel.CreateUnbounded<WebhookEvent>(new UnboundedChannelOptions { SingleReader = true });
        _backgroundTask = Task.Run(DrainAsync);
    }

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
    {
        _channel.Writer.TryWrite(evt);
        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync())
        {
            var body = BuildPayload(evt);
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            var matchingEndpoints = _options.Endpoints
                .Where(ep => MatchesFilter(ep, evt.Event))
                .ToList();

            await Task.WhenAll(matchingEndpoints.Select(ep =>
                DispatchToEndpointAsync(ep, evt, body, bodyBytes, CancellationToken.None)));
        }
    }

    private async Task DispatchToEndpointAsync(
        WebhookEndpointConfig ep,
        WebhookEvent evt,
        string body,
        byte[] bodyBytes,
        CancellationToken ct)
    {
        _ = body; // payload is captured via bodyBytes; body kept for future debug use

        string? signature = null;
        if (ep.SecretEnvVar is not null)
        {
            var secret = Environment.GetEnvironmentVariable(ep.SecretEnvVar) ?? string.Empty;
            signature = ComputeSignature(bodyBytes, secret);
        }

        var backoff = TimeSpan.FromSeconds(ep.InitialBackoffSeconds);
        HttpStatusCode? lastStatus = null;

        for (var attempt = 1; attempt <= ep.MaxAttempts; attempt++)
        {
            try
            {
                using var request = BuildRequest(ep, evt, bodyBytes, signature);
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(ep.TimeoutSeconds);

                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    return;

                lastStatus = response.StatusCode;
                _log.LogWarning(
                    "Webhook {Endpoint} returned {Status} for delivery {DeliveryId} event {Event} (attempt {Attempt}/{Max})",
                    ep.Name, (int)response.StatusCode, evt.DeliveryId, evt.Event, attempt, ep.MaxAttempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Webhook {Endpoint} threw on delivery {DeliveryId} event {Event} (attempt {Attempt}/{Max})",
                    ep.Name, evt.DeliveryId, evt.Event, attempt, ep.MaxAttempts);
            }

            if (attempt < ep.MaxAttempts)
                await Task.Delay(backoff, ct);
            else
                _log.LogWarning(
                    "Webhook {Endpoint} gave up after {Max} attempts for event {Event} delivery {DeliveryId}; last status {Status}",
                    ep.Name, ep.MaxAttempts, evt.Event, evt.DeliveryId, lastStatus.HasValue ? (int)lastStatus.Value : 0);

            backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
        }
    }

    private static HttpRequestMessage BuildRequest(
        WebhookEndpointConfig ep,
        WebhookEvent evt,
        byte[] bodyBytes,
        string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ep.Url);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add("X-CodeyBox-Event", evt.Event);
        request.Headers.Add("X-CodeyBox-Delivery", evt.DeliveryId.ToString());
        if (signature is not null)
            request.Headers.Add("X-CodeyBox-Signature", $"sha256={signature}");
        return request;
    }

    public static string BuildPayload(WebhookEvent evt)
    {
        var payload = new WebhookPayload(
            Event: evt.Event,
            OccurredAt: evt.OccurredAt,
            WorkItem: MapWorkItem(evt.WorkItem),
            Project: new WebhookProjectPayload(
                evt.Project.Id.Value,
                evt.Project.DisplayName,
                evt.Project.RepositoryUrl),
            Details: evt.Details);

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static WebhookWorkItemPayload MapWorkItem(WorkItem item) => new(
        Id: item.Id.ToString(),
        ProjectId: item.ProjectId.Value,
        Title: item.Title,
        Agent: (item.Agent ?? AgentKind.Claude).Value,
        BaseBranch: item.BaseBranch,
        WorkBranch: item.WorkBranch,
        State: item.State.ToString(),
        CreatedAt: item.CreatedAt,
        UpdatedAt: item.UpdatedAt,
        LastError: item.LastError,
        UpstreamPushAttempts: item.UpstreamPushAttempts);

    public static string ComputeSignature(byte[] bodyBytes, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool MatchesFilter(WebhookEndpointConfig ep, string eventName)
        => ep.EventFilter.Count == 0 || ep.EventFilter.Contains(eventName, StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _backgroundTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Webhook dispatcher drain timed out; some in-flight deliveries may be lost");
        }
    }
}

// ── Internal payload DTOs ────────────────────────────────────────────────────

internal sealed record WebhookPayload(
    string Event,
    DateTimeOffset OccurredAt,
    WebhookWorkItemPayload WorkItem,
    WebhookProjectPayload Project,
    object? Details);

internal sealed record WebhookWorkItemPayload(
    string Id,
    string ProjectId,
    string Title,
    string Agent,
    string? BaseBranch,
    string? WorkBranch,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    int UpstreamPushAttempts);

internal sealed record WebhookProjectPayload(
    string Id,
    string DisplayName,
    string RepositoryUrl);
