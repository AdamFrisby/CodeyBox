using System.Text;
using System.Text.Json;
using CodeyBox.Agents;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Antigravity;

/// <summary>Antigravity (Google Antigravity agy OAuth) quota-probe token source.</summary>
public interface IAntigravityQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Refreshes Google Antigravity (<c>agy</c>) OAuth tokens.
/// The host CLI (<c>agy</c>) refreshes its OAuth token using its embedded OAuth client
/// and writes the resulting bundle to the system keyring (freedesktop Secret Service,
/// item <c>service=gemini, username=antigravity</c>), but does NOT write back to disk.
///
/// <para>Refresh strategy:</para>
/// <list type="number">
///   <item>Invoke the host <c>agy</c> CLI via <c>agy --print '/usage'</c> so it self-refreshes.</item>
///   <item>Read the refreshed token bundle from the Secret Service item <c>service=gemini, username=antigravity</c>.</item>
///   <item>Persist it to disk at <c>Source.FilePath</c> (from <c>CodeyBox:Antigravity:OAuthTokenFile</c>) so the quota probe and in-VM dispatch pick it up.</item>
/// </list>
///
/// <para>Degrades gracefully to returning the existing (stale) token rather than throwing if refresh fails.</para>
/// </summary>
public sealed class AntigravityOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, IAntigravityQuotaTokenSource
{
    /// <summary>Timeout for the agy CLI refresh sub-process.</summary>
    internal static readonly TimeSpan AntigravityCliRefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Fallback token lifetime used when the keyring-refreshed bundle carries no expiry.</summary>
    internal static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromHours(1);

    private readonly Func<CancellationToken, Task<bool>>? _cliRunner;
    private readonly Func<CancellationToken, Task<string?>> _keyringReader;

    protected override bool RequiresRefreshToken => false;
    protected override bool CanRefreshWithoutFile => true;

    public AntigravityOauthCredentialFileRefresher(
        ICredentialFileReader source,
        IHttpClientFactory httpClientFactory,
        ILogger<AntigravityOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<bool>>? cliRunner = null,
        Func<CancellationToken, Task<string?>>? keyringReader = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _cliRunner = cliRunner;
        _keyringReader = keyringReader ?? CreateDefaultKeyringReader(isPlatformSupported: null, log);
    }

    /// <summary>
    /// Builds the default keyring delegate used when the caller does not inject
    /// one: the freedesktop Secret Service reader on supported platforms
    /// (Linux with a session bus), otherwise a null-returning delegate so the
    /// refresher degrades to the on-disk (possibly stale) token. The platform
    /// check is injectable so tests can exercise both paths without branching
    /// on the real OS.
    /// </summary>
    internal static Func<CancellationToken, Task<string?>> CreateDefaultKeyringReader(
        Func<bool>? isPlatformSupported,
        ILogger? log)
    {
        var reader = SecretServiceKeyringReader.TryCreate(isPlatformSupported, log);
        if (reader is null)
            return _ => Task.FromResult<string?>(null);
        return AntigravityKeyring.ToRefreshDelegate(reader);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var token = await GetOrRefreshAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            return token;

        // Failure path: degrade to returning the existing (possibly expired) token rather than throwing.
        var raw = Source.GetRaw();
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var parsed = ParseCreds(raw);
                if (!string.IsNullOrEmpty(parsed.AccessToken))
                    return parsed.AccessToken;
            }
            catch (JsonException) { }
        }

        return null;
    }

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new ParsedCreds(null, null, null, null, null, null);

        // Native agy shape: {"auth_method":"consumer","token":{"access_token":...,"refresh_token":...,"token_type":"Bearer","expiry":"..."}}
        if (root.TryGetProperty("token", out var tokenObj) && tokenObj.ValueKind == JsonValueKind.Object)
        {
            var access = TryString(tokenObj, "access_token");
            var refresh = TryString(tokenObj, "refresh_token");
            var expires = ExtractExpiry(tokenObj);
            if (!string.IsNullOrEmpty(access))
                return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: null);
        }

        // Legacy flat shape: {"access_token":...,"refresh_token":...,"expiry_date":...}
        {
            var access = TryString(root, "access_token");
            var refresh = TryString(root, "refresh_token");
            var expires = ExtractExpiry(root);
            return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: null);
        }
    }

    /// <summary>
    /// Threshold to distinguish between Unix timestamps in seconds vs milliseconds.
    /// Timestamps below 100 billion correspond to dates before Nov 5138 in seconds,
    /// whereas in milliseconds they correspond to dates before Mar 1973.
    /// </summary>
    private const long UnixSecondsThreshold = 100_000_000_000L;

    private static DateTimeOffset? ExtractExpiry(JsonElement element)
    {
        if (element.TryGetProperty("expiry", out var exp))
        {
            if (exp.ValueKind == JsonValueKind.String)
            {
                var str = exp.GetString();
                if (!string.IsNullOrEmpty(str) && DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
                    return dto;
            }
            else if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var n))
            {
                return n < UnixSecondsThreshold
                    ? DateTimeOffset.FromUnixTimeSeconds(n)
                    : DateTimeOffset.FromUnixTimeMilliseconds(n);
            }
        }

        if (element.TryGetProperty("expiry_date", out var expDate) && expDate.ValueKind == JsonValueKind.Number && expDate.TryGetInt64(out var ms))
        {
            return ms < UnixSecondsThreshold
                ? DateTimeOffset.FromUnixTimeSeconds(ms)
                : DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return null;
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        if (_cliRunner is not null)
        {
            var cliOk = await _cliRunner(ct).ConfigureAwait(false);
            if (!cliOk)
                return new RefreshResult(null, null, TimeSpan.Zero);
        }

        string? secretJson;
        try
        {
            secretJson = await _keyringReader(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RefreshResult(null, null, TimeSpan.Zero);
        }

        if (string.IsNullOrEmpty(secretJson))
            return new RefreshResult(null, null, TimeSpan.Zero);

        ParsedCreds refreshed;
        try
        {
            refreshed = ParseCreds(secretJson);
        }
        catch (JsonException)
        {
            return new RefreshResult(null, null, TimeSpan.Zero);
        }

        if (string.IsNullOrEmpty(refreshed.AccessToken))
            return new RefreshResult(null, null, TimeSpan.Zero);

        var expiresAt = refreshed.ExpiresAt ?? (TimeProvider.GetUtcNow() + FallbackTokenLifetime);
        var expiresIn = expiresAt - TimeProvider.GetUtcNow();
        if (expiresIn <= TimeSpan.Zero)
            return new RefreshResult(null, null, TimeSpan.Zero);

        return new RefreshResult(refreshed.AccessToken, refreshed.RefreshToken, expiresIn);
    }

    protected override string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(existingRaw) ? "{}" : existingRaw);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            var sawToken = false;
            var sawAuthMethod = false;

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("token") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        sawToken = true;
                        writer.WritePropertyName("token");
                        writer.WriteStartObject();
                        var sawAccess = false;
                        var sawExpiry = false;
                        var sawRefresh = false;

                        foreach (var t in prop.Value.EnumerateObject())
                        {
                            if (t.NameEquals("access_token"))
                            {
                                writer.WriteString("access_token", result.AccessToken);
                                sawAccess = true;
                            }
                            else if (t.NameEquals("refresh_token"))
                            {
                                sawRefresh = true;
                                if (!string.IsNullOrEmpty(result.RefreshToken))
                                    writer.WriteString("refresh_token", result.RefreshToken);
                                else
                                    t.WriteTo(writer);
                            }
                            else if (t.NameEquals("expiry"))
                            {
                                writer.WriteString("expiry", newExpiresAt.ToString("o"));
                                sawExpiry = true;
                            }
                            else
                            {
                                t.WriteTo(writer);
                            }
                        }

                        if (!sawAccess && result.AccessToken is not null)
                            writer.WriteString("access_token", result.AccessToken);
                        if (!sawRefresh && !string.IsNullOrEmpty(result.RefreshToken))
                            writer.WriteString("refresh_token", result.RefreshToken);
                        if (!sawExpiry)
                            writer.WriteString("expiry", newExpiresAt.ToString("o"));

                        writer.WriteEndObject();
                    }
                    else
                    {
                        if (prop.NameEquals("auth_method"))
                            sawAuthMethod = true;
                        prop.WriteTo(writer);
                    }
                }
            }

            if (!sawToken)
            {
                if (!sawAuthMethod)
                    writer.WriteString("auth_method", "consumer");

                writer.WritePropertyName("token");
                writer.WriteStartObject();
                writer.WriteString("access_token", result.AccessToken);
                if (!string.IsNullOrEmpty(result.RefreshToken))
                    writer.WriteString("refresh_token", result.RefreshToken);
                writer.WriteString("token_type", "Bearer");
                writer.WriteString("expiry", newExpiresAt.ToString("o"));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static Func<CancellationToken, Task<bool>>? TryCreateCliRefreshHandler(
        Func<string?>? resolvePath = null)
    {
        var cliPath = (resolvePath ?? ResolveAntigravityCliPath)();
        if (cliPath is null) return null;
        return BuildCliRefreshDelegate(cliPath);
    }

    internal static string? ResolveAntigravityCliPath()
    {
        return ResolveExecutablePath(OperatingSystem.IsWindows() ? "where" : "which", "agy");
    }

    private static readonly string[] AntigravityCliRefreshArgs = ["--print", "/usage"];

    internal static Func<CancellationToken, Task<bool>> BuildCliRefreshDelegate(string cliPath)
    {
        return ct => ExecuteCliProcessAsync(cliPath, AntigravityCliRefreshArgs, AntigravityCliRefreshTimeout, ct);
    }

    private static string? TryString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
