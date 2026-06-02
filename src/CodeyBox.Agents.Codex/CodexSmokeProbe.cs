using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Smoke-tests Codex credentials by issuing a single minimal validation
/// request — either against the OpenAI API (API-key auth) or against the
/// ChatGPT backend usage endpoint (OAuth subscription auth).
///
/// <para>Authentication priority and endpoint:</para>
/// <list type="number">
///   <item><c>OPENAI_API_KEY</c> (env var or <c>OPENAI_API_KEY</c> field inside
///   <c>CODEX_AUTH_JSON</c>) — sent as <c>Authorization: Bearer</c> against the
///   public chat completions endpoint (<see cref="CompletionsEndpoint"/>).</item>
///   <item><c>CODEX_AUTH_JSON</c> with <c>tokens.access_token</c> (the codex
///   CLI subscription OAuth bundle materialised by
///   <c>CodexOAuthFileCredentialProvider</c>) — sent as
///   <c>Authorization: Bearer &lt;access_token&gt;</c> plus a
///   <c>ChatGPT-Account-Id</c> header (when <c>tokens.account_id</c> is
///   present) against the ChatGPT backend WHAM usage endpoint
///   (<see cref="OAuthUsageEndpoint"/>). This is the same safe validation
///   endpoint <c>CodexQuotaProbe</c> already uses for the OAuth subscription
///   path; sending an OAuth bearer to <c>api.openai.com</c> always 401s, and
///   sending it to a raw inference endpoint risks account-safety flags, so
///   we mirror the quota probe's usage endpoint instead.</item>
/// </list>
///
/// <para>Uses the <c>agent-smoke</c> named HTTP client. Never logs the API
/// key, OAuth access token, account id, or credential values. The 0 ms
/// fast-fail path ("no token in credential bundle") is reached only when
/// neither auth method supplies a usable credential — this lets the OAuth
/// credential bundle pass smoke without requiring a separate
/// <c>OPENAI_API_KEY</c>.</para>
/// </summary>
public sealed class CodexSmokeProbe : IAgentSmokeProbe
{
    // API-key path: OpenAI public chat completions. Does NOT authenticate
    // ChatGPT subscription OAuth bearers — keep this for OPENAI_API_KEY only.
    internal const string CompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    internal const string ProbeModel = "gpt-4o-mini";

    // OAuth subscription path: the ChatGPT backend WHAM usage endpoint, the
    // same surface CodexQuotaProbe already validates against successfully
    // (it reads codex 100% via OAuth). A safe usage/validation endpoint —
    // do NOT swap this for a raw inference endpoint (account-safety).
    internal const string OAuthUsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

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
            credential.EnvironmentVariables.TryGetValue("CODEX_AUTH_JSON", out var authJson);

            // CODEX_AUTH_JSON may carry either a top-level OPENAI_API_KEY or a
            // tokens.access_token (OAuth subscription) — see CodexAgentRunner's
            // resolver. Mirror that here so the smoke probe accepts the same
            // credential shapes the runner would actually use.
            string? oauthAccessToken = null;
            string? oauthAccountId = null;
            if (!string.IsNullOrEmpty(authJson))
            {
                var parsed = TryParseAuthJson(authJson!, _log);
                if (string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(parsed.ApiKey))
                    apiKey = parsed.ApiKey;
                oauthAccessToken = parsed.AccessToken;
                oauthAccountId = parsed.AccountId;
            }

            if (!string.IsNullOrEmpty(apiKey))
                return await ProbeWithApiKeyAsync(apiKey!, ct, sw);

            if (!string.IsNullOrEmpty(oauthAccessToken))
                return await ProbeWithOAuthAsync(oauthAccessToken!, oauthAccountId, ct, sw);

            return Fail("no token in credential bundle", sw, SmokeFailureCategory.Persistent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("timeout", sw, SmokeFailureCategory.Transient);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex smoke probe threw; treating as transient");
            return Fail("transient: try later", sw, SmokeFailureCategory.Transient);
        }
    }

    private async Task<AgentSmokeResult> ProbeWithApiKeyAsync(
        string apiKey, CancellationToken ct, Stopwatch sw)
    {
        var client = _httpClientFactory.CreateClient("agent-smoke");
        using var request = new HttpRequestMessage(HttpMethod.Post, CompletionsEndpoint);

        // Do NOT log the Authorization header — it contains the API key.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        request.Content = new StringContent(
            $$"""{"model":"{{ProbeModel}}","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");

        return await SendAndInterpretAsync(client, request, ct, sw);
    }

    private async Task<AgentSmokeResult> ProbeWithOAuthAsync(
        string accessToken, string? accountId, CancellationToken ct, Stopwatch sw)
    {
        var client = _httpClientFactory.CreateClient("agent-smoke");
        using var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsageEndpoint);

        // Do NOT log the Authorization header — it contains the ChatGPT
        // subscription access token.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(accountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

        return await SendAndInterpretAsync(client, request, ct, sw);
    }

    private static async Task<AgentSmokeResult> SendAndInterpretAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken ct, Stopwatch sw)
    {
        using var response = await client.SendAsync(request, ct);
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

    internal static (string? ApiKey, string? AccessToken, string? AccountId) TryParseAuthJson(
        string raw, ILogger? log = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            string? apiKey = null;
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyEl)
                && apiKeyEl.ValueKind == JsonValueKind.String)
            {
                var value = apiKeyEl.GetString();
                if (!string.IsNullOrEmpty(value))
                    apiKey = value;
            }

            string? accessToken = null;
            string? accountId = null;
            if (root.TryGetProperty("tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Object)
            {
                if (tokens.TryGetProperty("access_token", out var at)
                    && at.ValueKind == JsonValueKind.String)
                {
                    var value = at.GetString();
                    if (!string.IsNullOrEmpty(value))
                        accessToken = value;
                }
                if (tokens.TryGetProperty("account_id", out var acc)
                    && acc.ValueKind == JsonValueKind.String)
                {
                    var value = acc.GetString();
                    if (!string.IsNullOrEmpty(value))
                        accountId = value;
                }
            }

            return (apiKey, accessToken, accountId);
        }
        catch (JsonException ex)
        {
            log?.LogDebug(ex, "Codex CODEX_AUTH_JSON is malformed; treating as no token");
            return (null, null, null);
        }
    }

    private static AgentSmokeResult Fail(string reason, Stopwatch sw, SmokeFailureCategory category)
    {
        sw.Stop();
        return new AgentSmokeResult(false, reason, sw.Elapsed, category);
    }
}
