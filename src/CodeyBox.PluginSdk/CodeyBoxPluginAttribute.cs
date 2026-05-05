namespace CodeyBox.PluginSdk;

/// <summary>
/// Marks a class as a CodeyBox plugin entry point. Apply to every public class
/// that implements one or more <c>CodeyBox.Core</c> interfaces and should be
/// discovered and registered at host startup.
///
/// <para>One class may carry this attribute per plugin ID. Multiple classes in
/// the same assembly carrying different IDs are each treated as independent
/// plugins and validated separately.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CodeyBoxPluginAttribute : Attribute
{
    /// <summary>
    /// Stable reverse-domain identifier. Used for allowlist matching and
    /// configuration scoping. Must be unique across all loaded plugins.
    /// Example: <c>"myorg.custom-auditor"</c>.
    /// </summary>
    public string Id { get; }

    /// <summary>Human-readable name shown in logs and the admin dashboard.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Minimum host API version this plugin requires. The host rejects the
    /// plugin if its own version does not satisfy this minimum.
    /// Format: <c>"major.minor"</c> (e.g. <c>"1.0"</c>).
    /// </summary>
    public string MinHostApiVersion { get; }

    public CodeyBoxPluginAttribute(string id, string displayName, string minHostApiVersion = "1.0")
    {
        Id = id;
        DisplayName = displayName;
        MinHostApiVersion = minHostApiVersion;
    }
}
