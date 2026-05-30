using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Smoke-tests Gemini (Google AI) credentials by issuing a minimal
/// generateContent request.
///
/// <para>Authentication priority and endpoint:</para>
/// <list type="number">
///   <item><c>GEMINI_API_KEY</c> — sent as <c>x-goog-api-key</c> header against
///   the public Generative Language endpoint
///   (<see cref="ApiKeyGenerateContentEndpoint"/>).</item>
///   <item><c>CODEYBOX_GEMINI_OAUTH_CREDS_JSON</c> — the raw <c>~/.gemini/oauth_creds.json</c>
///   contents (env-var name sourced from
///   <c>CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar</c>);
///   the <c>access_token</c> field is extracted and sent as
///   <c>Authorization: Bearer &lt;token&gt;</c> against the Code Assist OAuth
///   endpoint (<see cref="OAuthGenerateContentEndpoint"/>). The public
///   Generative Language endpoint does <i>not</i> authenticate OAuth bearer
///   tokens — sending one returns 401/403 and an OAuth-only setup would always
///   fail the smoke gate. The Code Assist v1internal surface is the same
///   endpoint <c>GeminiAgentRunner.SendOAuthAsync</c>,
///   <c>GeminiQuotaProbe</c>, and <c>GeminiModelListProbe</c> already use for
///   the OAuth subscription path; the request body is wrapped in
///   <c>{model, request}</c> to match Code Assist's shape.</item>
/// </list>
///
/// <para>Uses the <c>agent-smoke</c> named HTTP client. Never logs the API key,
/// access token, or credential values. The 0 ms fast-fail path ("no token in
/// credential bundle") is reached only when neither auth method supplies a
/// usable token — this lets the OAuth credential bundle (populated by
/// <c>GeminiOAuthFileCredentialProvider</c>) pass smoke without requiring a
/// separate <c>GEMINI_API_KEY</c> env var.</para>
/// </summary>
public sealed class GeminiSmokeProbe : IAgentSmokeProbe
{
    // API-key path: the public Generative Language endpoint. Does NOT
    // authenticate OAuth bearer tokens — keep this for x-goog-api-key only.
    internal const string ApiKeyGenerateContentEndpoint =
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent";

    // OAuth subscription path: the Code Assist v1internal surface, the same
    // endpoint family GeminiQuotaProbe / GeminiModelListProbe / GeminiAgentRunner
    // already use for OAuth-personal. Code Assist wraps the GenerateContent
    // body in {model, request} (see SmokeRequestBodyOAuthJson below).
    internal const string OAuthGenerateContentEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

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
            if (!string.IsNullOrEmpty(apiKey))
                return await ProbeWithApiKeyAsync(apiKey!, ct, sw);

            if (credential.EnvironmentVariables.TryGetValue(CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar, out var oauthJson)
                && !string.IsNullOrEmpty(oauthJson))
            {
                var accessToken = ExtractAccessToken(oauthJson!, _log);
                if (!string.IsNullOrEmpty(accessToken))
                    return await ProbeWithOAuthAsync(accessToken!, ct, sw);
            }

            return Fail("no token in credential bundle", sw);
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

    private async Task<AgentSmokeResult> ProbeWithApiKeyAsync(
        string apiKey, CancellationToken ct, Stopwatch sw)
    {
        var client = _httpClientFactory.CreateClient("agent-smoke");
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiKeyGenerateContentEndpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = CreateApiKeySmokeRequestBody();
        return await SendAndInterpretAsync(client, request, ct, sw);
    }

    private async Task<AgentSmokeResult> ProbeWithOAuthAsync(
        string accessToken, CancellationToken ct, Stopwatch sw)
    {
        var client = _httpClientFactory.CreateClient("agent-smoke");
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthGenerateContentEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = CreateOAuthSmokeRequestBody();
        return await SendAndInterpretAsync(client, request, ct, sw);
    }

    private static async Task<AgentSmokeResult> SendAndInterpretAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken ct, Stopwatch sw)
    {
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

    internal static string? ExtractAccessToken(string oauthCredsJson, ILogger? log = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(oauthCredsJson);
            if (doc.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }
        }
        catch (JsonException ex)
        {
            log?.LogDebug(ex, "Gemini OAuth creds JSON is malformed; treating as no token");
        }
        return null;
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed);
    }

    // Public v1beta endpoint shape: GenerateContentRequest at the top level.
    private const string SmokeRequestBodyApiKeyJson =
        """{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"maxOutputTokens":1}}""";

    // Code Assist v1internal shape: {model, request} envelope around the
    // GenerateContentRequest. Mirrors GeminiQuotaProbe.ProbeOneAsync /
    // GeminiAgentRunner.SendOAuthAsync. The model id matches the API-key probe
    // (gemini-2.0-flash) so OAuth and API-key gates exercise the same cheapest
    // model bucket.
    private const string SmokeRequestBodyOAuthJson =
        """{"model":"models/gemini-2.0-flash","request":{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"maxOutputTokens":1}}}""";

    private static StringContent CreateApiKeySmokeRequestBody() =>
        new(SmokeRequestBodyApiKeyJson, Encoding.UTF8, "application/json");

    private static StringContent CreateOAuthSmokeRequestBody() =>
        new(SmokeRequestBodyOAuthJson, Encoding.UTF8, "application/json");
}
