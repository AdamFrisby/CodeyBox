namespace CodeyBox.Core;

/// <summary>
/// Wire constants shared by the host's inbound interaction endpoint and the
/// notification provider plugins that mint callback payloads. The endpoint
/// (<c>POST {RoutePrefix}/{provider}</c>) verifies and bounds what it
/// accepts; a plugin that renders callbacks must agree with those bounds or
/// every interaction it emits fails verification — so, like
/// <see cref="NotificationCorrelation"/>, the contract lives in Core where
/// both sides can reference it. Do not re-declare these values elsewhere.
/// </summary>
public static class InteractionContract
{
    /// <summary>Route prefix the inbound interaction endpoint is mounted at;
    /// the provider name follows as the last segment.</summary>
    public const string RoutePrefix = "/webhooks/interactions";

    /// <summary>Upper bound the endpoint places on the answer field, in
    /// characters. A button whose answer would exceed it can never resolve.</summary>
    public const int MaxAnswerChars = 4000;

    /// <summary>Header carrying the payload signature for schemes that use
    /// the CodeyBox header convention (<c>hmac-sha256</c> by default, and
    /// <c>ntfy-hmac</c> always — the ntfy provider mints buttons with this
    /// header name, so its verifier reads no other).</summary>
    public const string DefaultSignatureHeader = "X-CodeyBox-Signature";
}
