using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// One validated candidate SPIR-V module: a repository-relative binary path
/// as it travels to the tool argv (forward slashes, no leading <c>./</c>,
/// contained in the worktree, carrying the <c>.spv</c> extension). Produced
/// by <see cref="SpirvValidationSupport.ResolveModules"/> — construct
/// through that seam so containment, the binary-only extension gate, and
/// the explicit-selection rule stay in one place for every consumer.
/// Binaries encode their own entry points, so modules carry no stage: there
/// is no per-file stage to resolve and no stage flag to pass.
/// </summary>
/// <param name="Modules">
/// Validated modules in operator configuration order (duplicates removed).
/// Each module is validated with its own tool invocation.
/// </param>
public sealed record SpirvModuleSet(IReadOnlyList<string> Modules);

/// <summary>
/// Shared pure core for SPIR-V module validation: explicit selection and
/// containment of candidate <c>.spv</c> binaries plus extraction of the
/// two-component <c>vYYYY.N</c> SPIRV-Tools release token. Every SPIR-V
/// consumer resolves through this seam so the binary-only gate and the
/// never-discover rule cannot drift between callers. All members are pure
/// functions of their inputs.
/// </summary>
public static class SpirvValidationSupport
{
    /// <summary>Module-file extension gate: only assembled SPIR-V binaries validate here.</summary>
    public const string SpirvModuleExtension = ".spv";

    /// <summary>
    /// SPIR-V magic number (<c>0x07230203</c>) as the lowercase hex words an
    /// <c>od -A n -t x1</c> probe prints on little-endian hosts, used by the
    /// auditor's pre-scan module check. Kept here so the probe expectation
    /// and its documentation share one spelling.
    /// </summary>
    public const string SpirvMagicProbeWords = "03 02 23 07";

    private static readonly System.Text.RegularExpressions.Regex SpirvToolsVersionPattern = new(
        @"(?<!\d)\d{4}\.\d+(?!\d)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Extracts the SPIRV-Tools release token from tool version output. The
    /// project versions releases as <c>vYYYY.N</c> (two components, e.g.
    /// <c>v2025.1</c> in <c>SPIRV-Tools v2025.1 unknown hash, …</c>), which
    /// the shared three-component extraction cannot represent — so this
    /// family seam owns the scheme explicitly. Returns null when no release
    /// token is present.
    /// </summary>
    public static string? ExtractSpirvToolsVersion(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        var match = SpirvToolsVersionPattern.Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Containment sink for every module path that reaches the tool argv.
    /// Beyond the shared repo-relative/argv guard, an entry must name a
    /// <c>.spv</c> binary: GLSL/ESSL sources belong to the source validators
    /// in this family, and directories, HLSL, ShaderLab, and tool
    /// configuration are outside binary-module validation. Returns the
    /// canonical bare repo-relative form.
    /// </summary>
    public static string ContainModulePath(string value, string source)
    {
        var contained = ShaderValidationSupport.ContainRepoRelativePath(value, source);
        if (contained.EndsWith(SpirvModuleExtension, StringComparison.OrdinalIgnoreCase))
            return contained;
        throw new AuditUnavailableException(
            $"could-not-verify: configured '{source}' entry ('{Truncate(contained)}') must name an "
            + "assembled SPIR-V binary (.spv). GLSL/ESSL sources (.vert, .frag, .glsl and friends) "
            + "belong to the source-shader validators; directories, HLSL, ShaderLab, and tool "
            + "configuration are outside binary-module validation.")
        { IsDeterministic = true };
    }

    /// <summary>
    /// Resolves raw operator-configured module entries into a validated
    /// <see cref="SpirvModuleSet"/>. Selection is explicit only: the auditor
    /// never walks the tree for binaries (an extension walk would sweep
    /// vendored and dependency blobs the change under audit did not touch),
    /// so an empty set is a deterministic configuration error — and an
    /// operand-less <c>spirv-val</c> run would read its module from standard
    /// input, which the auditor never does.
    /// </summary>
    public static SpirvModuleSet ResolveModules(
        IReadOnlyList<string> entries,
        string pluginId,
        string targetsKey)
    {
        var modules = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var module = ContainModulePath(entry.Trim(), $"{pluginId}:{targetsKey}");
            if (seen.Add(module))
                modules.Add(module);
        }

        if (modules.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{pluginId}' has no usable '{targetsKey}' — configure at least "
                + $"one repo-relative SPIR-V module under CodeyBox:Plugins:{pluginId}:{targetsKey}. An "
                + "operand-less run would read its module from standard input, which this auditor "
                + "never does.")
            { IsDeterministic = true };
        if (modules.Count > ShaderValidationSupport.MaxTargets)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{pluginId}' was configured with {modules.Count} '{targetsKey}' "
                + $"entries, more than the {ShaderValidationSupport.MaxTargets} one audit validates. Narrow "
                + "the set to the modules under audit.")
            { IsDeterministic = true };

        return new SpirvModuleSet(modules);
    }

    private static string Truncate(string value)
        => value.Length <= ToolOutputText.MessageValueMaxChars
            ? value
            : value[..ToolOutputText.MessageValueMaxChars];
}
