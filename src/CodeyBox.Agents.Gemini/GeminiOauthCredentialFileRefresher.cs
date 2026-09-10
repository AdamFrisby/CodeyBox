using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Gemini;

/// <summary>Gemini (Google Code Assist OAuth) quota-probe token source.</summary>
public interface IGeminiQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Refreshes Google Code Assist OAuth tokens (gemini CLI subscription path).
/// Reads <c>~/.gemini/oauth_creds.json</c> — schema:
/// <code>
/// { "access_token": "...", "refresh_token": "...", "client_id": "...",
///   "client_secret": "...", "expiry_date": &lt;ms-since-epoch&gt; }
/// </code>
///
/// <para>Refresh strategy:</para>
/// <list type="number">
///   <item>HTTP refresh using client_id + client_secret pooled from whichever
///   source is available (file creds take precedence; config fallback from
///   env var or <c>codeybox-extra.json</c> fills in missing values). This is
///   a single attempt — if HTTP creds exist but the call fails, CLI is not
///   attempted.</item>
///   <item>CLI-based refresh (only when no client credentials are available
///   for HTTP): invoke the host <c>gemini</c> CLI, which self-refreshes
///   using its own embedded OAuth client, then re-read
///   <c>~/.gemini/oauth_creds.json</c> for the new access_token/expiry.</item>
/// </list>
/// </summary>
public sealed class GeminiOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, IGeminiQuotaTokenSource, IGeminiOAuthTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://oauth2.googleapis.com/token";
    internal const string HttpClientName = "agent-quota";

    /// <summary>Timeout for the gemini CLI refresh sub-process.</summary>
    internal static readonly TimeSpan GeminiCliRefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Fallback token lifetime used when the CLI-refreshed file carries no expiry.</summary>
    internal static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>Maximum response body size in bytes accepted from the OAuth refresh endpoint.</summary>
    internal const int MaxRefreshBodyBytes = 8192;

    private readonly string _refreshEndpoint;
    private readonly string? _fallbackClientId;
    private readonly string? _fallbackClientSecret;
    private readonly Func<CancellationToken, Task<bool>>? _cliTokenRefresher;

    public GeminiOauthCredentialFileRefresher(
        ICredentialFileReader source,
        IHttpClientFactory httpClientFactory,
        ILogger<GeminiOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? geminiOauthClientId = null,
        string? geminiOauthClientSecret = null,
        Func<CancellationToken, Task<bool>>? cliTokenRefresher = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _fallbackClientId = geminiOauthClientId;
        _fallbackClientSecret = geminiOauthClientSecret;
        _cliTokenRefresher = cliTokenRefresher;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => GetOrRefreshAsync(ct);

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        string? access = TryString(root, "access_token");
        string? refresh = TryString(root, "refresh_token");
        string? clientId = TryString(root, "client_id");
        string? clientSecret = TryString(root, "client_secret");
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("expiry_date", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var ms))
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        return new ParsedCreds(access, refresh, expires, clientId, clientSecret, AccountId: null);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        var clientId = creds.ClientId ?? _fallbackClientId;
        var clientSecret = creds.ClientSecret ?? _fallbackClientSecret;

        if (!string.IsNullOrEmpty(creds.RefreshToken)
            && !string.IsNullOrEmpty(clientId)
            && !string.IsNullOrEmpty(clientSecret))
        {
            return await HttpRefreshAsync(creds.RefreshToken!, clientId!, clientSecret!, ct)
                .ConfigureAwait(false);
        }

        if (_cliTokenRefresher is not null && await _cliTokenRefresher(ct).ConfigureAwait(false))
        {
            var raw = Source.GetRaw();
            if (!string.IsNullOrEmpty(raw))
            {
                var reparsed = ParseCreds(raw);
                if (!string.IsNullOrEmpty(reparsed.AccessToken))
                {
                    var expiresAt = reparsed.ExpiresAt ?? (TimeProvider.GetUtcNow() + FallbackTokenLifetime);
                    var expiresIn = expiresAt - TimeProvider.GetUtcNow();
                    if (expiresIn <= TimeSpan.Zero) expiresIn = FallbackTokenLifetime;
                    return new RefreshResult(reparsed.AccessToken, reparsed.RefreshToken, expiresIn);
                }
            }
        }

        return new RefreshResult(null, null, TimeSpan.Zero);
    }

    private async Task<RefreshResult> HttpRefreshAsync(
        string refreshToken, string clientId, string clientSecret, CancellationToken ct)
    {
        var http = HttpClientFactory.CreateClient(HttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint) { Content = form };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return new RefreshResult(null, null, TimeSpan.Zero);

        var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bodyBytes.Length > MaxRefreshBodyBytes)
            return new RefreshResult(null, null, TimeSpan.Zero);
        var body = System.Text.Encoding.UTF8.GetString(bodyBytes);
        using var doc = JsonDocument.Parse(body);
        var newAccess = TryString(doc.RootElement, "access_token");
        var newRefresh = TryString(doc.RootElement, "refresh_token");
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
            && ex.TryGetInt32(out var s) ? s : (int)FallbackTokenLifetime.TotalSeconds;
        return new RefreshResult(newAccess, newRefresh, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Returns a delegate that invokes the host <c>gemini</c> CLI to force an
    /// OAuth token refresh, or null if the CLI cannot be found.
    /// The delegate launches <c>gemini -p "."</c> with a 30 s timeout and
    /// returns <c>true</c> when the process exits successfully (exit code 0),
    /// which indicates the CLI refreshed and rewrote <c>~/.gemini/oauth_creds.json</c>.
    /// </summary>
    /// <param name="resolvePath">Optional test seam. When non-null, used instead
    /// of <see cref="ResolveGeminiCliPath"/> to resolve the gemini binary path.</param>
    public static Func<CancellationToken, Task<bool>>? TryCreateCliRefreshHandler(
        Func<string?>? resolvePath = null)
    {
        var cliPath = (resolvePath ?? ResolveGeminiCliPath)();
        if (cliPath is null) return null;
        return BuildCliRefreshDelegate(cliPath);
    }

    private static readonly string[] GeminiCliRefreshArgs = ["-p", "."];

    private static Func<CancellationToken, Task<bool>> BuildCliRefreshDelegate(string cliPath)
    {
        return ct => ExecuteCliProcessAsync(cliPath, GeminiCliRefreshArgs, GeminiCliRefreshTimeout, ct);
    }

    /// <summary>
    /// Resolves the absolute path to the host <c>gemini</c> binary using
    /// <c>which</c> (POSIX) or <c>where</c> (Windows). Returns <c>null</c>
    /// when the binary is not found or any OS error occurs.
    /// </summary>
    internal static string? ResolveGeminiCliPath()
    {
        return ResolveExecutablePath(OperatingSystem.IsWindows() ? "where" : "which", "gemini");
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
                if (prop.NameEquals("access_token"))
                {
                    writer.WriteString("access_token", result.AccessToken);
                }
                else if (prop.NameEquals("refresh_token") && !string.IsNullOrEmpty(result.RefreshToken))
                {
                    writer.WriteString("refresh_token", result.RefreshToken);
                }
                else if (prop.NameEquals("expiry_date"))
                {
                    writer.WriteNumber("expiry_date", newExpiresAt.ToUnixTimeMilliseconds());
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            if (!doc.RootElement.TryGetProperty("access_token", out _))
                writer.WriteString("access_token", result.AccessToken);
            if (!doc.RootElement.TryGetProperty("expiry_date", out _))
                writer.WriteNumber("expiry_date", newExpiresAt.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? TryString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

