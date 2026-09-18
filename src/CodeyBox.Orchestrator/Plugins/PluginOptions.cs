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

    /// <summary>
    /// Plugin IDs explicitly switched on by the operator. A plugin loads only
    /// when it is both allowlisted <em>and</em> enabled; an
    /// allowlisted-but-disabled plugin stays unloaded — its assembly is never
    /// loaded, its types are never registered, its instances are never
    /// constructed. Use <c>["*"]</c> to enable all (discouraged in production:
    /// every future plugin would switch itself on by being present).
    ///
    /// <para>Default: the four bundled plugins that predate this switch
    /// (<c>codeybox.file-size-limits</c>, <c>codeybox.statistics</c>,
    /// <c>codeybox.quota-reset-notifier</c>, <c>codeybox.opencode-go-quota</c>)
    /// so deployments that allowlisted them keep working with no config
    /// change. Every other ID — including the bundled
    /// <c>codeybox.dotnet-test-runner</c> / <c>codeybox.pytest-test-runner</c>
    /// entries and any plugin added later — is disabled until the operator
    /// names it here. Explicitly configuring an empty list disables
    /// everything, including the four defaults.</para>
    ///
    /// <para>Changing this set requires a host restart (see
    /// <c>PluginOptionsChangeWatcher</c>): unloading a live plugin is not
    /// safe, so a runtime change is reported and ignored until restart.</para>
    /// </summary>
    public List<string> Enabled { get; set; } = [.. DefaultEnabledPluginIds];

    /// <summary>
    /// Plugin IDs enabled by default. These are exactly the bundled plugins
    /// that existed before the enablement switch was introduced, kept on so
    /// current deployments observe no behaviour change beyond the new switch
    /// itself. New catalogue plugins must be opted into explicitly.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultEnabledPluginIds =
    [
        "codeybox.file-size-limits",
        "codeybox.statistics",
        "codeybox.quota-reset-notifier",
        "codeybox.opencode-go-quota",
    ];

    /// <summary>
    /// Whether <paramref name="pluginId"/> is switched on. An empty
    /// <see cref="Enabled"/> list (explicitly configured) enables nothing;
    /// <c>"*"</c> enables everything. Unknown IDs are off unless named.
    /// </summary>
    public bool IsEnabled(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return false;
        if (Enabled.Contains("*", StringComparer.OrdinalIgnoreCase))
            return true;
        return Enabled.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
    }
}
