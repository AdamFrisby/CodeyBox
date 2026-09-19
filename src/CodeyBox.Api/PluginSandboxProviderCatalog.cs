using System.Diagnostics.CodeAnalysis;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Host-owned index of plugin-contributed sandbox provider kinds.
///
/// <para>Plugins implement <see cref="ISandboxProvider"/> and are registered by
/// <see cref="Orchestrator.PluginLoader"/> under that Core interface; the composition
/// root collects them here, keyed by their normalised <see cref="ISandboxProvider.Name"/>
/// (trimmed, lowercase, exact ordinal match — never substring). A kind in this catalog
/// can be named by <see cref="SandboxMember.ProviderKind"/> and is constructed once:
/// the catalog holds the DI singleton instance, and the provider registry shares it
/// across every member naming the kind, exactly like a built-in kind.</para>
///
/// <para>The catalog is the trust boundary for the kind namespace, not the plugin:
/// names are bounded and validated here, collisions with built-in kinds are refused
/// (a plugin must not shadow a reviewed backend), and duplicate claims across plugins
/// fail closed. Egress classification is deliberately absent here — it stays in
/// <see cref="HostPlatformSupport.GetEgressEnforcement"/>, where every plugin kind
/// classifies <see cref="EgressEnforcementLocation.NotEnforced"/> unless promoted by
/// an in-tree change.</para>
/// </summary>
public sealed class PluginSandboxProviderCatalog
{
    private readonly Dictionary<string, Entry> _byKind;

    /// <summary>Empty catalog: no plugin sandbox providers. Used when no plugins are loaded.</summary>
    public static PluginSandboxProviderCatalog Empty { get; } = new([]);

    /// <summary>
    /// Builds the catalog from plugin providers. Entries are processed in plugin-id
    /// order so duplicate-kind diagnostics are independent of discovery order.
    /// </summary>
    /// <param name="entries">One entry per plugin type implementing <see cref="ISandboxProvider"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown for a blank provider name, an over-long name or one containing control
    /// characters, a kind colliding with a built-in provider, or two plugins claiming
    /// the same kind. All fail closed at startup.
    /// </exception>
    public PluginSandboxProviderCatalog(IEnumerable<(string PluginId, ISandboxProvider Provider)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byKind = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pluginId, provider) in entries.OrderBy(static e => e.PluginId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new InvalidOperationException("A plugin sandbox provider entry names no plugin id; refusing to register.");
            ArgumentNullException.ThrowIfNull(provider);
            var rawName = provider.Name;
            if (string.IsNullOrWhiteSpace(rawName))
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' implements ISandboxProvider with a blank Name; " +
                    $"a sandbox provider kind must be a non-empty id. Fix the plugin's Name or remove it from the allowlist.");
            var kind = rawName.Trim().ToLowerInvariant();
            if (kind.Length > SandboxProviderIdPolicy.MaximumLength || kind.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' registers sandbox provider kind '{rawName.Trim()}' which exceeds the kind safety bound " +
                    $"(at most {SandboxProviderIdPolicy.MaximumLength} characters, no control characters); refusing to register.");
            if (SandboxProviderKinds.IsRegistered(kind))
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' registers sandbox provider kind '{kind}' which collides with a built-in provider kind. " +
                    $"A plugin must not shadow a reviewed backend; rename the plugin provider's Name.");
            if (_byKind.TryGetValue(kind, out var existing))
                throw new InvalidOperationException(
                    $"Plugins '{existing.PluginId}' and '{pluginId}' both register sandbox provider kind '{kind}'. " +
                    $"A kind must be claimed by exactly one plugin; rename one provider or remove one plugin from the allowlist.");
            _byKind[kind] = new Entry(pluginId, provider);
        }
        Kinds = new HashSet<string>(_byKind.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Normalised plugin-contributed kinds (lowercase, ordinal ignore-case).</summary>
    public IReadOnlySet<string> Kinds { get; }

    /// <summary>
    /// Every kind the composition root can build: built-ins plus plugin-contributed
    /// kinds. Used to validate member references so an unknown kind fails closed with
    /// a message naming everything registered.
    /// </summary>
    public IReadOnlySet<string> AllKnownKinds
    {
        get
        {
            var union = new HashSet<string>(SandboxProviderKinds.All, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(Kinds);
            return union;
        }
    }

    /// <summary>True when <paramref name="kind"/> names a plugin-contributed provider.</summary>
    public bool IsPluginKind(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && _byKind.ContainsKey(kind.Trim());

    /// <summary>
    /// Plugin id contributing <paramref name="kind"/>, or "(built-in)" when the kind
    /// is not plugin-contributed. For startup diagnostics only.
    /// </summary>
    public string PluginIdFor(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return _byKind.TryGetValue(kind.Trim(), out var entry) ? entry.PluginId : "(built-in)";
    }

    /// <summary>Returns the shared plugin provider instance for <paramref name="kind"/>, or false when unknown.</summary>
    public bool TryGetProvider(string kind, [NotNullWhen(true)] out ISandboxProvider? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (_byKind.TryGetValue(kind.Trim(), out var entry))
        {
            provider = entry.Provider;
            return true;
        }
        provider = null;
        return false;
    }

    private sealed record Entry(string PluginId, ISandboxProvider Provider);
}
