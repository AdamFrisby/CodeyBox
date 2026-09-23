using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// Typed failure for every 1Password backend call. The failure taxonomy,
/// infrastructure classification, status mapping, and message-truncation
/// discipline are the shared <see cref="CredentialException"/> contract —
/// this type exists so callers can catch a 1Password-typed failure.
/// Messages carry only safe fields — HTTP status, host, vault/item/field
/// names, lease ids (identity, never values).
/// </summary>
public sealed class OnePasswordException : CredentialException
{
    public OnePasswordException(
        CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base("1Password", kind, message, statusCode, retryAfterSeconds)
    {
    }

    public OnePasswordException(
        CredentialFailureKind kind, string message, Exception inner, int? statusCode = null)
        : base("1Password", kind, message, inner, statusCode)
    {
    }

    /// <summary>Factory matching <see cref="CredentialExceptionFactory"/> for the shared transport.</summary>
    internal static OnePasswordException Create(
        CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
        => inner is null
            ? new OnePasswordException(kind, message, statusCode, retryAfterSeconds)
            : new OnePasswordException(kind, message, inner, statusCode);
}
