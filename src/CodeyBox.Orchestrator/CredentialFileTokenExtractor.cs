using System.Text.Json;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Extracts host-side OAuth access tokens for startup probes without expanding
/// concrete credential-provider APIs beyond <c>ICredentialProvider</c>.
/// </summary>
public static class CredentialFileTokenExtractor
{
    public static string? ExtractClaudeAccessToken(string? rawContents)
        => TryBuildClaudeSanitisedBundle(rawContents, out var accessToken, out _)
            ? accessToken
            : null;

    internal static bool TryBuildClaudeSanitisedBundle(
        string? rawContents,
        out string accessToken,
        out string sanitisedBundle)
    {
        accessToken = "";
        sanitisedBundle = "";
        if (string.IsNullOrEmpty(rawContents))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var token = tokenEl.GetString() ?? "";
            if (token.Length == 0)
                return false;
            accessToken = token;
            sanitisedBundle = BuildClaudeSandboxBundle(oauth, token);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static (string? AccessToken, string? AccountId) ExtractCodexAccessTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ExtractCodexAccessTokens(doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    internal static (string? AccessToken, string? AccountId) ExtractCodexAccessTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var accessToken = tokens.TryGetProperty("access_token", out var token) &&
            token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        var accountId = tokens.TryGetProperty("account_id", out var account) &&
            account.ValueKind == JsonValueKind.String
                ? account.GetString()
                : null;
        return (accessToken, accountId);
    }

    public static string? ExtractCursorAccessToken(string? rawContents)
    {
        if (string.IsNullOrWhiteSpace(rawContents))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (doc.RootElement.TryGetProperty("accessToken", out var token) &&
                token.ValueKind == JsonValueKind.String)
            {
                var value = token.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Extracts an Anthropic API key from a CrockCode <c>config.json</c>
    /// payload of shape <c>{ "anthropic_api_key": "sk-…", "tunnel_provider": "…" }</c>.
    /// Tolerant of leading/trailing whitespace and the camelCase /
    /// SCREAMING_SNAKE_CASE variants operators sometimes hand-write. Returns
    /// null on any parse failure or when the key is missing/blank.
    /// </summary>
    public static string? ExtractCrockAnthropicApiKey(string? rawContents)
    {
        if (string.IsNullOrWhiteSpace(rawContents)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(prop.Name, "anthropic_api_key", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "anthropicApiKey", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "ANTHROPIC_API_KEY", StringComparison.Ordinal))
                {
                    var raw = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ExtractGeminiAccessToken(string? rawContents)
    {
        if (string.IsNullOrWhiteSpace(rawContents))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (doc.RootElement.TryGetProperty("access_token", out var token) &&
                token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Validates an Antigravity (<c>agy</c>) OAuth token bundle and returns it
    /// <b>verbatim</b> for materialisation into the sandbox at
    /// <c>~/.gemini/antigravity-cli/antigravity-oauth-token</c> — the path agy's
    /// <c>fileTokenStorage</c> reads when no system keyring (Secret Service) is
    /// present, i.e. inside every headless sandbox.
    ///
    /// <para><b>Why the refresh_token is KEPT (unlike the Claude path).</b> agy's
    /// access_token is short-lived (~1h) and the in-VM agy has no other refresh
    /// path, so it must self-refresh via its <c>persistingTokenSource</c>; a
    /// stripped bundle leaves it permanently "not logged into Antigravity". The
    /// host's own agy authenticates from the system <em>keyring</em> — a separate
    /// token store — so the file copy shipped to the sandbox is operationally
    /// independent of the host CLI / quota probe.</para>
    ///
    /// <para>Accepts agy's native shape
    /// <c>{"auth_method":"consumer","token":{"access_token":…,"refresh_token":…,"token_type":…,"expiry":…}}</c>
    /// (the exact bytes agy stores in the keyring) and a legacy top-level
    /// <c>{"access_token":…}</c> shape.</para>
    /// </summary>
    /// <returns>
    /// True with the input echoed verbatim in <paramref name="bundle"/> when it
    /// parses as a JSON object carrying a non-empty access token; false otherwise.
    /// </returns>
    public static bool TryBuildAntigravityTokenBundle(
        string? rawContents,
        out string bundle)
    {
        bundle = "";
        if (string.IsNullOrEmpty(rawContents))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!HasNonEmptyAntigravityAccessToken(root))
                return false;

            // Verbatim — agy needs the refresh_token to refresh the ~1h
            // access_token in-VM, and the native {auth_method, token} envelope
            // is exactly what agy's fileTokenStorage expects.
            bundle = rawContents;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNonEmptyAntigravityAccessToken(JsonElement root)
    {
        // Native agy shape: token.access_token.
        if (root.TryGetProperty("token", out var tok)
            && tok.ValueKind == JsonValueKind.Object
            && tok.TryGetProperty("access_token", out var nested)
            && nested.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(nested.GetString()))
        {
            return true;
        }

        // Legacy flat shape: top-level access_token.
        return root.TryGetProperty("access_token", out var flat)
            && flat.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(flat.GetString());
    }

    private static string BuildClaudeSandboxBundle(JsonElement oauth, string token)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("claudeAiOauth");
            writer.WriteStartObject();
            writer.WriteString("accessToken", token);
            // Forward expiresAt verbatim (number or string) when present so the
            // in-VM CLI can short-circuit a doomed reuse of a stale token.
            if (oauth.TryGetProperty("expiresAt", out var expiresAt))
            {
                writer.WritePropertyName("expiresAt");
                expiresAt.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
