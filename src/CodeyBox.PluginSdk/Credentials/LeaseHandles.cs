using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Parses a backend's typed lease handle — the signature every plugin's
/// <c>TryParse</c> shares, so <see cref="LeaseHandles.ParseOrThrow"/> can
/// gate renew/revoke identically across backends.
/// </summary>
public delegate bool CredentialLeaseParser<TParsed>(string? leaseId, out TParsed parsed);

/// <summary>
/// Shared lease-handle shape for credential-provider plugins. Handles are
/// self-describing — <c>{prefix}.{kindLetter}.{sandboxVar}.{tail}</c> — so
/// renew and revoke keep working after an orchestrator restart, when the
/// provider's in-memory issue registry is gone. Names only, never values,
/// so handles are safe to log, persist, and reconcile. The prefix and kind
/// letters stay per backend (each provider maps its letters to its own
/// kind enum); the split/prefix/length/shape validation lives here once.
/// </summary>
public static class LeaseHandles
{
    /// <summary>Maximum handle length accepted (the host lease-id cap).</summary>
    public const int MaxHandleLength = SecretLeasingOptions.MaxLeaseIdLength;

    /// <summary>Builds a handle with a fresh random tail.</summary>
    public static string Build(string prefix, string kindLetter, string sandboxEnvVar)
        => Build(prefix, kindLetter, sandboxEnvVar, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Builds a handle with a caller-supplied tail (e.g. a server lease id).
    /// Every segment must be non-empty and free of <c>.</c> and control
    /// characters: a segment violating that would produce a handle
    /// <see cref="TryParse"/> cannot round-trip (and the host lease-id
    /// policy refuses), so the failure surfaces here at issue time instead
    /// of minting a lease that can never be renewed or revoked.
    /// </summary>
    public static string Build(string prefix, string kindLetter, string sandboxEnvVar, string tail)
    {
        ValidateSegment(prefix, nameof(prefix));
        ValidateSegment(kindLetter, nameof(kindLetter));
        ValidateSegment(sandboxEnvVar, nameof(sandboxEnvVar));
        ValidateSegment(tail, nameof(tail));
        var handle = $"{prefix}.{kindLetter}.{sandboxEnvVar}.{tail}";
        if (handle.Length > MaxHandleLength)
            throw new ArgumentException(
                $"Lease handle exceeds {MaxHandleLength} characters.");
        return handle;
    }

    /// <summary>
    /// Parses a handle into its kind letter, sandbox variable, and tail.
    /// Returns false for null, over-long, or mis-shaped handles, and for
    /// segments <see cref="Build(string, string, string, string)"/> would
    /// refuse — so a parsed handle is always a shape Build could emit and
    /// never carries control characters into logs.
    /// </summary>
    public static bool TryParse(
        string? leaseId, string prefix,
        out string kindLetter, out string sandboxEnvVar, out string tail)
    {
        kindLetter = string.Empty;
        sandboxEnvVar = string.Empty;
        tail = string.Empty;
        if (string.IsNullOrWhiteSpace(leaseId) || leaseId.Length > MaxHandleLength)
            return false;
        var parts = leaseId.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], prefix, StringComparison.Ordinal)
            || !IsValidSegment(parts[1])
            || !IsValidSegment(parts[2])
            || !IsValidSegment(parts[3]))
            return false;
        kindLetter = parts[1];
        sandboxEnvVar = parts[2];
        tail = parts[3];
        return true;
    }

    /// <summary>
    /// The lease-handle gate every credential provider applies before renew
    /// and revoke: a foreign or malformed handle is a typed
    /// <see cref="CredentialFailureKind.Misconfigured"/> failure carrying the
    /// backend's own exception type, identical across backends. The handle is
    /// already segment-sanitised by the parser, so echoing it is safe.
    /// </summary>
    public static TParsed ParseOrThrow<TParsed>(
        string? leaseId,
        string backend,
        CredentialLeaseParser<TParsed> parser,
        CredentialExceptionFactory exceptionFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        if (!parser(leaseId, out var parsed))
        {
            throw exceptionFactory(
                CredentialFailureKind.Misconfigured,
                $"Lease '{leaseId ?? string.Empty}' is not a valid {backend} lease handle.",
                null, null, null);
        }
        return parsed;
    }

    /// <summary>
    /// True when <paramref name="value"/> can ride in one handle segment:
    /// non-empty, no <c>.</c> (the segment delimiter), no control
    /// characters. This is the same gate
    /// <see cref="Build(string, string, string, string)"/> enforces —
    /// exposed so a provider can validate a backend-supplied identifier
    /// (a server lease id) before it ever reaches a handle, a log line,
    /// or an exception message.
    /// </summary>
    public static bool IsValidSegment(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.IndexOf('.') < 0
            && !value.Any(static c => char.IsControl(c));

    private static void ValidateSegment(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Lease-handle segment must be non-empty.", parameterName);
        if (value.IndexOf('.') >= 0 || value.Any(static c => char.IsControl(c)))
            throw new ArgumentException(
                "Lease-handle segment must not contain '.' or control characters.", parameterName);
    }
}
