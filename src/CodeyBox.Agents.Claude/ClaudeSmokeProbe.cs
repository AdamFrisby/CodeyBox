using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Smoke-tests Claude credentials directly against Anthropic (not via the CLI
/// or sandbox).
///
/// <para>Supports both OAuth tokens (<c>CLAUDE_CODE_OAUTH_TOKEN</c>, sent as
/// <c>Authorization: Bearer</c> to the OAuth usage endpoint) and raw API keys
/// (<c>ANTHROPIC_API_KEY</c>, sent as <c>x-api-key</c> to a single-token
/// <c>/v1/messages</c> request). OAuth is tried first.</para>
///
/// <para>Uses the <c>agent-smoke</c> named HTTP client. Never logs the
/// Authorization header or credential values.</para>
/// </summary>
public sealed class ClaudeSmokeProbe : IAgentSmokeProbe
{
    internal const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    // OAuth-native usage endpoint — the same one ClaudeQuotaProbe and the Claude
    // Code client use. Validates a subscription OAuth token WITHOUT a raw inference
    // call, so it carries no account-termination risk (unlike /v1/messages with a
    // subscription Bearer token).
    internal const string OAuthUsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    internal const string AnthropicVersion = "2023-06-01";
    // Smallest widely-available Claude model; used only for the 1-token probe.
    internal const string ProbeModel = "claude-haiku-4-5-20251001";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeSmokeProbe> _log;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan> _maxRetryDelayProvider;

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeSmokeProbe(IHttpClientFactory httpClientFactory, ILogger<ClaudeSmokeProbe> log)
        : this(httpClientFactory, log, null, null)
    {
    }

    internal ClaudeSmokeProbe(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeSmokeProbe> log,
        TimeProvider? timeProvider)
        : this(httpClientFactory, log, timeProvider, null)
    {
    }

    public ClaudeSmokeProbe(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeSmokeProbe> log,
        TimeProvider? timeProvider,
        Func<TimeSpan>? maxRetryDelayProvider)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxRetryDelayProvider = maxRetryDelayProvider
            ?? (static () => ClaudeQuotaProbeResilienceOptions.DefaultMaxRetryDelay);
    }

    public async Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            credential.EnvironmentVariables.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var oauthToken);
            credential.EnvironmentVariables.TryGetValue("ANTHROPIC_API_KEY", out var apiKey);

            var hasAnyToken = !string.IsNullOrEmpty(oauthToken) || !string.IsNullOrEmpty(apiKey);
            if (!hasAnyToken)
                return Fail("no token in credential bundle", sw, SmokeFailureCategory.Persistent);

            var client = _httpClientFactory.CreateClient("agent-smoke");
            using var response = await SendWithRetryAfterAsync(client, oauthToken, apiKey, ct)
                .ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
                return new AgentSmokeResult(true, null, sw.Elapsed, SmokeFailureCategory.None);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AgentSmokeResult(false, "auth", sw.Elapsed, SmokeFailureCategory.Persistent);

            if ((int)response.StatusCode >= 500)
                return new AgentSmokeResult(false, "transient: try later", sw.Elapsed, SmokeFailureCategory.Transient);

            return new AgentSmokeResult(
                false, $"HTTP {(int)response.StatusCode}", sw.Elapsed, SmokeFailureCategory.Unknown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("timeout", sw, SmokeFailureCategory.Transient);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Claude smoke probe threw; treating as transient");
            return Fail("transient: try later", sw, SmokeFailureCategory.Transient);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAfterAsync(
        HttpClient client,
        string? oauthToken,
        string? apiKey,
        CancellationToken ct)
    {
        using var request = CreateRequest(oauthToken, apiKey);
        var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(oauthToken) || !ShouldRetryOAuthUsage(response))
            return response;

        var retryAfter = HttpQuotaRetryPolicy.TryGetRetryAfterDelay(
            response.Headers,
            _timeProvider.GetUtcNow());
        if (retryAfter is null)
            return response;

        var delay = HttpQuotaRetryPolicy.ComputeRetryDelay(
            TimeSpan.Zero,
            retryAfter,
            NormalizeMaxRetryDelay(_maxRetryDelayProvider()));
        response.Dispose();
        await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);

        using var retryRequest = CreateRequest(oauthToken, apiKey);
        return await client.SendAsync(retryRequest, ct).ConfigureAwait(false);
    }

    private static bool ShouldRetryOAuthUsage(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static TimeSpan NormalizeMaxRetryDelay(TimeSpan maxRetryDelay) =>
        maxRetryDelay > TimeSpan.Zero
            ? maxRetryDelay
            : ClaudeQuotaProbeResilienceOptions.DefaultMaxRetryDelay;

    private static HttpRequestMessage CreateRequest(string? oauthToken, string? apiKey)
    {
        if (!string.IsNullOrEmpty(oauthToken))
        {
            // Subscription OAuth token: validate via the OAuth-native usage
            // endpoint (what ClaudeQuotaProbe / the Claude Code client use). A
            // raw /v1/messages call with the subscription Bearer token would risk
            // account termination (wrong client shape) and is never made here.
            var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsageEndpoint);
            // Do NOT log the Authorization header — it contains the credential.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
            return request;
        }

        // A real ANTHROPIC_API_KEY (x-api-key) is a legitimate raw-API
        // credential, so a 1-token /v1/messages probe is appropriate.
        var apiKeyRequest = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
        apiKeyRequest.Headers.Add("x-api-key", apiKey!);
        apiKeyRequest.Headers.Add("anthropic-version", AnthropicVersion);
        apiKeyRequest.Content = new StringContent(
            $$"""{"model":"{{ProbeModel}}","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");
        return apiKeyRequest;
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw, SmokeFailureCategory category)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed, category);
    }
}
