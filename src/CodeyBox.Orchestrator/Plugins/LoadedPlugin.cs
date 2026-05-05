namespace CodeyBox.Orchestrator;

/// <summary>
/// Metadata for a successfully discovered and validated plugin type.
/// One instance per <c>[CodeyBoxPlugin]</c>-decorated class.
/// </summary>
public sealed record LoadedPlugin(
    /// <summary>Plugin ID from <see cref="CodeyBox.PluginSdk.CodeyBoxPluginAttribute.Id"/>.</summary>
    string PluginId,
    /// <summary>Display name from the attribute.</summary>
    string DisplayName,
    /// <summary>Absolute path of the assembly the type was loaded from.</summary>
    string AssemblyPath,
    /// <summary>
    /// Types to register in DI. Typically a single-element list containing the
    /// plugin entry type. The loader registers each element under the
    /// <c>CodeyBox.Core</c> interfaces it implements.
    /// </summary>
    IReadOnlyList<Type> RegisteredTypes);
