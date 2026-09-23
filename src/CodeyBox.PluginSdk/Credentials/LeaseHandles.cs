using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Credentials;

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

    private static bool IsValidSegment(string? value)
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
