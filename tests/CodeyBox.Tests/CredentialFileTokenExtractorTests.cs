using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CredentialFileTokenExtractorTests
{
    [Fact]
    public void ExtractClaudeAccessToken_ReadsAccessTokenFromClaudeOauthShape()
    {
        const string raw =
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-token","refreshToken":"rt-ignored","expiresAt":123}}""";

        var token = CredentialFileTokenExtractor.ExtractClaudeAccessToken(raw);

        Assert.Equal("sk-ant-oat01-token", token);
    }

    [Fact]
    public void TryBuildClaudeSanitisedBundle_StripsRefreshToken()
    {
        const string raw =
            """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-token","refreshToken":"rt-secret","expiresAt":123}}""";

        var ok = CredentialFileTokenExtractor.TryBuildClaudeSanitisedBundle(
            raw,
            out var token,
            out var bundle);

        Assert.True(ok);
        Assert.Equal("sk-ant-oat01-token", token);
        Assert.Contains("\"accessToken\":\"sk-ant-oat01-token\"", bundle);
        Assert.Contains("\"expiresAt\":123", bundle);
        Assert.DoesNotContain("refreshToken", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-secret", bundle);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"claudeAiOauth":{"refreshToken":"rt-only"}}""")]
    [InlineData("""{"other":true}""")]
    public void ExtractClaudeAccessToken_ReturnsNullForMalformedOrMissingToken(string raw)
    {
        Assert.Null(CredentialFileTokenExtractor.ExtractClaudeAccessToken(raw));
    }

    [Fact]
    public void ExtractCodexAccessTokens_ReadsAccessTokenAndAccountId()
    {
        const string raw = """{"tokens":{"access_token":"codex-token","account_id":"acct-42"}}""";

        var (accessToken, accountId) = CredentialFileTokenExtractor.ExtractCodexAccessTokens(raw);

        Assert.Equal("codex-token", accessToken);
        Assert.Equal("acct-42", accountId);
    }

    [Fact]
    public void ExtractCodexAccessTokens_AllowsMissingAccountId()
    {
        const string raw = """{"tokens":{"access_token":"codex-token"}}""";

        var (accessToken, accountId) = CredentialFileTokenExtractor.ExtractCodexAccessTokens(raw);

        Assert.Equal("codex-token", accessToken);
        Assert.Null(accountId);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"tokens":{"access_token":123}}""")]
    [InlineData("""{"other":true}""")]
    public void ExtractCodexAccessTokens_ReturnsNullsForMalformedOrMissingToken(string raw)
    {
        var (accessToken, accountId) = CredentialFileTokenExtractor.ExtractCodexAccessTokens(raw);

        Assert.Null(accessToken);
        Assert.Null(accountId);
    }

    [Fact]
    public void ExtractCursorAccessToken_ReadsAccessToken()
    {
        const string raw = """{"accessToken":"cursor-subscription-token","email":"redacted@example.com"}""";

        var token = CredentialFileTokenExtractor.ExtractCursorAccessToken(raw);

        Assert.Equal("cursor-subscription-token", token);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"accessToken":""}""")]
    [InlineData("""{"token":"legacy-shape"}""")]
    public void ExtractCursorAccessToken_ReturnsNullForMalformedOrMissingToken(string raw)
    {
        Assert.Null(CredentialFileTokenExtractor.ExtractCursorAccessToken(raw));
    }

    [Fact]
    public void ExtractGeminiAccessToken_ReadsAccessToken()
    {
        const string raw = """{"access_token":"gemini-token","refresh_token":"ignored"}""";

        var token = CredentialFileTokenExtractor.ExtractGeminiAccessToken(raw);

        Assert.Equal("gemini-token", token);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"refresh_token":"rt-only"}""")]
    [InlineData("""{"access_token":123}""")]
    public void ExtractGeminiAccessToken_ReturnsNullForMalformedOrMissingToken(string raw)
    {
        Assert.Null(CredentialFileTokenExtractor.ExtractGeminiAccessToken(raw));
    }

    [Fact]
    public void TryBuildAntigravitySanitisedBundle_StripsRefreshTokenAndKeepsRest()
    {
        // The host CLI is the sole party that holds the Google OAuth
        // refresh_token; the in-VM agy binary must not see it (otherwise an
        // in-VM refresh would rotate the host's refresh_token out from under
        // the host CLI / quota probes). Same isolation invariant Claude
        // enforces via TryBuildClaudeSanitisedBundle.
        const string raw =
            """{"access_token":"ya29.live","refresh_token":"rt-secret","client_id":"cid","client_secret":"csec","expiry_date":1900000000000,"token_type":"Bearer","scope":"https://www.googleapis.com/auth/cloud-platform"}""";

        var ok = CredentialFileTokenExtractor.TryBuildAntigravitySanitisedBundle(raw, out var bundle);

        Assert.True(ok);
        Assert.Contains("\"access_token\":\"ya29.live\"", bundle);
        Assert.Contains("\"expiry_date\":1900000000000", bundle);
        // refresh_token must be gone in both the key and the secret value.
        Assert.DoesNotContain("refresh_token", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rt-secret", bundle);
        // Non-secret companions (client_id, expiry, scope) survive — they are
        // not the single-use refresh credential and the in-VM CLI may surface
        // them for diagnostics.
        Assert.Contains("\"token_type\":\"Bearer\"", bundle);
        Assert.Contains("\"scope\":", bundle);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData("""{"refresh_token":"rt-only"}""")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":123}""")]
    [InlineData("[]")]
    public void TryBuildAntigravitySanitisedBundle_ReturnsFalseForMalformedOrMissingAccessToken(string raw)
    {
        var ok = CredentialFileTokenExtractor.TryBuildAntigravitySanitisedBundle(raw, out var bundle);

        Assert.False(ok);
        Assert.Equal(string.Empty, bundle);
    }
}
