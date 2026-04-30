using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Smoke-tests Codex (OpenAI) credentials by sending a single-token
/// chat-completions request directly to the OpenAI API.
///
/// <para>Reads <c>OPENAI_API_KEY</c> from the credential bundle and sends it
/// as <c>Authorization: Bearer</c>. Uses the <c>agent-smoke</c> named HTTP
/// client. Never logs the Authorization header or credential values.</para>
/// </summary>
public sealed class CodexSmokeProbe : IAgentSmokeProbe
{
    internal const string CompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    internal const string ProbeModel = "gpt-4o-mini";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CodexSmokeProbe> _log;

    public AgentKind Kind => AgentKind.Codex;

    public CodexSmokeProbe(IHttpClientFactory httpClientFactory, ILogger<CodexSmokeProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            credential.EnvironmentVariables.TryGetValue("OPENAI_API_KEY", out var apiKey);
            if (string.IsNullOrEmpty(apiKey))
                return Fail("no token in credential bundle", sw);

            var client = _httpClientFactory.CreateClient("agent-smoke");
            using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsEndpoint);

            // Do NOT log the Authorization header — it contains the API key.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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
            _log.LogDebug(ex, "Codex smoke probe threw; treating as transient");
            return Fail("transient: try later", sw);
        }
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed);
    }
}
