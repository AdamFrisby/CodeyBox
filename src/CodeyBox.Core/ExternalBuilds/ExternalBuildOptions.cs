namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// Hot-reloadable operator knobs for the provider-neutral external-build
/// framework. Default OFF: nothing dispatches to any provider until the
/// operator explicitly enables the feature and approves at least one target.
/// All values are plain operational data (counts, seconds, bytes); vendor or
/// toolchain specifics live in adapter configuration, never here.
/// </summary>
public sealed class ExternalBuildOptions
{
    public const string SectionName = "CodeyBox:ExternalBuilds";

    /// <summary>Master switch. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Operator-approved build targets, keyed by stable target id
    /// (e.g. <c>unity-android-il2cpp</c>). Sandbox callers may select only
    /// these ids; arbitrary provider endpoints or host commands are rejected.
    /// Empty by default.
    /// </summary>
    public Dictionary<string, ExternalBuildTargetApproval> ApprovedTargets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Max concurrently dispatched provider runs per project. Default 2.</summary>
    public int MaxConcurrentPerProject { get; set; } = 2;

    /// <summary>Max concurrently dispatched provider runs per provider id. Default 4.</summary>
    public int MaxConcurrentPerProvider { get; set; } = 4;

    /// <summary>Max queued (non-terminal) builds per project. Default 20.</summary>
    public int MaxQueuedPerProject { get; set; } = 20;

    /// <summary>Per-build wall-clock deadline in seconds. Default 7200 (2h).</summary>
    public int BuildDeadlineSeconds { get; set; } = 7200;

    /// <summary>Poll interval in seconds. Default 15.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Max poll attempts before marking reconciliation-impossible. Default 480.</summary>
    public int MaxPollAttempts { get; set; } = 480;

    /// <summary>Max dispatch attempts (initial submit + reconciled retries). Default 3.</summary>
    public int MaxDispatchAttempts { get; set; } = 3;

    /// <summary>Max callback age in seconds. Default 600.</summary>
    public int MaxCallbackAgeSeconds { get; set; } = 600;

    /// <summary>History samples used by the park predictor. Default 8.</summary>
    public int HistorySampleSize { get; set; } = 8;

    /// <summary>Minimum comparable samples before a prediction is trusted. Default 3.</summary>
    public int MinSamplesForPrediction { get; set; } = 3;

    /// <summary>Estimator: <c>Median</c> (default) or <c>Mean</c>.</summary>
    public string Estimator { get; set; } = nameof(ParkEstimator.Median);

    /// <summary>Max snapshot bytes accepted from a work tree. Default 256 MiB.</summary>
    public long MaxSnapshotBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Max files captured in one snapshot. Default 50,000.</summary>
    public int MaxSnapshotFiles { get; set; } = 50_000;

    /// <summary>Max single-file bytes in a snapshot. Default 64 MiB.</summary>
    public long MaxSnapshotFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Max artifact bytes ingested per artifact. Default 512 MiB.</summary>
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Max artifacts ingested per build. Default 32.</summary>
    public int MaxArtifactsPerBuild { get; set; } = 32;

    /// <summary>Max decompressed bytes per archive artifact. Default 2 GiB.</summary>
    public long MaxDecompressedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Max archive entries scanned per archive. Default 20,000.</summary>
    public int MaxArchiveEntries { get; set; } = 20_000;

    /// <summary>Max diagnostics chars returned to a sandbox caller. Default 32 KiB.</summary>
    public int MaxDiagnosticsChars { get; set; } = 32 * 1024;

    /// <summary>Retention days for terminal build records. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Scoped sandbox capability lifetime in seconds. Default 3600.</summary>
    public int CapabilityLifetimeSeconds { get; set; } = 3600;

    public static bool IsValid(ExternalBuildOptions opts) =>
        opts.MaxConcurrentPerProject is >= 1 and <= 32
        && opts.MaxConcurrentPerProvider is >= 1 and <= 64
        && opts.MaxQueuedPerProject is >= 1 and <= 500
        && opts.BuildDeadlineSeconds is >= 60 and <= 86400
        && opts.PollIntervalSeconds is >= 1 and <= 600
        && opts.MaxPollAttempts is >= 1 and <= 100_000
        && opts.MaxDispatchAttempts is >= 1 and <= 10
        && opts.MaxCallbackAgeSeconds is >= 10 and <= 86400
        && opts.HistorySampleSize is >= 1 and <= 100
        && opts.MinSamplesForPrediction is >= 1 and <= 100
        && opts.MinSamplesForPrediction <= opts.HistorySampleSize
        && Enum.TryParse<ParkEstimator>(opts.Estimator, ignoreCase: false, out _)
        && opts.MaxSnapshotBytes is >= 1024 and <= 4L * 1024 * 1024 * 1024
        && opts.MaxSnapshotFiles is >= 1 and <= 500_000
        && opts.MaxSnapshotFileBytes is >= 1024 and <= 1024L * 1024 * 1024
        && opts.MaxArtifactBytes is >= 1024 and <= 8L * 1024 * 1024 * 1024
        && opts.MaxArtifactsPerBuild is >= 1 and <= 256
        && opts.MaxDecompressedBytes is >= 1024
        && opts.MaxArchiveEntries is >= 1 and <= 500_000
        && opts.MaxDiagnosticsChars is >= 256 and <= 1024 * 1024
        && opts.RetentionDays is >= 1 and <= 365
        && opts.CapabilityLifetimeSeconds is >= 60 and <= 86400;
}

/// <summary>
/// Operator approval for one sandbox-selectable build target. Neutral:
/// provider id, target id, configuration profile and opaque adapter
/// parameters. No language/ecosystem-specific fields.
/// </summary>
public sealed class ExternalBuildTargetApproval
{
    /// <summary>Provider id the adapter registers (e.g. <c>fake-git</c>).</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Provider-side target/workflow identifier.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Configuration profile name (e.g. <c>release</c>).</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>Opaque adapter parameters; never interpreted by Core.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether this target accepts a Git-published candidate ref.</summary>
    public bool AllowGitPublication { get; set; }

    /// <summary>Whether this target accepts an uploaded source snapshot.</summary>
    public bool AllowSnapshotUpload { get; set; } = true;
}

public enum ParkEstimator
{
    Median = 0,
    Mean = 1,
}
