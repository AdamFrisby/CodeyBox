namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Safe exception-message truncation shared by credential-provider plugins.
/// Messages flow into host logs and the persisted lease store, so embedded
/// control characters must not forge log lines regardless of which backend
/// constructed the text. One implementation so the sanitisation policy
/// cannot fork per backend.
/// </summary>
public static class CredentialMessages
{
    /// <summary>
    /// Flattens carriage returns and newlines to spaces, then caps the
    /// message at <paramref name="maxChars"/>. Empty input yields
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string Truncate(string? message, string fallback, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxChars, 0);
        if (string.IsNullOrEmpty(message))
            return fallback;
        var flat = message.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= maxChars ? flat : flat[..maxChars];
    }
}
