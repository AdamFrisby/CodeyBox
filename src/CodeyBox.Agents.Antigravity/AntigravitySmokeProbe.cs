using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Smoke-tests Antigravity credentials. Refresh-token-bearing OAuth bundles
/// pass by presence check because the in-VM agy CLI owns token refresh; access-
/// token-only bundles are validated with a loadCodeAssist request.
/// </summary>
public sealed class AntigravitySmokeProbe : IAgentSmokeProbe
{
    internal const string LoadCodeAssistEndpoint = "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string LoadCodeAssistBody = "{\"metadata\":{\"pluginType\":\"GEMINI\"}}";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AntigravitySmokeProbe> _log;

    public AgentKind Kind => AgentKind.Antigravity;

    public AntigravitySmokeProbe(IHttpClientFactory httpClientFactory, ILogger<AntigravitySmokeProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!credential.EnvironmentVariables.TryGetValue(AntigravityConstants.OAuthCredsEnvVar, out var oauthJson)
                || string.IsNullOrEmpty(oauthJson))
            {
                return Fail("no token in credential bundle", sw, SmokeFailureCategory.Persistent);
            }

            var parsed = ExtractOAuthCredential(oauthJson, _log);
            if (parsed.HasRefreshToken)
            {
                sw.Stop();
                return new AgentSmokeResult(true, null, sw.Elapsed, SmokeFailureCategory.None);
            }

            if (string.IsNullOrEmpty(parsed.AccessToken))
            {
                return Fail("no token in credential bundle", sw, SmokeFailureCategory.Persistent);
            }

            var client = _httpClientFactory.CreateClient("agent-smoke");
            using var request = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", parsed.AccessToken);
            request.Content = new StringContent(LoadCodeAssistBody, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
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
            _log.LogDebug(ex, "Antigravity smoke probe threw; treating as transient");
            return Fail("transient: try later", sw, SmokeFailureCategory.Transient);
        }
    }

    internal static string? ExtractAccessToken(string oauthCredsJson, ILogger? log = null) =>
        ExtractOAuthCredential(oauthCredsJson, log).AccessToken;

    internal static AntigravityOAuthCredential ExtractOAuthCredential(string oauthCredsJson, ILogger? log = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(oauthCredsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, false);

            string? accessToken = null;
            var hasRefreshToken = false;
            if (root.TryGetProperty("token", out var tok)
                && tok.ValueKind == JsonValueKind.Object)
            {
                if (tok.TryGetProperty("access_token", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                {
                    accessToken = nested.GetString();
                }

                hasRefreshToken = tok.TryGetProperty("refresh_token", out var nestedRefresh)
                    && nestedRefresh.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nestedRefresh.GetString());
            }

            if (root.TryGetProperty("access_token", out var flat)
                && flat.ValueKind == JsonValueKind.String)
            {
                accessToken ??= flat.GetString();
            }

            hasRefreshToken = hasRefreshToken
                || (root.TryGetProperty("refresh_token", out var flatRefresh)
                    && flatRefresh.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(flatRefresh.GetString()));

            return new(accessToken, hasRefreshToken);
        }
        catch (JsonException ex)
        {
            log?.LogDebug(ex, "Antigravity OAuth creds JSON is malformed; treating as no token");
        }
        return new(null, false);
    }

    internal readonly record struct AntigravityOAuthCredential(string? AccessToken, bool HasRefreshToken);

    private static AgentSmokeResult Fail(string reason, Stopwatch sw, SmokeFailureCategory category)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed, category);
    }
}
