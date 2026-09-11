using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Claude;

/// <summary>Claude (Anthropic OAuth) quota-probe token source.</summary>
public interface IClaudeQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Refreshes Anthropic OAuth tokens (claude CLI subscription path).
/// Reads <c>~/.claude/.credentials.json</c> — schema:
/// <code>
/// { "claudeAiOauth": { "accessToken": "...", "refreshToken": "...", "expiresAt": &lt;epoch-ms&gt; } }
/// </code>
/// Refreshes via <c>POST https://console.anthropic.com/v1/oauth/token</c>.
/// </summary>
public sealed class ClaudeOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, IClaudeQuotaTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://console.anthropic.com/v1/oauth/token";
    internal const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    internal const string HttpClientName = "agent-quota";

    private readonly string _refreshEndpoint;
    private readonly string _clientId;

    public ClaudeOauthCredentialFileRefresher(
        ICredentialFileReader source,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? clientId = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _clientId = clientId ?? DefaultClientId;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => GetOrRefreshAsync(ct);

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("claudeAiOauth", out var oauth)
            || oauth.ValueKind != JsonValueKind.Object)
        {
            return new ParsedCreds(null, null, null, null, null, null);
        }
        var access = oauth.TryGetProperty("accessToken", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var refresh = oauth.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var n))
        {
            // Anthropic emits milliseconds-since-epoch; older snapshots used
            // seconds. Disambiguate by magnitude: < 1e11 → seconds (would put
            // anything realistic before year 5138), else milliseconds.
            expires = n < 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(n)
                : DateTimeOffset.FromUnixTimeMilliseconds(n);
        }
        return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: null);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken))
            return new RefreshResult(null, null, TimeSpan.Zero);

        var http = HttpClientFactory.CreateClient(HttpClientName);
        var body = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = creds.RefreshToken,
            client_id = _clientId,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var bounded = await SendBoundedRefreshAsync(http, req, ct).ConfigureAwait(false);
        if (bounded.StatusCode != HttpStatusCode.OK || bounded.BodyTooLarge || bounded.Body is null)
            return new RefreshResult(null, null, TimeSpan.Zero);

        using var doc = JsonDocument.Parse(bounded.Body);
        var newAccess = doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString() : null;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
            && ex.TryGetInt32(out var s) ? s : 3600;
        return new RefreshResult(newAccess, newRefresh, TimeSpan.FromSeconds(seconds));
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
                if (prop.NameEquals("claudeAiOauth") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("claudeAiOauth");
                    writer.WriteStartObject();
                    var sawAccess = false;
                    var sawExpires = false;
                    foreach (var t in prop.Value.EnumerateObject())
                    {
                        if (t.NameEquals("accessToken"))
                        {
                            writer.WriteString("accessToken", result.AccessToken);
                            sawAccess = true;
                        }
                        else if (t.NameEquals("refreshToken") && !string.IsNullOrEmpty(result.RefreshToken))
                        {
                            writer.WriteString("refreshToken", result.RefreshToken);
                        }
                        else if (t.NameEquals("expiresAt"))
                        {
                            writer.WriteNumber("expiresAt", newExpiresAt.ToUnixTimeMilliseconds());
                            sawExpires = true;
                        }
                        else
                        {
                            t.WriteTo(writer);
                        }
                    }
                    if (!sawAccess) writer.WriteString("accessToken", result.AccessToken);
                    if (!sawExpires) writer.WriteNumber("expiresAt", newExpiresAt.ToUnixTimeMilliseconds());
                    writer.WriteEndObject();
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
}

