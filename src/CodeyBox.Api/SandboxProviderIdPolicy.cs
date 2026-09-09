namespace CodeyBox.Api;

/// <summary>
/// Provider-neutral bounds for opaque lifecycle-provider identities crossing
/// API and durable-session boundaries. Concrete provider registration applies
/// its stricter identifier syntax in addition to this guard.
/// </summary>
internal static class SandboxProviderIdPolicy
{
    internal const int MaximumLength = 128;

    internal static bool IsValidOpaque(string? providerId) =>
        providerId is { Length: > 0 and <= MaximumLength }
        && !string.IsNullOrWhiteSpace(providerId)
        && !providerId.Any(char.IsControl);
}
