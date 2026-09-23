using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// Typed failure for every Doppler backend call. The failure taxonomy,
/// infrastructure classification, status mapping, and message-truncation
/// discipline are the shared <see cref="CredentialException"/> contract —
/// this type exists so callers can catch a Doppler-typed failure.
/// Messages carry only safe fields — HTTP status, host, project/config/
/// secret names, lease ids (identity, never values).
/// </summary>
public sealed class DopplerException : CredentialException
{
    /// <summary>Display name stamped on every Doppler failure and transport message.</summary>
    internal const string BackendName = "Doppler";

    public DopplerException(
        CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, statusCode, retryAfterSeconds)
    {
    }

    public DopplerException(
        CredentialFailureKind kind, string message, Exception inner,
        int? statusCode = null, int? retryAfterSeconds = null)
        : base(BackendName, kind, message, inner, statusCode, retryAfterSeconds)
    {
    }

    /// <summary>Factory matching <see cref="CredentialExceptionFactory"/> for the shared transport.</summary>
    internal static DopplerException Create(
        CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
        => inner is null
            ? new DopplerException(kind, message, statusCode, retryAfterSeconds)
            : new DopplerException(kind, message, inner, statusCode, retryAfterSeconds);
}
