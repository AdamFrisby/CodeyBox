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
    public void TryBuildAntigravityTokenBundle_NativeShape_KeepsRefreshTokenVerbatim()
    {
        // agy's native keyring/file shape: {auth_method, token:{...}}. The
        // refresh_token MUST be kept — the in-VM agy has no other way to refresh
        // the short-lived access_token, and the host authenticates from the
        // keyring (a separate store), so shipping it is operationally safe.
        const string raw =
            """{"auth_method":"consumer","token":{"access_token":"ya29.live","token_type":"Bearer","refresh_token":"rt-secret","expiry":"2026-06-10T19:57:49+10:00"}}""";

        var ok = CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle(raw, out var bundle);

        Assert.True(ok);
        // Echoed byte-for-byte — agy's fileTokenStorage expects this exact envelope.
        Assert.Equal(raw, bundle);
        Assert.Contains("refresh_token", bundle);
        Assert.Contains("rt-secret", bundle);
    }

    [Fact]
    public void TryBuildAntigravityTokenBundle_LegacyFlatShape_Accepted()
    {
        const string raw = """{"access_token":"ya29.flat","refresh_token":"rt","token_type":"Bearer"}""";

        var ok = CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle(raw, out var bundle);

        Assert.True(ok);
        Assert.Equal(raw, bundle);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData("""{"refresh_token":"rt-only"}""")]
    [InlineData("""{"token":{"refresh_token":"rt-only"}}""")]
    [InlineData("""{"token":{"access_token":""}}""")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":123}""")]
    [InlineData("[]")]
    public void TryBuildAntigravityTokenBundle_ReturnsFalseForMalformedOrMissingAccessToken(string raw)
    {
        var ok = CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle(raw, out var bundle);

        Assert.False(ok);
        Assert.Equal(string.Empty, bundle);
    }
}
