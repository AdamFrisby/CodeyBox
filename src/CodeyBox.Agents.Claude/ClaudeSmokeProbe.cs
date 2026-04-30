using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Smoke-tests Claude credentials by sending a single-token /v1/messages
/// request directly to the Anthropic API (not via the CLI or sandbox).
///
/// <para>Supports both OAuth tokens (<c>CLAUDE_CODE_OAUTH_TOKEN</c>, sent as
/// <c>Authorization: Bearer</c>) and raw API keys (<c>ANTHROPIC_API_KEY</c>,
/// sent as <c>x-api-key</c>). OAuth is tried first.</para>
///
/// <para>Uses the <c>agent-smoke</c> named HTTP client. Never logs the
/// Authorization header or credential values.</para>
/// </summary>
public sealed class ClaudeSmokeProbe : IAgentSmokeProbe
{
    internal const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    internal const string AnthropicVersion = "2023-06-01";
    // Smallest widely-available Claude model; used only for the 1-token probe.
    internal const string ProbeModel = "claude-haiku-4-5-20251001";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeSmokeProbe> _log;

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeSmokeProbe(IHttpClientFactory httpClientFactory, ILogger<ClaudeSmokeProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
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
                return Fail("no token in credential bundle", sw);

            var client = _httpClientFactory.CreateClient("agent-smoke");
            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);

            // Do NOT log the Authorization header — it contains the credential.
            if (!string.IsNullOrEmpty(oauthToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
            else
                request.Headers.Add("x-api-key", apiKey!);

            request.Headers.Add("anthropic-version", AnthropicVersion);

            request.Content = new StringContent(
                $$"""{"model":"{{ProbeModel}}","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode)
                return new AgentSmokeResult(true, null, sw.Elapsed);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AgentSmokeResult(false, "auth", sw.Elapsed);

            if ((int)response.StatusCode >= 500)
                return new AgentSmokeResult(false, "transient: try later", sw.Elapsed);

            return new AgentSmokeResult(false, $"HTTP {(int)response.StatusCode}", sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("timeout", sw);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Claude smoke probe threw; treating as transient");
            return Fail("transient: try later", sw);
        }
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed);
    }
}
