using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Optional fresh-access-token source for <see cref="GeminiSmokeProbe"/>.
/// The Gemini CLI rotates <c>~/.gemini/oauth_creds.json</c>'s
/// <c>access_token</c> roughly every hour; without a refresh hook the smoke
/// probe parses the on-disk token and inevitably sends an expired bearer when
/// the file's expiry has passed, producing a persistent-looking auth failure
/// that benches the agent until the operator re-runs <c>gemini</c>.
///
/// <para>Implementations sit on top of the same refresh path the quota probe
/// uses (HTTP refresh when <c>client_id/client_secret</c> are configured or
/// embedded in the creds file, CLI-driven refresh otherwise). Returns null
/// when no token can be obtained — the probe then falls back to the raw
/// credential bundle and finally to a persistent "no token" failure that
/// surfaces as an operator-actionable alert.</para>
/// </summary>
public interface IGeminiOAuthTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

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
    private readonly IGeminiOAuthTokenSource? _oauthTokenSource;

    public AgentKind Kind => AgentKind.Gemini;

    public GeminiSmokeProbe(IHttpClientFactory httpClientFactory, ILogger<GeminiSmokeProbe> log)
        : this(httpClientFactory, log, oauthTokenSource: null)
    {
    }

    public GeminiSmokeProbe(
        IHttpClientFactory httpClientFactory,
        ILogger<GeminiSmokeProbe> log,
        IGeminiOAuthTokenSource? oauthTokenSource)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
        _oauthTokenSource = oauthTokenSource;
    }

    public async Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            credential.EnvironmentVariables.TryGetValue("GEMINI_API_KEY", out var apiKey);
            if (!string.IsNullOrEmpty(apiKey))
                return await ProbeWithApiKeyAsync(apiKey!, ct, sw);

            // OAuth path: prefer a freshly-refreshed access token from the
            // injected source (which talks to Google's OAuth refresh endpoint /
            // the gemini CLI when needed) over the raw on-disk JSON. The
            // on-disk file's access_token rotates every hour; without this
            // refresh the smoke probe sends an expired token, gets back a
            // category-Persistent auth failure, and the agent stays benched
            // even though gemini is fully usable. Falls back to the raw JSON
            // when no refresher is wired (tests, legacy configs).
            string? oauthAccessToken = null;
            if (_oauthTokenSource is not null)
            {
                try
                {
                    oauthAccessToken = await _oauthTokenSource.GetAccessTokenAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Gemini OAuth token source threw; falling back to credential bundle");
                }
            }
            if (string.IsNullOrEmpty(oauthAccessToken)
                && credential.EnvironmentVariables.TryGetValue(CodeyBox.Core.GeminiConstants.OAuthCredsEnvVar, out var oauthJson)
                && !string.IsNullOrEmpty(oauthJson))
            {
                oauthAccessToken = ExtractAccessToken(oauthJson!, _log);
            }
            if (!string.IsNullOrEmpty(oauthAccessToken))
                return await ProbeWithOAuthAsync(oauthAccessToken!, ct, sw);

            // No usable token from any source. This is operator-actionable:
            // the gemini CLI hard-reads ~/.gemini/oauth_creds.json and the
            // operator must run `gemini` to mint one, or set GEMINI_API_KEY.
            return Fail("no token in credential bundle", sw, SmokeFailureCategory.Persistent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("timeout", sw, SmokeFailureCategory.Transient);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Gemini smoke probe threw; treating as transient");
            return Fail("transient: try later", sw, SmokeFailureCategory.Transient);
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
            return new AgentSmokeResult(true, null, sw.Elapsed, SmokeFailureCategory.None);

        // 401/403 = credential rejection. Persistent: a retry sends the same
        // dead bearer/key and gets the same answer. Operator must re-auth.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new AgentSmokeResult(false, "auth", sw.Elapsed, SmokeFailureCategory.Persistent);

        if ((int)response.StatusCode >= 500)
            return new AgentSmokeResult(false, "transient: try later", sw.Elapsed, SmokeFailureCategory.Transient);

        // Other 4xx (bad request, payment required, etc.) — we can't be sure
        // whether retrying helps, but they don't fix themselves either. Bucket
        // as Unknown so the periodic sweep keeps trying without firing the
        // persistent-failure alert on a one-off API hiccup.
        return new AgentSmokeResult(
            false, $"HTTP {(int)response.StatusCode}", sw.Elapsed, SmokeFailureCategory.Unknown);
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

    private static AgentSmokeResult Fail(string reason, Stopwatch sw, SmokeFailureCategory category)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed, category);
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
