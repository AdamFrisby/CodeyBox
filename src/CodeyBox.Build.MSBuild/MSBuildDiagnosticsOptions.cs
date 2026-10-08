using System.Text.RegularExpressions;

namespace CodeyBox.Build.MSBuild;

/// <summary>
/// Hot-reloadable operator knobs for MSBuild binary-log diagnostics
/// enrichment. Disabled by default: no binlog is captured, parsed, or
/// attached until the operator opts in. All bounds are enforced before
/// buffering (at capture and again at the parse sink) so unbounded toolchain
/// output cannot exhaust the host.
/// </summary>
public sealed record MSBuildDiagnosticsOptions
{
    public const string SectionName = "CodeyBox:MSBuildDiagnostics";

    /// <summary>Stable provider id this adapter registers under.</summary>
    public const string ProviderId = "msbuild-binlog";

    /// <summary>Exact payload format id accepted by the producer. Matched by exact equality.</summary>
    public const string PayloadFormat = "msbuild-binlog/v1";

    /// <summary>
    /// Highest file-format version verified readable by the pinned
    /// MSBuild.StructuredLogger 2.3.246 build (the .NET 10 SDK toolchain
    /// emits v26; verified by round-trip test). Upper-bounds accepted logs
    /// so a newer writer is never silently misread; re-verify and bump
    /// together with the package pin.
    /// </summary>
    public const int PinnedFileFormatVersion = 26;

    /// <summary>Master switch. Default false: the adapter captures nothing.</summary>
    public bool Enabled { get; init; }

    /// <summary>Build configuration passed to the opted-in build (exact, allowlisted).</summary>
    public string BuildConfiguration { get; init; } = "Debug";

    /// <summary>Maximum accepted binlog bytes per file, enforced before buffering.</summary>
    public long MaxBinlogBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum decompressed-to-compressed ratio accepted during the gzip
    /// integrity pre-check (decompression-bomb guard). The pre-check streams
    /// without retaining output; exceeding the ratio yields
    /// insufficient-diagnostics.
    /// </summary>
    public int MaxDecompressionRatio { get; init; } = 32;

    /// <summary>Maximum binlog files collected from one build attempt.</summary>
    public int MaxBinlogsPerBuild { get; init; } = 4;

    /// <summary>Maximum diagnostics retained in one evidence value.</summary>
    public int MaxDiagnostics { get; init; } = 64;

    /// <summary>Maximum characters retained per diagnostic message (after redaction).</summary>
    public int MaxDiagnosticMessageChars { get; init; } = 2048;

    /// <summary>Maximum tree nodes visited while extracting diagnostics.</summary>
    public int MaxNodesVisited { get; init; } = 200_000;

    /// <summary>Maximum tree depth descended while extracting diagnostics.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Wall-clock budget for one binlog parse.</summary>
    public TimeSpan ParseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum accepted binlog file-format version.</summary>
    public int MinSupportedFileFormatVersion { get; init; } = 9;

    /// <summary>Maximum accepted binlog file-format version (tracks the package pin).</summary>
    public int MaxSupportedFileFormatVersion { get; init; } = PinnedFileFormatVersion;

    private static readonly Regex ConfigurationPattern = new(
        "^[A-Za-z0-9_.-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidConfiguration(string? configuration) =>
        !string.IsNullOrEmpty(configuration) && ConfigurationPattern.IsMatch(configuration);

    public bool IsSupportedVersion(int fileFormatVersion) =>
        fileFormatVersion >= MinSupportedFileFormatVersion
        && fileFormatVersion <= MaxSupportedFileFormatVersion;

    /// <summary>
    /// Fail-fast structural validation for operator configuration. Positive
    /// <see cref="ParseTimeout"/> is required here; a non-positive value at
    /// runtime still yields insufficient-diagnostics rather than a hang.
    /// </summary>
    public static bool IsValid(MSBuildDiagnosticsOptions? options) =>
        options is not null
        && IsValidConfiguration(options.BuildConfiguration)
        && options.MaxBinlogBytes > 0
        && options.MaxBinlogBytes <= 256 * 1024 * 1024
        && options.MaxDecompressionRatio is >= 4 and <= 256
        && options.MaxBinlogsPerBuild is >= 1 and <= 32
        && options.MaxDiagnostics is >= 1 and <= 1024
        && options.MaxDiagnosticMessageChars is >= 64 and <= 32768
        && options.MaxNodesVisited is >= 1000 and <= 10_000_000
        && options.MaxDepth is >= 4 and <= 1024
        && options.ParseTimeout > TimeSpan.Zero
        && options.ParseTimeout <= TimeSpan.FromMinutes(10)
        && options.MinSupportedFileFormatVersion >= 1
        && options.MaxSupportedFileFormatVersion >= options.MinSupportedFileFormatVersion;
}
