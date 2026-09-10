using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Codex;

/// <summary>Codex (ChatGPT OAuth) quota-probe token source.</summary>
public interface ICodexQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<(string? AccessToken, string? AccountId)> GetTokensAsync(CancellationToken ct = default);
}

/// <summary>
/// Refreshes ChatGPT OAuth tokens (codex CLI subscription path).
/// Reads <c>~/.codex/auth.json</c> — schema:
/// <code>
/// { "tokens": { "id_token": "...", "access_token": "...", "refresh_token": "...", "account_id": "..." } }
/// </code>
/// The access_token is a JWT; expiry is read from the embedded <c>exp</c>
/// claim. Refreshes via <c>POST https://auth.openai.com/oauth/token</c> with
/// <c>grant_type=refresh_token</c>. The codex CLI's hardcoded client id is
/// configurable via the constructor for tests / future ChatGPT app changes.
/// </summary>
public sealed class CodexOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, ICodexQuotaTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://auth.openai.com/oauth/token";

    /// <summary>
    /// Codex CLI's public client id. Pulled into a constant so tests can
    /// override without environment manipulation; in production this matches
    /// the value the codex CLI bundles in its own source.
    /// </summary>
    internal const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    internal const string HttpClientName = "agent-quota";

    private readonly string _refreshEndpoint;
    private readonly string _clientId;
    private string? _cachedAccountId;

    public CodexOauthCredentialFileRefresher(
        ICredentialFileReader source,
        IHttpClientFactory httpClientFactory,
        ILogger<CodexOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? clientId = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _clientId = clientId ?? DefaultClientId;
    }

    public async Task<(string? AccessToken, string? AccountId)> GetTokensAsync(CancellationToken ct = default)
    {
        var token = await GetOrRefreshAsync(ct).ConfigureAwait(false);
        string? accountId;
        lock (CacheLock) accountId = _cachedAccountId;
        if (token is null && accountId is null)
        {
            // Fall back to whatever the file currently contains so callers still
            // see the account id when the file is parseable but refresh is
            // unneeded.
            var raw = Source.GetRaw();
            if (!string.IsNullOrEmpty(raw))
            {
                try { accountId = ParseCreds(raw).AccountId; }
                catch (JsonException) { /* swallow */ }
            }
        }
        return (token, accountId);
    }

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tokens", out var tokens)
            || tokens.ValueKind != JsonValueKind.Object)
        {
            return new ParsedCreds(null, null, null, null, null, null);
        }
        var access = tokens.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var refresh = tokens.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var account = tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String
            ? acc.GetString() : null;
        lock (CacheLock) _cachedAccountId = account ?? _cachedAccountId;
        var expires = ExtractJwtExpiry(access);
        return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: account);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken))
            return new RefreshResult(null, null, TimeSpan.Zero);

        var http = HttpClientFactory.CreateClient(HttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", creds.RefreshToken!),
            new KeyValuePair<string, string>("client_id", _clientId),
            // Codex's scope set; harmless if the server ignores it on refresh.
            new KeyValuePair<string, string>("scope", "openid profile email"),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint) { Content = form };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return new RefreshResult(null, null, TimeSpan.Zero);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var newAccess = doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString() : null;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        var jwtExpiry = ExtractJwtExpiry(newAccess);
        var ttl = jwtExpiry is { } e && e > TimeProvider.GetUtcNow()
            ? e - TimeProvider.GetUtcNow()
            : (doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
                && ex.TryGetInt32(out var s)
                    ? TimeSpan.FromSeconds(s)
                    : TimeSpan.FromHours(1));
        return new RefreshResult(newAccess, newRefresh, ttl);
    }

    protected override string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        using var doc = JsonDocument.Parse(existingRaw);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("tokens") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("tokens");
                    writer.WriteStartObject();
                    foreach (var t in prop.Value.EnumerateObject())
                    {
                        if (t.NameEquals("access_token"))
                            writer.WriteString("access_token", result.AccessToken);
                        else if (t.NameEquals("refresh_token") && !string.IsNullOrEmpty(result.RefreshToken))
                            writer.WriteString("refresh_token", result.RefreshToken);
                        else
                            t.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else if (prop.NameEquals("last_refresh"))
                {
                    writer.WriteString("last_refresh", TimeProvider.GetUtcNow().ToString("o"));
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Decode a JWT's <c>exp</c> claim without verifying the signature; the
    /// expiry tells us when to refresh and is unauthenticated by design — a
    /// malicious server-side rewrite can only cause us to refresh sooner, never
    /// later, so signature verification adds nothing here.
    /// </summary>
    internal static DateTimeOffset? ExtractJwtExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (FormatException) { }
        catch (JsonException) { }
        return null;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }
}

