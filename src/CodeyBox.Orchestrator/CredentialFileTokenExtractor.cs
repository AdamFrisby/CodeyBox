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
