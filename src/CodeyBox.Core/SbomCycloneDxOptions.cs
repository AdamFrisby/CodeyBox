namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable operator knobs for producer-neutral CycloneDX SBOM evidence.
/// Disabled by default: nothing is imported, generated, or compared until the
/// operator explicitly enables the feature and approves a baseline.
/// All values are plain operational data (sizes, versions, timeouts) — never
/// repository-controlled — and are read through a <c>Func</c> accessor so
/// edits apply without a host restart.
/// </summary>
public sealed class SbomCycloneDxOptions
{
    /// <summary>Scoped-config section name.</summary>
    public const string SectionName = "CodeyBox:SbomCycloneDx";

    /// <summary>Master switch. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum accepted SBOM document bytes (pre-parse bound, enforced before buffering). Default 5 MiB.</summary>
    public int MaxSbomBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Maximum components accepted in one document. Default 20,000.</summary>
    public int MaxComponents { get; set; } = 20_000;

    /// <summary>Maximum dependency edges accepted in one document. Default 60,000.</summary>
    public int MaxDependencies { get; set; } = 60_000;

    /// <summary>Supported CycloneDX spec versions. Default 1.4, 1.5, 1.6.</summary>
    public List<string> SupportedSpecVersions { get; set; } = ["1.4", "1.5", "1.6"];

    /// <summary>Supported document formats. Default json and xml.</summary>
    public List<string> SupportedFormats { get; set; } = ["json", "xml"];

    /// <summary>Per-operation wall-clock bound in seconds (import + validate + diff). Default 120.</summary>
    public int OperationTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Policy applied to baseline diffs. One of <c>FailOnAnyChange</c> (default),
    /// <c>FailOnAddedOrVersionChanged</c>, <c>AdvisoryOnly</c>.
    /// </summary>
    public string PolicyMode { get; set; } = nameof(SbomPolicyMode.FailOnAnyChange);

    /// <summary>
    /// Explicitly selected generator adapter ids (for example <c>cdxgen</c>).
    /// Empty means no generation is attempted: existing evidence is reused or the
    /// run is reported unavailable. Never defaults to a generator.
    /// </summary>
    public List<string> AllowedGenerators { get; set; } = [];

    /// <summary>
    /// Optional trusted validator tooling (for example <c>cyclonedx-cli</c>).
    /// Empty means the built-in bounded validator is the only validator.
    /// </summary>
    public string ValidatorTool { get; set; } = string.Empty;

    /// <summary>Expected version of the optional generator/validator tooling. Empty means unpinned (not recommended).</summary>
    public string ExpectedToolVersion { get; set; } = string.Empty;

    /// <summary>Validates ranges; pure and side-effect free.</summary>
    public static bool IsValid(SbomCycloneDxOptions opts) =>
        opts.MaxSbomBytes is >= 1024 and <= 100 * 1024 * 1024
        && opts.MaxComponents is >= 1 and <= 200_000
        && opts.MaxDependencies is >= 1 and <= 600_000
        && opts.OperationTimeoutSeconds is >= 5 and <= 3600
        && opts.SupportedSpecVersions.Count > 0
        && opts.SupportedFormats.Count > 0
        && Enum.TryParse<SbomPolicyMode>(opts.PolicyMode, ignoreCase: false, out _);
}
