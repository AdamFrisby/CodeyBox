using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raw (unvalidated) tool declaration read from assembly metadata.
/// Validation happens in <see cref="PluginToolRequirement.TryCreate"/>.
/// </summary>
internal sealed record PluginToolDeclaration(
    string? Binary,
    string? AptPackage,
    string? InstallHint);

/// <summary>
/// One <c>[CodeyBoxPlugin]</c>-decorated type found by metadata inspection,
/// before any executable code from the assembly is loaded.
/// </summary>
internal sealed record PluginMetadataCandidate(
    /// <summary>Metadata full name of the decorated type (namespace + name).</summary>
    string TypeFullName,
    string PluginId,
    string DisplayName,
    string MinHostApiVersion,
    IReadOnlyList<PluginToolDeclaration> ToolDeclarations);

/// <summary>
/// Outcome of <see cref="PluginAssemblyInspector.InspectWithOutcome"/>.
/// <see cref="Error"/> is null on success — including the "valid assembly
/// with no plugin types" case, which is not an inspection failure.
/// </summary>
internal sealed record PluginInspectionOutcome(
    IReadOnlyList<PluginMetadataCandidate> Candidates,
    string? Error,
    bool IsStaleContracts);

/// <summary>
/// Inspects a plugin assembly's metadata <em>without loading it for
/// execution</em>. Uses <see cref="MetadataLoadContext"/> so no static
/// constructors, module initializers, or attribute constructors from the
/// plugin ever run: the host learns each candidate's plugin ID, API version,
/// and tool declarations purely from metadata, then decides whether the
/// assembly may be loaded at all. A disabled plugin's assembly is never
/// loaded — not even into the isolated load context.
/// </summary>
internal static class PluginAssemblyInspector
{
    internal const string PluginAttributeFullName = "CodeyBox.PluginSdk.CodeyBoxPluginAttribute";
    internal const string RequiresToolAttributeFullName = "CodeyBox.PluginSdk.CodeyBoxPluginRequiresToolAttribute";

    /// <summary>
    /// Returns every <c>[CodeyBoxPlugin]</c>-decorated type declared in the
    /// assembly at <paramref name="assemblyPath"/>. Returns an empty list
    /// (with a log entry) when the file is not a readable managed assembly.
    /// Never throws.
    /// </summary>
    public static IReadOnlyList<PluginMetadataCandidate> Inspect(string assemblyPath, ILogger logger) =>
        InspectWithOutcome(assemblyPath, logger).Candidates;

    /// <summary>
    /// Metadata-only inspection that also reports <em>why</em> nothing was
    /// found. Returns the candidates (possibly empty), a short operator-facing
    /// error when inspection itself failed (null on success, including the
    /// "valid assembly with no plugin types" case), and whether the failure
    /// looks like a stale host-contracts build. Never throws and never loads
    /// the assembly for execution.
    /// </summary>
    public static PluginInspectionOutcome InspectWithOutcome(string assemblyPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(assemblyPath))
        {
            logger.LogWarning("Plugin assembly not found, skipping: {Path}", assemblyPath);
            return new PluginInspectionOutcome([], "file not found", IsStaleContracts: false);
        }

        try
        {
            using var context = CreateMetadataContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var candidates = new List<PluginMetadataCandidate>();
            foreach (var type in GetLoadableTypes(assembly, assemblyPath, logger))
            {
                var candidate = ReadCandidate(type);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
            return new PluginInspectionOutcome(candidates, Error: null, IsStaleContracts: false);
        }
        catch (Exception ex)
        {
            var stale = PluginContractStaleness.IsStaleContractFailure(ex);
            logger.LogError(ex, "Failed to inspect plugin assembly metadata: {Path}", assemblyPath);
            var error = stale
                ? $"assembly {PluginContractStaleness.OperatorMessage}"
                : (ex.GetType().Name + ": " + FirstLine(ex.Message));
            return new PluginInspectionOutcome([], error, stale);
        }
    }

    private static MetadataLoadContext CreateMetadataContext(string assemblyPath)
    {
        // The resolver only needs the assemblies required to understand the
        // plugin's attribute surface: the runtime core, the host's own
        // PluginSdk/Core (attribute type identity), plus every already-loaded
        // non-dynamic assembly so framework base types resolve. The plugin
        // file itself is added so its own dependencies in the same directory
        // can resolve if needed for type enumeration.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assemblyPath };
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!loaded.IsDynamic && !string.IsNullOrEmpty(loaded.Location))
                paths.Add(loaded.Location);
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                paths.Add(dll);
        }
        return new MetadataLoadContext(new PathAssemblyResolver(paths));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, string assemblyPath, ILogger logger)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            logger.LogWarning(
                ex,
                "Partially unreadable plugin assembly {Path}: {Loaded}/{Total} types readable",
                assemblyPath,
                ex.Types.Count(static t => t is not null),
                ex.Types.Length);
            return ex.Types.Where(static t => t is not null)!;
        }
        return types;
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(no detail)";
        var line = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int maxLength = 256;
        return line.Length > maxLength ? line[..maxLength] + "…" : line;
    }

    private static PluginMetadataCandidate? ReadCandidate(Type type)
    {
        // Mirror the old GetExportedTypes surface: only public plugin entry
        // points are discoverable. A non-public [CodeyBoxPlugin] class is
        // invisible, exactly as before.
        if (type.IsAbstract || !type.IsClass || (!type.IsPublic && !type.IsNestedPublic))
            return null;

        CustomAttributeData? pluginAttr = null;
        List<CustomAttributeData>? toolAttrs = null;
        foreach (var attr in CustomAttributeData.GetCustomAttributes(type))
        {
            var name = attr.AttributeType.FullName;
            if (name == PluginAttributeFullName)
                pluginAttr = attr;
            else if (name == RequiresToolAttributeFullName)
                (toolAttrs ??= []).Add(attr);
        }

        if (pluginAttr is null)
            return null;

        var args = pluginAttr.ConstructorArguments;
        if (args.Count < 2)
            return null;

        var id = args[0].Value as string;
        var displayName = args[1].Value as string;
        var minVersion = args.Count >= 3 ? args[2].Value as string : null;
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var tools = new List<PluginToolDeclaration>();
        if (toolAttrs is not null)
        {
            foreach (var toolAttr in toolAttrs)
                tools.Add(ReadToolDeclaration(toolAttr));
        }

        return new PluginMetadataCandidate(
            type.FullName ?? type.Name,
            id,
            string.IsNullOrWhiteSpace(displayName) ? id : displayName!,
            string.IsNullOrWhiteSpace(minVersion) ? "1.0" : minVersion!,
            tools);
    }

    private static PluginToolDeclaration ReadToolDeclaration(CustomAttributeData attr)
    {
        var binary = attr.ConstructorArguments.Count >= 1
            ? attr.ConstructorArguments[0].Value as string
            : null;
        string? aptPackage = null;
        string? installHint = null;
        foreach (var named in attr.NamedArguments)
        {
            if (named.MemberName == nameof(CodeyBox.PluginSdk.CodeyBoxPluginRequiresToolAttribute.AptPackage))
                aptPackage = named.TypedValue.Value as string;
            else if (named.MemberName == nameof(CodeyBox.PluginSdk.CodeyBoxPluginRequiresToolAttribute.InstallHint))
                installHint = named.TypedValue.Value as string;
        }
        return new PluginToolDeclaration(binary, aptPackage, installHint);
    }
}
