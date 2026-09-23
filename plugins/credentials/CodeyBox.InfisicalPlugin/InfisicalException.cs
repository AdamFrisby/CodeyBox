using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// Typed failure for every Infisical backend call. The failure taxonomy,
/// infrastructure classification, status mapping, and message-truncation
/// discipline are the shared <see cref="CredentialException"/> contract —
/// this type exists so callers can catch an Infisical-typed failure.
/// Messages carry only safe fields — HTTP status, host, secret key names,
/// lease ids (identity, never values).
/// </summary>
public sealed class InfisicalException : CredentialException
{
    /// <summary>Display name stamped on every Infisical failure and transport message.</summary>
    internal const string BackendName = "Infisical";

    public InfisicalException(
        CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, statusCode, retryAfterSeconds)
    {
    }

    public InfisicalException(
        CredentialFailureKind kind, string message, Exception inner,
        int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, inner, statusCode, retryAfterSeconds)
    {
    }

    /// <summary>Factory matching <see cref="CredentialExceptionFactory"/> for the shared transport.</summary>
    internal static InfisicalException Create(
        CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
        => inner is null
            ? new InfisicalException(kind, message, statusCode, retryAfterSeconds)
            : new InfisicalException(kind, message, inner, statusCode, retryAfterSeconds);
}
