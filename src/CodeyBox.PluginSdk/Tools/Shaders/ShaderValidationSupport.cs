using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Shared pure core for the shader-validation auditor family: stage and
/// target-environment validation plus resolution of operator-configured
/// <c>path[:stage]</c> target entries into <see cref="ShaderTargetSet"/>.
/// Every backend in the family (glslang today, SPIR-V tooling as a later
/// dependent) resolves through this seam so stage spelling, extension
/// inference, and the single-stage/mixed-stage invocation rule cannot drift
/// between backends. All members are pure functions of their inputs.
/// </summary>
public static class ShaderValidationSupport
{
    /// <summary>Target environment passed when the operator configures none. Matches the tool default.</summary>
    public const string DefaultTargetEnvironment = "vulkan1.0";

    /// <summary>Upper bound on configured targets per audit. Explicit file lists are small by construction.</summary>
    public const int MaxTargets = 128;

    /// <summary>Maximum characters for a single target entry (mirrors the shared argv-value bound).</summary>
    public const int MaxTargetEntryChars = 1024;

    /// <summary>Maximum characters for a target-environment value.</summary>
    public const int MaxTargetEnvironmentChars = 64;

    /// <summary>
    /// Canonical shader stage names accepted in <c>path:stage</c> suffixes
    /// and the shared stage default — the <c>-S</c> vocabulary of the
    /// glslang-family validators. Compared case-insensitively; the resolved
    /// stage is always the lowercase canonical spelling.
    /// </summary>
    public static readonly IReadOnlySet<string> Stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vert", "tesc", "tese", "geom", "frag", "comp",
        "mesh", "task",
        "rgen", "rint", "rahit", "rchit", "rmiss", "rcall",
    };

    // Extension → stage inference: the per-file stage mechanism every
    // validator in this family shares. `.glsl` carries no stage information
    // (it needs an explicit suffix or the configured default); `.conf` is a
    // validator configuration file, never a shader, and is rejected with a
    // dedicated message because a repository-authored config would silently
    // reprogram the validation.
    private static readonly IReadOnlyDictionary<string, string> ExtensionStages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".vert"] = "vert",
            [".tesc"] = "tesc",
            [".tese"] = "tese",
            [".geom"] = "geom",
            [".frag"] = "frag",
            [".comp"] = "comp",
            [".mesh"] = "mesh",
            [".task"] = "task",
            [".rgen"] = "rgen",
            [".rint"] = "rint",
            [".rahit"] = "rahit",
            [".rchit"] = "rchit",
            [".rmiss"] = "rmiss",
            [".rcall"] = "rcall",
        };

    private static readonly IReadOnlySet<string> StagelessShaderExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".glsl" };

    /// <summary>Stage inferred from a path's extension, or null when the extension carries no stage.</summary>
    public static string? StageForPath(string path)
    {
        var extension = PathExtension(path);
        return extension is not null && ExtensionStages.TryGetValue(extension, out var stage)
            ? stage
            : null;
    }

    /// <summary>Validates an explicitly configured stage value; returns the canonical lowercase spelling.</summary>
    public static string ValidateStage(string value, string source)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (Stages.Contains(trimmed))
            return trimmed.ToLowerInvariant();
        throw new AuditUnavailableException(
            $"could-not-verify: configured '{source}' stage ('{Truncate(trimmed)}') is not a known shader "
            + $"stage — expected one of: {string.Join(", ", Stages.OrderBy(static s => s, StringComparer.Ordinal))}.")
        { IsDeterministic = true };
    }

    /// <summary>
    /// Validates an explicitly configured target-environment value (passed to
    /// the backend's <c>--target-env</c> verbatim as its own argv entry).
    /// Blank means "unset" and yields <paramref name="defaultEnvironment"/>.
    /// Values are bounded plain tokens (letters, digits, <c>.</c>, <c>_</c>,
    /// <c>-</c>, leading alphanumeric): a typo surfaces as a tool error and
    /// fails closed downstream, but a flag-shaped or unbounded value never
    /// reaches argv.
    /// </summary>
    public static string ValidateTargetEnvironment(string? value, string defaultEnvironment, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultEnvironment;
        var trimmed = value.Trim();
        if (trimmed.Length <= MaxTargetEnvironmentChars
            && char.IsAsciiLetterOrDigit(trimmed[0])
            && trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return trimmed;
        throw new AuditUnavailableException(
            $"could-not-verify: configured '{source}' target environment ('{Truncate(trimmed)}') is not a "
            + "usable environment name — use a bounded token such as 'vulkan1.0', 'vulkan1.3' or 'opengl'.")
        { IsDeterministic = true };
    }

    /// <summary>
    /// Resolves raw operator-configured target entries (<c>path</c> or
    /// <c>path:stage</c>) into a validated <see cref="ShaderTargetSet"/>.
    /// Per-target stage resolution is explicit-suffix first, canonical stage
    /// extension second, <paramref name="defaultStage"/> last; a target with
    /// no stage from any source is a deterministic configuration error, as
    /// is an empty set (an operand-less run would validate whatever the tool
    /// finds by itself, which these auditors never do). A single invocation
    /// carries at most one stage flag, so a mixed-stage set is only accepted
    /// when every target carries a canonical stage extension — the per-file
    /// stage mechanism the invocation can express.
    /// </summary>
    public static ShaderTargetSet ResolveTargets(
        IReadOnlyList<string> entries,
        string? defaultStage,
        string pluginId,
        string targetsKey,
        string defaultStageKey)
    {
        var configuredDefault = string.IsNullOrWhiteSpace(defaultStage)
            ? null
            : ValidateStage(defaultStage, $"{pluginId}:{defaultStageKey}");

        var targets = new List<ShaderTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var target = ResolveEntry(entry, configuredDefault, pluginId, targetsKey, defaultStageKey);
            if (seen.Add(target.Path))
                targets.Add(target);
        }

        if (targets.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{pluginId}' has no usable '{targetsKey}' — configure at least "
                + $"one repo-relative shader file under CodeyBox:Plugins:{pluginId}:{targetsKey}. An "
                + "operand-less run would validate whatever the tool finds by itself, which this auditor "
                + "never does.")
            { IsDeterministic = true };
        if (targets.Count > MaxTargets)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{pluginId}' was configured with {targets.Count} '{targetsKey}' "
                + $"entries, more than the {MaxTargets} one validation invocation holds. Narrow the set to "
                + "the shaders under audit.")
            { IsDeterministic = true };

        var distinctStages = targets.Select(static t => t.Stage).Distinct(StringComparer.Ordinal).ToList();
        if (distinctStages.Count > 1)
        {
            var withoutExtension = targets
                .Where(static t => !t.StageFromExtension)
                .Select(static t => t.Path)
                .Take(5)
                .ToList();
            if (withoutExtension.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{pluginId}' '{targetsKey}' resolves to multiple shader "
                    + $"stages ({string.Join(", ", distinctStages.OrderBy(static s => s, StringComparer.Ordinal))}), "
                    + "but a single validation invocation carries one stage flag: every target in a "
                    + "mixed-stage set must carry a canonical stage extension (.vert, .frag, .comp and "
                    + "friends). These do not: "
                    + $"'{Truncate(string.Join("', '", withoutExtension))}'. Give them stage extensions, "
                    + "or configure targets that share one stage.")
                { IsDeterministic = true };
        }

        return new ShaderTargetSet(
            targets,
            distinctStages.Count == 1 ? distinctStages[0] : null);
    }

    private static ShaderTarget ResolveEntry(
        string entry,
        string? configuredDefault,
        string pluginId,
        string targetsKey,
        string defaultStageKey)
    {
        var source = $"{pluginId}:{targetsKey}";
        var trimmed = (entry ?? string.Empty).Trim();
        if (trimmed.Length == 0
            || trimmed.Length > MaxTargetEntryChars
            || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(trimmed)}') is not a usable "
                + "target value (empty, overlong, or contains control characters).")
            { IsDeterministic = true };

        // The `:stage` suffix splits on the last colon, and only when the
        // tail is exactly a known stage name — a path that merely contains a
        // colon (legal on Unix) keeps its full spelling.
        string pathText = trimmed;
        string? suffixStage = null;
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1)
        {
            var tail = trimmed[(colon + 1)..];
            if (Stages.Contains(tail))
            {
                pathText = trimmed[..colon].Trim();
                suffixStage = tail.ToLowerInvariant();
            }
        }

        var path = ValidatedTargetPath(pathText, source);
        var extension = PathExtension(path);
        if (string.Equals(extension, ".conf", StringComparison.OrdinalIgnoreCase))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(path)}') names a validator "
                + "configuration file (.conf), not a shader. Repository-authored tool configuration is "
                + "never passed to the validator — configure the shader files instead.")
            { IsDeterministic = true };
        var knownExtension = extension is not null
            && (ExtensionStages.ContainsKey(extension) || StagelessShaderExtensions.Contains(extension));
        if (!knownExtension)
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(path)}') must name a standalone "
                + "GLSL/ESSL file (.vert, .tesc, .tese, .geom, .frag, .comp, .mesh, .task, ray-tracing "
                + "stages, or .glsl with an explicit stage). Directories, HLSL, and Unity ShaderLab sources "
                + "are outside this auditor's scope.")
            { IsDeterministic = true };

        string? stage = suffixStage ?? (extension is not null ? StageForExtensionValue(extension) : null);
        var fromExtension = suffixStage is null && stage is not null;
        stage ??= configuredDefault;
        if (stage is null)
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(path)}') resolves to no shader "
                + $"stage — give it an explicit ':stage' suffix or set CodeyBox:Plugins:{pluginId}:"
                + $"{defaultStageKey} (e.g. 'frag' for .glsl files, which carry no stage in their name).")
            { IsDeterministic = true };

        return new ShaderTarget(path, stage, fromExtension);
    }

    private static string? StageForExtensionValue(string extension)
        => ExtensionStages.TryGetValue(extension, out var stage) ? stage : null;

    // Containment for a scan-target entry that must stay inside the audited
    // worktree: the argv guard (bounded, no leading parameter dash, no
    // controls) plus containment — a rooted path or a `..` segment would
    // point the tool outside the tree and produce report paths the
    // repo-relative finding contract cannot express.
    private static string ValidatedTargetPath(string value, string source)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0
            || trimmed.Length > MaxTargetEntryChars
            || IsParameterDash(trimmed[0])
            || trimmed.Any(char.IsControl))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(trimmed)}') is not a usable "
                + "repo-relative path (empty, overlong, has a leading parameter dash, or contains control "
                + "characters).")
            { IsDeterministic = true };

        var normalized = trimmed.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{Truncate(trimmed)}') must be a "
                + "repo-relative path inside the worktree.")
            { IsDeterministic = true };
        return normalized;
    }

    private static string? PathExtension(string path)
    {
        var name = path.Split('/').LastOrDefault() ?? string.Empty;
        var dot = name.LastIndexOf('.');
        return dot < 0 ? null : name[dot..];
    }

    private static bool IsParameterDash(char c)
        => c is '-' or '\u2013' or '\u2014' or '\u2015';

    private static string Truncate(string value)
        => value.Length <= ToolOutputText.MessageValueMaxChars
            ? value
            : value[..ToolOutputText.MessageValueMaxChars];
}
