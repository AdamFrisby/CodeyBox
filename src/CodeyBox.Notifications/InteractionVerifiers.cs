using System.Text;

namespace CodeyBox.Notifications;

/// <summary>
/// Generic HMAC-SHA256 interaction verification: the sender posts the raw
/// JSON body with <c>X-CodeyBox-Signature: sha256=&lt;hex&gt;</c> (HMAC over
/// the raw body bytes) and <c>X-CodeyBox-Timestamp</c> (unix seconds).
/// Header names are overridable per provider in
/// <see cref="InteractionProviderOptions"/>. Custom chat-app and
/// automation integrations use this scheme.
/// </summary>
public sealed class HmacInteractionVerifier : InteractionVerifierBase
{
    private readonly string _provider;

    public HmacInteractionVerifier(
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
        if (!TryResolveSecret(out var secret, out var failure))
            return Task.FromResult(InteractionVerificationResult.Fail(failure));

        var signatureHeader = Options?.SignatureHeader;
        if (string.IsNullOrWhiteSpace(signatureHeader))
            signatureHeader = "X-CodeyBox-Signature";
        var timestampHeader = Options?.TimestampHeader;
        if (string.IsNullOrWhiteSpace(timestampHeader))
            timestampHeader = "X-CodeyBox-Timestamp";

        if (!TryCheckTimestamp(Header(headers, timestampHeader), out var tsFailure))
            return Task.FromResult(InteractionVerificationResult.Fail(tsFailure));

        var signature = Header(headers, signatureHeader);
        const string Prefix = "sha256=";
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(InteractionVerificationResult.Fail("missing or malformed signature"));

        var providedHex = signature[Prefix.Length..];
        var computed = HmacSha256(secret, rawBody);
        if (!SignaturesEqual(providedHex, computed))
            return Task.FromResult(InteractionVerificationResult.Fail("signature mismatch"));

        return Task.FromResult(InteractionVerificationResult.Ok());
    }
}

/// <summary>
/// Slack request verification (<c>v0=&lt;hex HMAC-SHA256 over
/// "v0:{timestamp}:{raw_body}"&gt;</c>), headers
/// <c>X-Slack-Signature</c> / <c>X-Slack-Request-Timestamp</c>.
/// The signing secret resolves from the env var named by
/// <see cref="InteractionProviderOptions.SigningSecretEnvVar"/>.
/// </summary>
public sealed class SlackInteractionVerifier : InteractionVerifierBase
{
    private readonly string _provider;

    public SlackInteractionVerifier(
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
        if (!TryResolveSecret(out var secret, out var failure))
            return Task.FromResult(InteractionVerificationResult.Fail(failure));

        var signatureHeader = Options?.SignatureHeader;
        if (string.IsNullOrWhiteSpace(signatureHeader))
            signatureHeader = "X-Slack-Signature";
        var timestampHeader = Options?.TimestampHeader;
        if (string.IsNullOrWhiteSpace(timestampHeader))
            timestampHeader = "X-Slack-Request-Timestamp";

        var timestampValue = Header(headers, timestampHeader);
        if (!TryCheckTimestamp(timestampValue, out var tsFailure))
            return Task.FromResult(InteractionVerificationResult.Fail(tsFailure));

        var signature = Header(headers, signatureHeader);
        const string Prefix = "v0=";
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(InteractionVerificationResult.Fail("missing or malformed signature"));

        var providedHex = signature[Prefix.Length..];
        var basis = Encoding.UTF8.GetBytes($"v0:{timestampValue}:{Encoding.UTF8.GetString(rawBody)}");
        var computed = HmacSha256(secret, basis);
        if (!SignaturesEqual(providedHex, computed))
            return Task.FromResult(InteractionVerificationResult.Fail("signature mismatch"));

        return Task.FromResult(InteractionVerificationResult.Ok());
    }
}
