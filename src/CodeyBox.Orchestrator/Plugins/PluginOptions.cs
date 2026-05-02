namespace CodeyBox.Orchestrator;

/// <summary>
/// Plugin discovery configuration. Bind from <c>CodeyBox:Plugins</c>.
/// </summary>
public sealed class PluginOptions
{
    /// <summary>
    /// Absolute paths to individual plugin assembly files.
    /// Example: <c>["/etc/codeybox/plugins/MyOrg.CustomAuditor.dll"]</c>.
    /// </summary>
    public List<string> AssemblyPaths { get; set; } = [];

    /// <summary>
    /// Directories scanned for <c>*.dll</c> files (non-recursive).
    /// Example: <c>["/etc/codeybox/plugins"]</c>.
    /// </summary>
    public List<string> PackageDirectories { get; set; } = [];

    /// <summary>
    /// Plugin IDs allowed to load. When non-empty, only IDs in this list are
    /// accepted; others are logged and skipped. Use <c>["*"]</c> to allow all
    /// (discouraged in production). An empty list means no plugins load.
    /// </summary>
    public List<string> Allowlist { get; set; } = [];
}
