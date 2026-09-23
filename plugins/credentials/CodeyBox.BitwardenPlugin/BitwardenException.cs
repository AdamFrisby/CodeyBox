using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Typed failure for every Bitwarden backend call. The failure taxonomy,
/// infrastructure classification, status mapping, and message-truncation
/// discipline are the shared <see cref="CredentialException"/> contract —
/// this type exists so callers can catch a Bitwarden-typed failure.
/// Messages carry only safe fields — HTTP status, host, secret ids/keys,
/// organisation and project ids, lease ids (identity, never values).
/// </summary>
public sealed class BitwardenException : CredentialException
{
    /// <summary>Display name stamped on every Bitwarden failure and transport message.</summary>
    internal const string BackendName = "Bitwarden";

    public BitwardenException(
        CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, statusCode, retryAfterSeconds)
    {
    }

    public BitwardenException(
        CredentialFailureKind kind, string message, Exception inner,
        int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, inner, statusCode, retryAfterSeconds)
    {
    }

    /// <summary>Factory matching <see cref="CredentialExceptionFactory"/> for the shared transport.</summary>
    internal static BitwardenException Create(
        CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
        => inner is null
            ? new BitwardenException(kind, message, statusCode, retryAfterSeconds)
            : new BitwardenException(kind, message, inner, statusCode, retryAfterSeconds);
}
