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
    /// <summary>Maximum handle length accepted (matches the host lease-id cap).</summary>
    public const int MaxHandleLength = 256;

    /// <summary>True when the handle claims this backend's prefix.</summary>
    public static bool IsOurs(string? leaseId, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return leaseId is not null
            && leaseId.StartsWith(prefix + ".", StringComparison.Ordinal);
    }

    /// <summary>Builds a handle with a fresh random tail.</summary>
    public static string Build(string prefix, string kindLetter, string sandboxEnvVar)
        => Build(prefix, kindLetter, sandboxEnvVar, Guid.NewGuid().ToString("N"));

    /// <summary>Builds a handle with a caller-supplied tail (e.g. a server lease id).</summary>
    public static string Build(string prefix, string kindLetter, string sandboxEnvVar, string tail)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(kindLetter);
        ArgumentNullException.ThrowIfNull(sandboxEnvVar);
        ArgumentNullException.ThrowIfNull(tail);
        return $"{prefix}.{kindLetter}.{sandboxEnvVar}.{tail}";
    }

    /// <summary>
    /// Parses a handle into its kind letter, sandbox variable, and tail.
    /// Returns false for null, over-long, or mis-shaped handles.
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
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3]))
            return false;
        kindLetter = parts[1];
        sandboxEnvVar = parts[2];
        tail = parts[3];
        return true;
    }
}
