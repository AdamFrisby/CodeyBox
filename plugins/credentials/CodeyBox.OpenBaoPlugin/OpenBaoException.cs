using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>
/// Typed failure for every OpenBao backend call. The failure taxonomy,
/// infrastructure classification, status mapping, and message-truncation
/// discipline are the shared <see cref="CredentialException"/> contract —
/// this type exists so callers can catch an OpenBao-typed failure.
/// Messages carry only safe fields — HTTP status, host, paths, secret
/// identifiers, lease ids (identity, never values or tokens).
/// </summary>
public sealed class OpenBaoException : CredentialException
{
    /// <summary>Display name stamped on every OpenBao failure and transport message.</summary>
    internal const string BackendName = "OpenBao";

    public OpenBaoException(
        CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, statusCode, retryAfterSeconds)
    {
    }

    public OpenBaoException(
        CredentialFailureKind kind, string message, Exception inner,
        int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, inner, statusCode, retryAfterSeconds)
    {
    }

    /// <summary>Factory matching <see cref="CredentialExceptionFactory"/> for the shared transport.</summary>
    internal static OpenBaoException Create(
        CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
        => inner is null
            ? new OpenBaoException(kind, message, statusCode, retryAfterSeconds)
            : new OpenBaoException(kind, message, inner, statusCode, retryAfterSeconds);
}
