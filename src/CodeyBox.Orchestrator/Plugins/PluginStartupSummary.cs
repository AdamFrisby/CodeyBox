namespace CodeyBox.Orchestrator;

/// <summary>
/// Builds the bounded plugin startup summary: a readable one-header-plus-
/// capped-details report of what discovery found, what loaded, what was
/// skipped and why. Pure function over immutable inputs so it is easy to test.
///
/// <para>Bounded by design: with dozens of plugins configured this stays a
/// short summary, never a wall of text. Detail beyond
/// <paramref name="maxEntries"/> collapses to counts; the full per-plugin and
/// per-path inventory remains available on <c>GET /plugins/status</c>.</para>
/// </summary>
internal static class PluginStartupSummary
{
    public sealed record Summary(
        string Header,
        IReadOnlyList<string> Details,
        int Loaded,
        int Skipped,
        int AssemblyFailures,
        int InitializationFailures,
        int Omitted);

    public static Summary Build(
        IReadOnlyList<PluginDiscoveryStatus> statuses,
        IReadOnlyList<PluginAssemblyReport> assemblies,
        IReadOnlyList<PluginInitializationFailure> initializationFailures,
        int maxEntries)
    {
        var cap = Math.Max(1, maxEntries);
        var loaded = statuses.Count(static s => s.Loaded);
        var skipped = statuses.Count(static s => !s.Loaded);
        var assemblyFailures = assemblies.Count(static a => !a.Loaded);
        var initFailures = initializationFailures.Count;

        var header =
            $"Plugin startup: {loaded} loaded, {skipped} skipped, " +
            $"{assemblyFailures} assembly issue(s), {initFailures} initialization failure(s)";

        var details = new List<string>();
        foreach (var status in statuses.OrderBy(static s => s.PluginId, StringComparer.Ordinal))
        {
            if (status.Loaded)
            {
                var contracts = status.Contracts is { Count: > 0 }
                    ? $" [{string.Join(", ", status.Contracts)}]"
                    : string.Empty;
                details.Add($"loaded {status.PluginId}{contracts} from {status.AssemblyPath}");
            }
            else
            {
                details.Add($"skipped {status.PluginId}: {Describe(status.SkipReason)}");
            }
        }

        foreach (var assembly in assemblies
                     .Where(static a => !a.Loaded)
                     .OrderBy(static a => a.AssemblyPath, StringComparer.Ordinal))
        {
            details.Add($"assembly {assembly.AssemblyPath}: {Describe(assembly.SkipReason)}" +
                (string.IsNullOrEmpty(assembly.Detail) ? string.Empty : $" ({assembly.Detail})"));
        }

        foreach (var failure in initializationFailures
                     .OrderBy(static f => f.PluginId, StringComparer.Ordinal))
        {
            details.Add($"initialization failed {failure.PluginId} ({failure.TypeName}): {failure.Error}");
        }

        var omitted = Math.Max(0, details.Count - cap);
        var shown = details.Take(cap).ToList();
        return new Summary(header, shown, loaded, skipped, assemblyFailures, initFailures, omitted);
    }

    private static string Describe(PluginSkipReason reason) => reason switch
    {
        PluginSkipReason.None => "loaded",
        PluginSkipReason.Disabled => "disabled (not in Plugins:Enabled)",
        PluginSkipReason.NotAllowlisted => "not in Plugins:Allowlist",
        PluginSkipReason.ApiVersionMismatch => "requires a newer host API version",
        PluginSkipReason.InvalidToolDeclaration => "invalid external-tool declaration (failed closed)",
        PluginSkipReason.FileMissing => "configured file not found",
        PluginSkipReason.InspectionFailed => "assembly metadata could not be inspected",
        PluginSkipReason.LoadFailed => "assembly could not be loaded for execution",
        PluginSkipReason.NoPluginEntry => "no [CodeyBoxPlugin] types found",
        PluginSkipReason.StaleHostContracts =>
            "built against a different version of the host contracts (CodeyBox.Core/CodeyBox.PluginSdk); rebuild the plugin",
        PluginSkipReason.InitializationFailed => "initialization threw",
        _ => "unknown reason",
    };
}

/// <summary>
/// One plugin type whose <c>IPluginInitializer</c> (or DI resolution for it)
/// threw during host startup. Recorded rather than only logged so the
/// administrative surface can answer "is my plugin actually running".
/// </summary>
public sealed record PluginInitializationFailure(
    string PluginId,
    string TypeName,
    string Error);
