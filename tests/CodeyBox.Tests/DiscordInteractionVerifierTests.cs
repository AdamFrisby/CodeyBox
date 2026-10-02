using System.Text;
using CodeyBox.Notifications;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for Discord Ed25519 verification: a correctly signed envelope
/// verifies, while tampered, replayed, unsigned, or misconfigured deliveries
/// are rejected before any semantic processing.
/// </summary>
public sealed class DiscordInteractionVerifierTests
{
    private static readonly byte[] PrivateSeed = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private readonly Ed25519PrivateKeyParameters _privateKey = new(PrivateSeed, 0);
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private string PublicHex() =>
        Convert.ToHexString(_privateKey.GeneratePublicKey().GetEncoded()).ToLowerInvariant();

    private DiscordInteractionVerifier BuildVerifier(TimeSpan? replayWindow = null)
    {
        var options = new InteractionProviderOptions
        {
            Provider = "discord",
            Scheme = "discord-ed25519",
            SigningSecretEnvVar = "CODEYBOX_TEST_DISCORD_UNIT_KEY",
            ReplayWindow = replayWindow ?? TimeSpan.FromMinutes(5),
        };
        Environment.SetEnvironmentVariable(options.SigningSecretEnvVar, PublicHex());
        return new DiscordInteractionVerifier("discord", () => options, clock: _clock);
    }

    private (byte[] Body, Dictionary<string, string> Headers) SignedEnvelope()
    {
        var body = Encoding.UTF8.GetBytes("""{"type":3,"id":"1"}""");
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var message = Encoding.ASCII.GetBytes(timestamp).Concat(body).ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return (body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Signature-Ed25519"] = Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant(),
            ["X-Signature-Timestamp"] = timestamp,
        });
    }

    [Fact]
    public async Task ValidSignature_Verifies()
    {
        var verifier = BuildVerifier();
        try
        {
            var (body, headers) = SignedEnvelope();

            var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

            Assert.True(result.Valid, result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        }
    }

    [Fact]
    public async Task TamperedBody_FailsVerification()
    {
        var verifier = BuildVerifier();
        try
        {
            var (body, headers) = SignedEnvelope();

            var result = await verifier.VerifyAsync([.. body, (byte)' '], headers, CancellationToken.None);

            Assert.False(result.Valid);
            Assert.NotEmpty(result.FailureReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        }
    }

    [Fact]
    public async Task StaleTimestamp_FailsAsReplay()
    {
        var verifier = BuildVerifier();
        try
        {
            var (body, headers) = SignedEnvelope();
            var stale = _clock.GetUtcNow().AddHours(-1).ToUnixTimeSeconds().ToString();
            var message = Encoding.ASCII.GetBytes(stale).Concat(body).ToArray();
            var signer = new Ed25519Signer();
            signer.Init(true, _privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            headers["X-Signature-Ed25519"] = Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
            headers["X-Signature-Timestamp"] = stale;

            var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Contains("replay", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        }
    }

    [Fact]
    public async Task MissingSignature_FailsVerification()
    {
        var verifier = BuildVerifier();
        try
        {
            var (body, _) = SignedEnvelope();

            var result = await verifier.VerifyAsync(body, new Dictionary<string, string>(), CancellationToken.None);

            Assert.False(result.Valid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        }
    }

    [Fact]
    public async Task MissingPublicKey_FailsClosed()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        var options = new InteractionProviderOptions
        {
            Provider = "discord",
            Scheme = "discord-ed25519",
            SigningSecretEnvVar = "CODEYBOX_TEST_DISCORD_UNIT_KEY",
        };
        var verifier = new DiscordInteractionVerifier("discord", () => options, clock: _clock);
        var (body, headers) = SignedEnvelope();

        var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

        Assert.False(result.Valid);
    }

    [Fact]
    public async Task MalformedPublicKey_FailsClosed()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", "not-hex");
        try
        {
            var options = new InteractionProviderOptions
            {
                Provider = "discord",
                Scheme = "discord-ed25519",
                SigningSecretEnvVar = "CODEYBOX_TEST_DISCORD_UNIT_KEY",
            };
            var verifier = new DiscordInteractionVerifier("discord", () => options, clock: _clock);
            var (body, headers) = SignedEnvelope();

            var result = await verifier.VerifyAsync(body, headers, CancellationToken.None);

            Assert.False(result.Valid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_TEST_DISCORD_UNIT_KEY", null);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
