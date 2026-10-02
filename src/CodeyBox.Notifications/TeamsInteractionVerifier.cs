using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Notifications;

/// <summary>
/// One Bot Framework signing key: the key id from the JWKS plus the RSA
/// parameters needed to verify a token signature.
/// </summary>
public sealed record BotFrameworkSigningKey(string Kid, RSAParameters Parameters);

/// <summary>
/// Supplies the current Bot Framework signing keys (Microsoft's JWKS).
/// Implementations cache and refresh; the verifier asks for a forced
/// refresh once when it sees an unknown key id.
/// </summary>
public interface IBotFrameworkSigningKeyProvider
{
    Task<IReadOnlyList<BotFrameworkSigningKey>> GetKeysAsync(CancellationToken ct, bool forceRefresh = false);
}

/// <summary>
/// Fetches the Bot Framework signing keys over HTTPS: the OpenID
/// configuration points at the JWKS, whose RSA entries become verification
/// keys. Keys cache for 24 hours; a failed refresh keeps serving the stale
/// set rather than failing every delivery, and reports empty when nothing
/// was ever fetched. Never throws for transport problems — the verifier
/// turns an empty set into a fixed-vocabulary failure.
/// </summary>
public sealed class HttpBotFrameworkSigningKeyProvider : IBotFrameworkSigningKeyProvider
{
    /// <summary>Bot Framework OpenID configuration URL.</summary>
    public const string OpenIdConfigurationUrl = "https://login.botframework.com/v1/.well-known/openidconfiguration";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private IReadOnlyList<BotFrameworkSigningKey> _cached = Array.Empty<BotFrameworkSigningKey>();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public HttpBotFrameworkSigningKeyProvider(IHttpClientFactory httpClients, TimeProvider? clock = null)
    {
        _httpClients = httpClients;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<BotFrameworkSigningKey>> GetKeysAsync(CancellationToken ct, bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (!forceRefresh && _cached.Count > 0 && _clock.GetUtcNow() < _fetchedAt + CacheLifetime)
                return _cached;
        }
        var fresh = await FetchAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (fresh.Count > 0)
            {
                _cached = fresh;
                _fetchedAt = _clock.GetUtcNow();
                return _cached;
            }
            return _cached;
        }
    }

    private async Task<IReadOnlyList<BotFrameworkSigningKey>> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FetchTimeout);
            var client = _httpClients.CreateClient();
            var configJson = await client.GetStringAsync(OpenIdConfigurationUrl, timeoutCts.Token).ConfigureAwait(false);
            string? jwksUri;
            try
            {
                using var config = JsonDocument.Parse(configJson);
                jwksUri = config.RootElement.TryGetProperty("jwks_uri", out var uriProp)
                    && uriProp.ValueKind == JsonValueKind.String
                    ? uriProp.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return Array.Empty<BotFrameworkSigningKey>();
            }
            if (string.IsNullOrWhiteSpace(jwksUri))
                return Array.Empty<BotFrameworkSigningKey>();
            var jwksJson = await client.GetStringAsync(jwksUri, timeoutCts.Token).ConfigureAwait(false);
            return ParseJwks(jwksJson);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<BotFrameworkSigningKey>();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return Array.Empty<BotFrameworkSigningKey>();
        }
    }

    internal static IReadOnlyList<BotFrameworkSigningKey> ParseJwks(string jwksJson)
    {
        var keys = new List<BotFrameworkSigningKey>();
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            if (!doc.RootElement.TryGetProperty("keys", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return keys;
            foreach (var key in arr.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object)
                    continue;
                if (!TryGetString(key, "kty", out var kty) || kty != "RSA")
                    continue;
                if (!TryGetString(key, "kid", out var kid) || string.IsNullOrEmpty(kid))
                    continue;
                if (!TryGetString(key, "n", out var n) || !TryGetString(key, "e", out var e))
                    continue;
                try
                {
                    keys.Add(new BotFrameworkSigningKey(kid, new RSAParameters
                    {
                        Modulus = Base64UrlDecode(n),
                        Exponent = Base64UrlDecode(e),
                    }));
                }
                catch (FormatException)
                {
                    continue;
                }
            }
        }
        catch (JsonException)
        {
        }
        return keys;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? string.Empty;
        return true;
    }

    internal static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Bot Framework token verification (<c>botframework-jwt</c> scheme). The
/// Bot Framework signs every activity with a JWT bearer token rather than
/// an HMAC: <c>Authorization: Bearer &lt;RS256 JWT&gt;</c> issued by
/// <c>https://api.botframework.com</c> for this bot's App ID. Verification
/// checks the RSA signature against Microsoft's JWKS, the issuer, the
/// audience, and the token lifetime — never an HMAC shape.
///
/// <para>The expected App ID (token audience) resolves from the env var
/// named by <see cref="InteractionProviderOptions.SigningSecretEnvVar"/>
/// — the credential chain, never a config value. Replay protection comes
/// from the token's own expiry plus the endpoint's interaction-id dedup and
/// question-state checks, so there is no sender-timestamp window.</para>
/// </summary>
public sealed class TeamsInteractionVerifier : InteractionVerifierBase
{
    /// <summary>Scheme name used in <see cref="InteractionProviderOptions.Scheme"/>.</summary>
    public const string Scheme = "botframework-jwt";

    /// <summary>Only issuer accepted for Bot Framework tokens.</summary>
    public const string ExpectedIssuer = "https://api.botframework.com";

    /// <summary>Clock skew tolerated on token lifetime checks.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly string _provider;
    private readonly IBotFrameworkSigningKeyProvider? _keys;

    public TeamsInteractionVerifier(
        string provider,
        Func<InteractionProviderOptions?> optsAccessor,
        Func<string, string?>? envReader = null,
        TimeProvider? clock = null,
        IBotFrameworkSigningKeyProvider? keyProvider = null)
        : base(optsAccessor, envReader, clock)
    {
        _provider = provider;
        _keys = keyProvider;
    }

    public override string Provider => _provider;

    public override async Task<InteractionVerificationResult> VerifyAsync(
        byte[] rawBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        if (!TryResolveSecret(out var expectedAppId, out var failure))
            return InteractionVerificationResult.Fail(failure);

        var headerName = Options?.SignatureHeader;
        if (string.IsNullOrWhiteSpace(headerName))
            headerName = "Authorization";
        var authorization = Header(headers, headerName);
        const string Prefix = "Bearer ";
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(Prefix, StringComparison.Ordinal))
            return InteractionVerificationResult.Fail("missing or malformed authorization");

        var token = authorization[Prefix.Length..].Trim();
        if (!TrySplitToken(token, out var signingInput, out var headerJson, out var payloadJson))
            return InteractionVerificationResult.Fail("malformed token");

        JsonDocument headerDoc;
        JsonDocument payloadDoc;
        try
        {
            headerDoc = JsonDocument.Parse(headerJson);
            payloadDoc = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return InteractionVerificationResult.Fail("malformed token");
        }

        using (headerDoc)
        using (payloadDoc)
        {
            var header = headerDoc.RootElement;
            var payload = payloadDoc.RootElement;
            if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object)
                return InteractionVerificationResult.Fail("malformed token");

            if (!header.TryGetProperty("alg", out var alg)
                || alg.ValueKind != JsonValueKind.String
                || !string.Equals(alg.GetString(), "RS256", StringComparison.Ordinal))
                return InteractionVerificationResult.Fail("unsupported token algorithm");

            if (!header.TryGetProperty("kid", out var kid)
                || kid.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(kid.GetString()))
                return InteractionVerificationResult.Fail("unknown signing key");

            if (!payload.TryGetProperty("iss", out var iss)
                || iss.ValueKind != JsonValueKind.String
                || !string.Equals(iss.GetString(), ExpectedIssuer, StringComparison.Ordinal))
                return InteractionVerificationResult.Fail("invalid issuer");

            if (!IsAudience(payload, expectedAppId))
                return InteractionVerificationResult.Fail("invalid audience");

            var now = Clock.GetUtcNow();
            if (!TryGetLifetimeSeconds(payload, "exp", out var exp)
                || now > DateTimeOffset.FromUnixTimeSeconds(exp).Add(ClockSkew))
                return InteractionVerificationResult.Fail("token expired");
            if (TryGetLifetimeSeconds(payload, "nbf", out var nbf)
                && now.Add(ClockSkew) < DateTimeOffset.FromUnixTimeSeconds(nbf))
                return InteractionVerificationResult.Fail("token not yet valid");

            if (_keys is null)
                return InteractionVerificationResult.Fail("signing keys unavailable");

            var keys = await _keys.GetKeysAsync(ct).ConfigureAwait(false);
            var match = FindKey(keys, kid.GetString()!);
            if (match is null)
            {
                keys = await _keys.GetKeysAsync(ct, forceRefresh: true).ConfigureAwait(false);
                match = FindKey(keys, kid.GetString()!);
            }
            if (match is null)
                return InteractionVerificationResult.Fail("unknown signing key");

            byte[] signature;
            try
            {
                signature = HttpBotFrameworkSigningKeyProvider.Base64UrlDecode(token[(token.LastIndexOf('.') + 1)..]);
            }
            catch (FormatException)
            {
                return InteractionVerificationResult.Fail("malformed token");
            }

            using var rsa = RSA.Create();
            try
            {
                rsa.ImportParameters(match.Parameters);
            }
            catch (CryptographicException)
            {
                return InteractionVerificationResult.Fail("unknown signing key");
            }
            var valid = rsa.VerifyData(
                Encoding.ASCII.GetBytes(signingInput),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return valid
                ? InteractionVerificationResult.Ok()
                : InteractionVerificationResult.Fail("signature mismatch");
        }
    }

    private static BotFrameworkSigningKey? FindKey(IReadOnlyList<BotFrameworkSigningKey> keys, string kid)
    {
        foreach (var key in keys)
        {
            if (string.Equals(key.Kid, kid, StringComparison.Ordinal))
                return key;
        }
        return null;
    }

    private static bool TrySplitToken(string token, out string signingInput, out string headerJson, out string payloadJson)
    {
        signingInput = headerJson = payloadJson = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;
        var first = token.IndexOf('.');
        if (first <= 0)
            return false;
        var second = token.IndexOf('.', first + 1);
        if (second <= first + 1 || second == token.Length - 1)
            return false;
        signingInput = token[..second];
        try
        {
            headerJson = Encoding.UTF8.GetString(HttpBotFrameworkSigningKeyProvider.Base64UrlDecode(token[..first]));
            payloadJson = Encoding.UTF8.GetString(HttpBotFrameworkSigningKeyProvider.Base64UrlDecode(token[(first + 1)..second]));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsAudience(JsonElement payload, string expectedAppId)
    {
        if (!payload.TryGetProperty("aud", out var aud))
            return false;
        if (aud.ValueKind == JsonValueKind.String)
            return string.Equals(aud.GetString(), expectedAppId, StringComparison.Ordinal);
        if (aud.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in aud.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && string.Equals(entry.GetString(), expectedAppId, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool TryGetLifetimeSeconds(JsonElement payload, string name, out long seconds)
    {
        seconds = 0;
        if (!payload.TryGetProperty(name, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out seconds))
            return true;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out seconds))
            return true;
        return false;
    }
}
