using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Notifications;

/// <summary>
/// Outcome of verifying one inbound interaction against its provider scheme.
/// Verification runs over the raw request body BEFORE the body is parsed
/// for meaning; an invalid result must never be partially processed.
/// </summary>
public sealed record InteractionVerificationResult
{
    public required bool Valid { get; init; }
    public required string FailureReason { get; init; }

    public static InteractionVerificationResult Ok() =>
        new() { Valid = true, FailureReason = string.Empty };

    public static InteractionVerificationResult Fail(string reason) =>
        new() { Valid = false, FailureReason = reason };
}

/// <summary>
/// Verifies that an inbound interaction genuinely came from the claimed
/// platform. Implementations check the signature over the raw body bytes
/// plus the sender timestamp for replay protection. Secrets resolve from
/// the environment (never from config values).
/// </summary>
public interface IInteractionVerifier
{
    /// <summary>Provider name this verifier handles (exact, case-insensitive).</summary>
    string Provider { get; }

    Task<InteractionVerificationResult> VerifyAsync(
        byte[] rawBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct);
}

/// <summary>
/// Shared verification plumbing: env-secret resolution, timestamp replay
/// window, and constant-time signature comparison. Secrets come from the
/// credential chain (environment), not configuration files.
/// </summary>
public abstract class InteractionVerifierBase : IInteractionVerifier
{
    private readonly Func<InteractionProviderOptions?> _optsAccessor;
    private readonly Func<string, string?> _envReader;
    private readonly TimeProvider _clock;

    protected InteractionVerifierBase(
        Func<InteractionProviderOptions?> optsAccessor,
        Func<string, string?>? envReader = null,
        TimeProvider? clock = null)
    {
        _optsAccessor = optsAccessor;
        _envReader = envReader ?? Environment.GetEnvironmentVariable;
        _clock = clock ?? TimeProvider.System;
    }

    public abstract string Provider { get; }

    protected InteractionProviderOptions? Options => _optsAccessor();

    protected TimeProvider Clock => _clock;

    public abstract Task<InteractionVerificationResult> VerifyAsync(
        byte[] rawBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct);

    protected bool TryResolveSecret(out string secret, out string failure)
    {
        secret = string.Empty;
        var envVar = Options?.SigningSecretEnvVar;
        if (string.IsNullOrWhiteSpace(envVar))
        {
            failure = "no signing secret configured for provider";
            return false;
        }
        var value = _envReader(envVar);
        if (string.IsNullOrEmpty(value))
        {
            failure = "signing secret is not set in environment";
            return false;
        }
        secret = value;
        failure = string.Empty;
        return true;
    }

    protected bool TryCheckTimestamp(string? timestampValue, out string failure)
    {
        failure = string.Empty;
        var window = Options?.ReplayWindow ?? TimeSpan.FromMinutes(5);
        if (string.IsNullOrWhiteSpace(timestampValue)
            || !long.TryParse(timestampValue, out var seconds))
        {
            failure = "missing or invalid timestamp";
            return false;
        }
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var age = Clock.GetUtcNow() - sentAt;
        if (age < TimeSpan.Zero - TimeSpan.FromMinutes(1) || age > window)
        {
            failure = "timestamp outside replay window";
            return false;
        }
        return true;
    }

    protected static string Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return string.Empty;
    }

    protected static bool SignaturesEqual(string providedHex, byte[] computedHash)
    {
        var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computedHex),
            Encoding.ASCII.GetBytes(providedHex.ToLowerInvariant()));
    }

    protected static byte[] HmacSha256(string secret, byte[] body) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
}
