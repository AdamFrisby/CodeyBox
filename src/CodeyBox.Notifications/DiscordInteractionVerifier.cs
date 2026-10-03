using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CodeyBox.Notifications;

/// <summary>
/// Discord interaction verification (<c>discord-ed25519</c> scheme).
/// Discord signs every interaction envelope with Ed25519 over
/// <c>timestamp + raw_body</c>, delivered in
/// <c>X-Signature-Ed25519</c> (hex) and <c>X-Signature-Timestamp</c>
/// (unix seconds). The 32-byte public key resolves from the env var
/// named by <see cref="InteractionProviderOptions.SigningSecretEnvVar"/>
/// (hex-encoded, from the Developer Portal → General Information →
/// Application Public Key) — never from config values.
///
/// <para>The timestamp doubles as the replay window: deliveries older
/// than the window (or more than a minute in the future) are rejected
/// before any semantic use.</para>
/// </summary>
public sealed class DiscordInteractionVerifier : InteractionVerifierBase
{
    private readonly string _provider;

    public DiscordInteractionVerifier(
        string provider,
        Func<InteractionProviderOptions?> optsAccessor,
        Func<string, string?>? envReader = null,
        TimeProvider? clock = null)
        : base(optsAccessor, envReader, clock)
    {
        _provider = provider;
    }

    public override string Provider => _provider;

    public override Task<InteractionVerificationResult> VerifyAsync(
        byte[] rawBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        if (!TryResolvePublicKey(out var publicKey, out var failure))
            return Task.FromResult(InteractionVerificationResult.Fail(failure));

        var signatureHeader = Options?.SignatureHeader;
        if (string.IsNullOrWhiteSpace(signatureHeader))
            signatureHeader = "X-Signature-Ed25519";
        var timestampHeader = Options?.TimestampHeader;
        if (string.IsNullOrWhiteSpace(timestampHeader))
            timestampHeader = "X-Signature-Timestamp";

        var timestampValue = Header(headers, timestampHeader);
        if (!TryCheckTimestamp(timestampValue, out var tsFailure))
            return Task.FromResult(InteractionVerificationResult.Fail(tsFailure));

        var signature = Header(headers, signatureHeader);
        if (!TryParseHex(signature, 64, out var signatureBytes))
            return Task.FromResult(InteractionVerificationResult.Fail("missing or malformed signature"));

        var message = Encoding.ASCII.GetBytes(timestampValue).Concat(rawBody).ToArray();
        bool valid;
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            signer.BlockUpdate(message, 0, message.Length);
            valid = signer.VerifySignature(signatureBytes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(InteractionVerificationResult.Fail("signature mismatch"));
        }

        return Task.FromResult(valid
            ? InteractionVerificationResult.Ok()
            : InteractionVerificationResult.Fail("signature mismatch"));
    }

    private bool TryResolvePublicKey(out byte[] publicKey, out string failure)
    {
        publicKey = [];
        if (!TryResolveSecret(out var secret, out failure))
            return false;
        if (!TryParseHex(secret.Trim(), 32, out publicKey))
        {
            failure = "signing secret is not a 32-byte hex public key";
            return false;
        }
        return true;
    }

    private static bool TryParseHex(string? text, int expectedBytes, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (trimmed.Length != expectedBytes * 2)
            return false;
        try
        {
            bytes = Convert.FromHexString(trimmed);
            return bytes.Length == expectedBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
