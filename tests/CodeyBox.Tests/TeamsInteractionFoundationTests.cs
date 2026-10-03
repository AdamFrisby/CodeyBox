using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Notifications;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the Teams inbound foundation: the Bot Framework bearer
/// token verifier (signed by Microsoft, never an HMAC) and the native
/// activity parser. Verification runs over the token before the activity is
/// parsed for meaning; these tests pin that order by feeding the verifier
/// raw headers and the parser raw bytes independently.
/// </summary>
public sealed class TeamsInteractionFoundationTests
{
    private const string SecretEnvVar = "CODEYBOX_TEST_TEAMS_APP_ID";
    private const string AppId = "test-teams-app-id";

    private readonly RSA _signingRsa = RSA.Create(2048);
    private const string KeyId = "test-key-1";

    private InteractionProviderOptions ProviderOptions() => new()
    {
        Provider = "teams",
        Scheme = TeamsInteractionVerifier.Scheme,
        SigningSecretEnvVar = SecretEnvVar,
    };

    private sealed class StubKeyProvider : IBotFrameworkSigningKeyProvider
    {
        private readonly IReadOnlyList<BotFrameworkSigningKey> _keys;
        public int Calls;
        public int RefreshCalls;
        public StubKeyProvider(IReadOnlyList<BotFrameworkSigningKey> keys) => _keys = keys;
        public Task<IReadOnlyList<BotFrameworkSigningKey>> GetKeysAsync(CancellationToken ct, bool forceRefresh = false)
        {
            Calls++;
            if (forceRefresh) RefreshCalls++;
            return Task.FromResult(_keys);
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string MintToken(
        string? audience = AppId,
        string issuer = TeamsInteractionVerifier.ExpectedIssuer,
        string algorithm = "RS256",
        string? kid = KeyId,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? notBefore = null,
        RSA? signer = null)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object?>();
        if (algorithm is not null) header["alg"] = algorithm;
        if (kid is not null) header["kid"] = kid;
        header["typ"] = "JWT";
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = (expiresAt ?? now.AddHours(1)).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
        };
        if (notBefore.HasValue)
            payload["nbf"] = notBefore.Value.ToUnixTimeSeconds();
        var signingInput = $"{Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)))}.{Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))}";
        var rsa = signer ?? _signingRsa;
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private TeamsInteractionVerifier BuildVerifier(StubKeyProvider keys)
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, AppId);
        return new TeamsInteractionVerifier("teams", ProviderOptions, keyProvider: keys);
    }

    private static Dictionary<string, string> AuthHeaders(string token) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer " + token,
    };

    private BotFrameworkSigningKey PublicKey(string kid = KeyId)
    {
        var parameters = _signingRsa.ExportParameters(false);
        return new BotFrameworkSigningKey(kid, parameters);
    }

    [Fact]
    public async Task ValidBearerToken_Verifies()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            var result = await verifier.VerifyAsync(
                Encoding.UTF8.GetBytes("""{"type":"message"}"""),
                AuthHeaders(MintToken()),
                CancellationToken.None);

            Assert.True(result.Valid);
            Assert.Equal(string.Empty, result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task TamperedToken_SignatureMismatch()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            // The bearer token authenticates the sender, not the body
            // bytes: forging the token itself must fail closed. The flip
            // lands mid-signature so it always changes real signature bits
            // (never padding bits).
            var token = MintToken();
            var parts = token.Split('.');
            var mid = parts[2][10] == 'A' ? 'B' : 'A';
            var tampered = $"{parts[0]}.{parts[1]}.{parts[2][..10]}{mid}{parts[2][11..]}";

            var result = await verifier.VerifyAsync(
                Encoding.UTF8.GetBytes("""{"type":"message"}"""),
                AuthHeaders(tampered),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("signature mismatch", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task ExpiredToken_RejectedBeforeParsing()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            var result = await verifier.VerifyAsync(
                Encoding.UTF8.GetBytes("not even json"),
                AuthHeaders(MintToken(expiresAt: DateTimeOffset.UtcNow.AddHours(-2))),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("token expired", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task WrongAudience_Rejected()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            var result = await verifier.VerifyAsync(
                Array.Empty<byte>(),
                AuthHeaders(MintToken(audience: "some-other-app-id")),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("invalid audience", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task WrongIssuer_Rejected()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            var result = await verifier.VerifyAsync(
                Array.Empty<byte>(),
                AuthHeaders(MintToken(issuer: "https://evil.example.invalid")),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("invalid issuer", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task HmacShapedToken_RejectedAsUnsupportedAlgorithm()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            // The Teams scheme is a signed bearer token, never an HMAC: a
            // caller presenting the Slack shape must fail closed.
            var result = await verifier.VerifyAsync(
                Array.Empty<byte>(),
                AuthHeaders(MintToken(algorithm: "HS256")),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("unsupported token algorithm", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task MissingAuthorization_Rejected()
    {
        var verifier = BuildVerifier(new StubKeyProvider(new[] { PublicKey() }));
        try
        {
            var result = await verifier.VerifyAsync(
                Array.Empty<byte>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("missing or malformed authorization", result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task UnknownKey_RefreshesOnceThenRejects()
    {
        var keys = new StubKeyProvider(new[] { PublicKey("other-key") });
        var verifier = BuildVerifier(keys);
        try
        {
            var result = await verifier.VerifyAsync(
                Array.Empty<byte>(),
                AuthHeaders(MintToken()),
                CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal("unknown signing key", result.FailureReason);
            Assert.Equal(2, keys.Calls);
            Assert.Equal(1, keys.RefreshCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task MissingAppId_Rejected()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        var verifier = new TeamsInteractionVerifier("teams", ProviderOptions, keyProvider: new StubKeyProvider(new[] { PublicKey() }));

        var result = await verifier.VerifyAsync(
            Array.Empty<byte>(),
            AuthHeaders(MintToken()),
            CancellationToken.None);

        Assert.False(result.Valid);
    }

    [Fact]
    public void Parser_RecordedSubmitActivity_ParsesToCanonical()
    {
        // Recorded Bot Framework submit shape (see Fixtures/Teams/): field
        // values are synthetic stand-ins for a captured delivery.
        var workItemId = "work-" + Guid.NewGuid().ToString("N");
        var body = File.ReadAllText(Path.Combine("Fixtures", "Teams", "submit-activity.json"))
            .Replace("WORK_ITEM_ID", workItemId, StringComparison.Ordinal)
            .Replace("ACTIVITY_ID", "activity-recorded-1", StringComparison.Ordinal);

        var ok = TeamsInteractionParser.TryParse(Encoding.UTF8.GetBytes(body), out var interaction, out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(interaction);
        Assert.Equal("teams:activity-recorded-1", interaction!.InteractionId);
        Assert.Equal(workItemId, interaction.WorkItemId);
        Assert.Equal("q-001", interaction.QuestionId);
        Assert.Equal("Use rollbacks", interaction.Answer);
        Assert.Equal("29:alice-user-id", interaction.UserId);
        Assert.Equal("Alice", interaction.Login);
        Assert.Equal("19:channel-id@thread.tacv2", interaction.ChannelId);
        Assert.Equal($"{workItemId}:q-001", interaction.CorrelationToken);
    }

    [Fact]
    public void Parser_CustomAnswerFollowUp_ResolvesTypedAnswer()
    {
        var body = """
            {"type":"message","id":"activity-custom-1",
             "from":{"id":"29:bob","name":"Bob"},
             "conversation":{"id":"19:conv@thread.tacv2"},
             "value":{"w":"work-7","q":"q-002","c":"work-7:q-002","codeybox_custom":"Roll back, then retry."}}
            """;

        var ok = TeamsInteractionParser.TryParse(Encoding.UTF8.GetBytes(body), out var interaction, out var reason);

        Assert.True(ok, reason);
        Assert.Equal("Roll back, then retry.", interaction!.Answer);
        Assert.Equal("work-7", interaction.WorkItemId);
        Assert.Equal("q-002", interaction.QuestionId);
    }

    [Fact]
    public void Parser_RoundTripsPluginSubmitData()
    {
        // The plugin's encoder and this parser are two ends of one contract:
        // whatever the card builder mints must resolve here.
        var data = CodeyBox.TeamsPlugin.TeamsAdaptiveCard.EncodeSubmitData("work-9", "q-003", "Ship it", "work-9:q-003");
        Assert.NotNull(data);
        var body = $$"""{"type":"message","id":"activity-rt-1","from":{"id":"29:x"},"value":{{data}}}""";

        var ok = TeamsInteractionParser.TryParse(Encoding.UTF8.GetBytes(body), out var interaction, out var reason);

        Assert.True(ok, reason);
        Assert.Equal("Ship it", interaction!.Answer);
    }

    [Theory]
    [InlineData("""{"type":"typing","id":"a1","from":{"id":"u"},"value":{"w":"x","q":"y","a":"z"}}""", "unsupported Teams activity type")]
    [InlineData("""{"type":"message","id":"a1","from":{"id":"u"}}""", "activity carries no CodeyBox binding")]
    [InlineData("""{"type":"message","id":"a1","from":{"id":"u"},"value":{"w":"x","q":"y"}}""", "activity carries no answer")]
    [InlineData("""{"type":"message","id":"a1","value":{"w":"x","q":"y","a":"z"}}""", "Teams activity has no sender")]
    [InlineData("""{"type":"message","from":{"id":"u"},"value":{"w":"x","q":"y","a":"z"}}""", "Teams activity has no id")]
    public void Parser_NonAnswerActivities_FailClosed(string body, string expectedReason)
    {
        var ok = TeamsInteractionParser.TryParse(Encoding.UTF8.GetBytes(body), out var interaction, out var reason);

        Assert.False(ok);
        Assert.Null(interaction);
        Assert.Equal(expectedReason, reason);
    }
}
