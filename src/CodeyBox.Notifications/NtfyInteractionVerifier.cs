using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// ntfy action-button verification (<c>ntfy-hmac</c> scheme). ntfy action
/// buttons are client-side: the subscribed device issues the callback request
/// itself, so the platform signs nothing and there is no sender timestamp to
/// window. Instead the ntfy provider mints each button's body at publish time
/// and MACs those exact bytes — <c>X-CodeyBox-Signature: sha256=&lt;hex
/// HMAC-SHA256 over the raw body&gt;</c> — keyed by the shared interaction
/// secret resolved from <see cref="InteractionProviderOptions.SigningSecretEnvVar"/>.
/// The MAC is bound to one concrete answer payload, so a captured header can
/// replay only the decision that button already grants; replays land on the
/// endpoint's dedup claim and the question-state checks, which is the replay
/// protection this scheme relies on in place of a timestamp window.
///
/// <para>The signature header is pinned to
/// <see cref="InteractionContract.DefaultSignatureHeader"/>: the publisher
/// mints buttons with that name and cannot see this host's options, so a
/// <see cref="InteractionProviderOptions.SignatureHeader"/> override cannot
/// apply to this scheme.</para>
/// </summary>
public sealed class NtfyInteractionVerifier : InteractionVerifierBase
{
    private readonly string _provider;

    public NtfyInteractionVerifier(
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

        var signature = Header(headers, InteractionContract.DefaultSignatureHeader);
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
