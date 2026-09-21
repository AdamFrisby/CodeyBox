using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Validates the bare binary name of an external audit tool. The name travels
/// to a sandbox probe (<c>command -v</c>) and to an argv vector, so anything
/// that is not a bare executable name — paths, whitespace, shell
/// metacharacters — is rejected fail-closed at construction time rather than
/// near a process-execution sink.
/// </summary>
public static class ExternalToolNames
{
    /// <summary>
    /// Returns <paramref name="binary"/> unchanged when it is a bare tool
    /// name; otherwise throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string Validate(string? binary, string paramName = "toolName")
    {
        if (string.IsNullOrWhiteSpace(binary) || !ExternalToolNamePolicy.IsValidBinaryName(binary))
            throw new ArgumentException(
                $"External audit tool name must be a bare executable name (letters, digits, '.', '_' or '-'; no paths, whitespace, or shell metacharacters): '{binary}'.",
                paramName);
        return binary;
    }
}
