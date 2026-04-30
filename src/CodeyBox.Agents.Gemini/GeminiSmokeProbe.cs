using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Smoke-tests Gemini (Google AI) credentials by issuing a minimal
/// generateContent request directly to the Generative Language API.
///
/// <para>Reads <c>GEMINI_API_KEY</c> from the credential bundle and sends it
/// as the <c>x-goog-api-key</c> header. Uses the <c>agent-smoke</c> named
/// HTTP client. Never logs the API key or credential values.</para>
/// </summary>
public sealed class GeminiSmokeProbe : IAgentSmokeProbe
{
    internal const string GenerateContentEndpoint =
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiSmokeProbe> _log;

    public AgentKind Kind => AgentKind.Gemini;

    public GeminiSmokeProbe(IHttpClientFactory httpClientFactory, ILogger<GeminiSmokeProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            credential.EnvironmentVariables.TryGetValue("GEMINI_API_KEY", out var apiKey);
            if (string.IsNullOrEmpty(apiKey))
                return Fail("no token in credential bundle", sw);

            var client = _httpClientFactory.CreateClient("agent-smoke");
            using var request = new HttpRequestMessage(HttpMethod.Post, GenerateContentEndpoint);

            // Do NOT log this header — it contains the API key.
            request.Headers.Add("x-goog-api-key", apiKey);

            request.Content = new StringContent(
                """{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"maxOutputTokens":1}}""",
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
            _log.LogDebug(ex, "Gemini smoke probe threw; treating as transient");
            return Fail("transient: try later", sw);
        }
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed);
    }
}
