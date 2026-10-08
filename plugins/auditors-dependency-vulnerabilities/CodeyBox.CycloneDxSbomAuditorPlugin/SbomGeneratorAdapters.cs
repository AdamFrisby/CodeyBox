using CodeyBox.Core;

namespace CodeyBox.CycloneDxSbomAuditorPlugin;

/// <summary>
/// Neutral generator-adapter contract: an explicitly selected, version-pinned
/// SBOM producer invoked inside the audit sandbox. Implementations carry no
/// language/ecosystem assumption in the contract — selection is by operator
/// configuration (<c>Generator</c> exact match plus <c>EcosystemTags</c>
/// capabilities), never by probing for a .NET SDK, MSBuild, or restore.
/// Generation side effects stay in the sandbox under existing policies.
/// </summary>
public interface ICycloneDxSbomGenerator
{
    /// <summary>Stable adapter id (for example <c>cdxgen</c>). Exact match against configuration.</summary>
    string GeneratorId { get; }

    /// <summary>
    /// Produces SBOM bytes for the worktree. Never touches the network outside
    /// the sandbox, never shells out through a string, and never assumes a
    /// .NET toolchain is present.
    /// </summary>
    Task<byte[]> GenerateAsync(
        ISandbox sandbox,
        string workingDirectory,
        CycloneDxSbomAuditorOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Operator-facing options bound from the plugin's scoped config section
/// (<c>CodeyBox:Plugins:codeybox.sbom-cyclonedx</c>). Hot-reloadable through
/// a <c>Func</c> accessor; everything defaults OFF.
/// </summary>
public sealed class CycloneDxSbomAuditorOptions
{
    /// <summary>Master switch. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Baseline-relative policy. Default <c>FailOnAnyChange</c>.</summary>
    public string PolicyMode { get; set; } = nameof(SbomPolicyMode.FailOnAnyChange);

    /// <summary>Explicit candidate SBOM path (worktree-relative). Empty means auto-discover producer output.</summary>
    public string CandidatePath { get; set; } = string.Empty;

    /// <summary>Operator-owned approved-baseline path (worktree-relative). Empty means no baseline is configured.</summary>
    public string BaselinePath { get; set; } = string.Empty;

    /// <summary>SHA-256 pin of the exact approved baseline bytes. Empty means no baseline is approved.</summary>
    public string BaselineDigest { get; set; } = string.Empty;

    /// <summary>Project id the baseline was approved under. Empty skips the project-ownership check.</summary>
    public string BaselineProjectId { get; set; } = string.Empty;

    /// <summary>Config digest the baseline was approved under. Empty skips the configuration-ownership check.</summary>
    public string BaselineConfigDigest { get; set; } = string.Empty;

    /// <summary>Explicitly selected generator adapter id (for example <c>cdxgen</c>). Empty disables generation.</summary>
    public string Generator { get; set; } = string.Empty;

    /// <summary>Expected version of the selected generator binary. Empty means the version probe is skipped (not recommended).</summary>
    public string GeneratorExpectedVersion { get; set; } = string.Empty;

    /// <summary>Project ecosystem capabilities driving generator selection (for example <c>npm,nuget,maven,native</c>).</summary>
    public string EcosystemTags { get; set; } = string.Empty;

    /// <summary>Optional trusted validator binary (for example <c>cyclonedx</c>). Empty keeps the built-in validator only.</summary>
    public string ValidatorTool { get; set; } = string.Empty;

    /// <summary>Expected version of the validator binary.</summary>
    public string ValidatorExpectedVersion { get; set; } = string.Empty;

    /// <summary>Maximum accepted SBOM bytes. Default 5 MiB.</summary>
    public int MaxSbomBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Maximum components. Default 20,000.</summary>
    public int MaxComponents { get; set; } = 20_000;

    /// <summary>Maximum dependency edges. Default 60,000.</summary>
    public int MaxDependencies { get; set; } = 60_000;

    /// <summary>Supported spec versions. Default 1.4, 1.5, 1.6.</summary>
    public string SupportedSpecVersions { get; set; } = "1.4,1.5,1.6";

    /// <summary>Per-operation timeout in seconds. Default 120.</summary>
    public int OperationTimeoutSeconds { get; set; } = 120;

    /// <summary>Projects to <see cref="SbomCycloneDxOptions"/> (pure mapping).</summary>
    public SbomCycloneDxOptions ToCoreOptions() => new()
    {
        Enabled = Enabled,
        MaxSbomBytes = MaxSbomBytes,
        MaxComponents = MaxComponents,
        MaxDependencies = MaxDependencies,
        SupportedSpecVersions = SupportedSpecVersions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        SupportedFormats = ["json", "xml"],
        OperationTimeoutSeconds = OperationTimeoutSeconds,
        PolicyMode = PolicyMode,
    };
}

/// <summary>
/// Optional <c>cdxgen</c> generator adapter. Explicitly selected by the
/// <c>Generator</c> scoped key (exact <c>cdxgen</c> match) and version-pinned
/// via <c>GeneratorExpectedVersion</c>; it is never a default and never
/// assumed. Invokes the <c>cdxgen</c> binary with a structured argv array —
/// never a shell string, never <c>dotnet restore</c> — so non-.NET
/// ecosystems (npm, Maven, native) generate without any .NET toolchain.
/// </summary>
public sealed class CdxgenGeneratorAdapter : ICycloneDxSbomGenerator
{
    /// <summary>Default pinned cdxgen release the adapter is verified against.</summary>
    public const string DefaultExpectedVersion = "11.0.0";

    /// <summary>Adapter id matched exactly against configuration.</summary>
    public string GeneratorId => "cdxgen";

    /// <summary>Probes <c>cdxgen --version</c> in the sandbox; mismatch is infrastructure, never a pass.</summary>
    public static async Task<string> ProbeVersionAsync(ISandbox sandbox, CycloneDxSbomAuditorOptions options, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cdxgen", "--version"],
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new AuditUnavailableException($"could-not-verify: SBOM generator 'cdxgen' version probe failed (exit {result.ExitCode}).");
        return (result.Stdout ?? string.Empty).Trim();
    }

    public async Task<byte[]> GenerateAsync(
        ISandbox sandbox,
        string workingDirectory,
        CycloneDxSbomAuditorOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.Generator, GeneratorId, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: SBOM generator '{options.Generator}' does not select the cdxgen adapter (exact 'cdxgen' required).")
            { IsDeterministic = true };

        var expected = string.IsNullOrWhiteSpace(options.GeneratorExpectedVersion)
            ? DefaultExpectedVersion
            : options.GeneratorExpectedVersion.Trim();
        var actual = await ProbeVersionAsync(sandbox, options, ct).ConfigureAwait(false);
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: SBOM generator 'cdxgen' version mismatch: expected '{expected}', probe reported '{SbomCycloneDxDiff.Redact(actual)}'.")
            { IsDeterministic = true };

        var outputName = ".codeybox-sbom-candidate.cdx.json";
        var argv = new List<string>
        {
            "cdxgen",
            "--output-file", outputName,
            "--spec-version", "1.5",
            "--no-recurse",
            ".",
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.OperationTimeoutSeconds, 5, 3600)));
        var generate = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = workingDirectory,
            MaxStdoutBytes = 256 * 1024,
            MaxStderrBytes = 256 * 1024,
        }, timeoutCts.Token).ConfigureAwait(false);
        if (!generate.Success)
            throw new AuditUnavailableException(
                $"could-not-verify: SBOM generator 'cdxgen' failed (exit {generate.ExitCode}): {SbomCycloneDxDiff.Redact(TrimTail(generate.Stderr ?? string.Empty))}");

        var read = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", outputName],
            WorkingDirectory = workingDirectory,
            MaxStdoutBytes = options.MaxSbomBytes + 1,
            MaxStderrBytes = 4096,
        }, timeoutCts.Token).ConfigureAwait(false);
        if (!read.Success || read.StdoutLimitExceeded)
            throw new AuditUnavailableException(
                "could-not-verify: SBOM generator 'cdxgen' produced missing or oversized output.");
        return System.Text.Encoding.UTF8.GetBytes(read.Stdout ?? string.Empty);
    }

    private static string TrimTail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[^500..];
    }
}

/// <summary>
/// Optional <c>cyclonedx-cli</c> validator. Runs only when the operator sets
/// <c>ValidatorTool</c> to exactly <c>cyclonedx</c> with a matching
/// <c>ValidatorExpectedVersion</c>; otherwise the built-in bounded validator
/// (the Core import path) is the only validator. Bounded, cancellable, and
/// sandbox-contained; a validation failure here is evidence rejection, never
/// a vulnerability verdict.
/// </summary>
public static class CycloneDxCliValidator
{
    /// <summary>Default pinned cyclonedx-cli release the adapter is verified against.</summary>
    public const string DefaultExpectedVersion = "0.24.2";

    /// <summary>
    /// Validates SBOM bytes through <c>cyclonedx validate</c> in the sandbox.
    /// Returns an error string, or null when the tool accepts the document.
    /// </summary>
    public static async Task<string?> ValidateAsync(
        ISandbox sandbox,
        byte[] content,
        string format,
        CycloneDxSbomAuditorOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.ValidatorTool, "cyclonedx", StringComparison.Ordinal))
            return "validator not selected (ValidatorTool must be exactly 'cyclonedx').";
        var expected = string.IsNullOrWhiteSpace(options.ValidatorExpectedVersion)
            ? DefaultExpectedVersion
            : options.ValidatorExpectedVersion.Trim();
        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cyclonedx", "--version"],
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096,
        }, ct).ConfigureAwait(false);
        if (!probe.Success)
            return $"validator 'cyclonedx' version probe failed (exit {probe.ExitCode}).";
        if (!(probe.Stdout ?? string.Empty).Contains(expected, StringComparison.Ordinal))
            return $"validator 'cyclonedx' version mismatch: expected '{expected}'.";
        var inputFlag = string.Equals(format, "xml", StringComparison.OrdinalIgnoreCase) ? "--input-file" : "--input-file";
        _ = inputFlag;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.OperationTimeoutSeconds, 5, 3600)));
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cyclonedx", "validate", "--input-file", "-"],
            Stdin = System.Text.Encoding.UTF8.GetString(content),
            MaxStdoutBytes = 64 * 1024,
            MaxStderrBytes = 64 * 1024,
        }, timeoutCts.Token).ConfigureAwait(false);
        return result.Success ? null : $"validator 'cyclonedx' rejected the document (exit {result.ExitCode}): {SbomCycloneDxDiff.Redact(TrimTail(result.Stderr ?? string.Empty))}";
    }

    private static string TrimTail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[^500..];
    }
}

/// <summary>
/// Bounded evidence retention through the existing artifact store: safe
/// allowlisted names only, size caps enforced before buffering, digests
/// recorded, no fetching of arbitrary referenced URLs.
/// </summary>
public static class SbomEvidenceWriter
{
    /// <summary>
    /// Stores candidate bytes, validation report, and diff report. Throws
    /// <see cref="AuditRunArtifactTooLargeException"/> when any payload
    /// exceeds <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<IReadOnlyList<(string Name, string Digest)>> PutAsync(
        IAuditRunArtifactStore store,
        string runId,
        byte[] candidate,
        string validationJson,
        string diffJson,
        long maxBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validationJson);
        ArgumentNullException.ThrowIfNull(diffJson);
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run id is required.", nameof(runId));
        var validationBytes = System.Text.Encoding.UTF8.GetBytes(validationJson);
        var diffBytes = System.Text.Encoding.UTF8.GetBytes(diffJson);
        var payloads = new (string Name, string MediaType, byte[] Content)[]
        {
            (SbomCycloneDxDiff.ArtifactNames.Candidate, "application/cyclonedx+json", candidate),
            (SbomCycloneDxDiff.ArtifactNames.Validation, "application/json", validationBytes),
            (SbomCycloneDxDiff.ArtifactNames.Diff, "application/json", diffBytes),
        };
        var stored = new List<(string Name, string Digest)>();
        foreach (var (name, mediaType, content) in payloads)
        {
            ct.ThrowIfCancellationRequested();
            if (!SbomCycloneDxDiff.ArtifactNames.IsKnown(name))
                throw new InvalidOperationException($"Refusing to store unknown SBOM artifact '{name}'.");
            if (content.LongLength > maxBytes)
                throw new AuditRunArtifactTooLargeException(name, content.LongLength, maxBytes);
            await store.PutAsync(runId, name, mediaType, content, ct).ConfigureAwait(false);
            stored.Add((name, SbomCycloneDxImport.DigestBytes(content)));
        }
        return stored;
    }
}
