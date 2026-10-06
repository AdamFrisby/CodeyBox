using System.Threading;
using CodeyBox.Core;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GltfValidatorAuditorPlugin;

/// <summary>
/// Mutable holder for one scan's report metadata. Populated by
/// <see cref="GltfValidatorSingleAssetScan"/> while its run is in progress
/// and read by the orchestrator after the run completes — a plain
/// caller-owned reference, so no execution-context or sharing semantics
/// are involved.
/// </summary>
internal sealed class ScanReportCapture
{
    /// <summary>
    /// Best-effort coverage summary of the scan's report (see
    /// <see cref="GltfValidatorJsonOutputParser.TrySummarize"/>), or null
    /// when the scan produced no recognizable report.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>Exit code of the scan invocation, if it ran.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Whether the scan validated external resources (buffer contents,
    /// accessor data, images). Defaults true; the scan engine corrects it
    /// when the operator's <c>ExtraArguments</c> override the
    /// resource-validation flag.
    /// </summary>
    public bool ResourcesValidated { get; set; } = true;
}

/// <summary>
/// Single-asset scan engine behind <see cref="GltfValidatorAuditor"/>: one
/// instance validates exactly one repo-relative <c>.gltf</c>/<c>.glb</c> file
/// with <c>gltf_validator -o &lt;asset&gt;</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>. The base supplies the sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration; this class adds the glTF report parser
/// (<see cref="GltfValidatorJsonOutputParser"/>), the tool-version gate for
/// a CLI that has no <c>--version</c> flag (see
/// <see cref="VerifyToolAsync"/>), and the per-asset argument shape.
///
/// <para>Instances are single-use: the orchestrator mints one per selected
/// asset per run and never shares it across concurrent audits. Per-run
/// report metadata (exit code, coverage summary) is captured into the
/// caller-supplied <see cref="ScanReportCapture"/> — an explicit holder,
/// because ExecutionContext state (such as <see cref="AsyncLocal{T}"/>)
/// set inside the scan does not flow back to the awaiting caller.</para>
/// </summary>
internal sealed class GltfValidatorSingleAssetScan : ExternalToolAuditorBase
{
    private readonly string _assetPath;
    private readonly ExternalToolAuditorOptions _options;
    private readonly Func<string?> _expectedVersion;
    private readonly bool _validateResources;
    private readonly ScanReportCapture _capture;

    internal GltfValidatorSingleAssetScan(
        string assetPath,
        ExternalToolAuditorOptions options,
        Func<string?> expectedVersion,
        bool validateResources,
        ScanReportCapture capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ArgumentNullException.ThrowIfNull(capture);
        _assetPath = assetPath;
        _options = options;
        _expectedVersion = expectedVersion;
        _validateResources = validateResources;
        _capture = capture;
    }

    /// <inheritdoc />
    public override string Name => GltfValidatorAuditor.AuditorName;

    /// <inheritdoc />
    protected override string ToolName => GltfValidatorAuditor.ToolBinary;

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } =
        GltfValidatorJsonOutputParser.Instance;

    /// <inheritdoc />
    protected override ExternalToolSeverityMapping SeverityMapping => GltfSeverityMapping;

    /// <summary>
    /// Declared mapping from the validator's severity vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. The report carries numeric
    /// severities (0 = Error, 1 = Warning, 2 = Information, 3 = Hint); the
    /// parser canonicalizes those to words, and both spellings are mapped
    /// here so an uncanonicalized token still lands deterministically. Only
    /// <c>error</c> blocks the audit; raw levels never reach findings.
    /// </summary>
    internal static readonly ExternalToolSeverityMapping GltfSeverityMapping =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["0"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["1"] = AuditSeverity.Warning,
            ["information"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["2"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["3"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => () => _options;

    /// <summary>
    /// No declarative pin: this CLI defines no <c>--version</c> flag (its
    /// argument parser only knows the validation flags), so the shared
    /// probe — <c>&lt;tool&gt; --version</c> expecting exit 0 with a token
    /// on stdout — can never succeed. The equivalent gate lives in
    /// <see cref="VerifyToolAsync"/>, which probes the bare binary and
    /// accepts its documented usage-banner behavior instead.
    /// </summary>
    protected override ToolVersionPin? VersionPin => null;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // The JSON report must ride stdout: without -o the tool writes
        // <asset>.report.json next to the asset inside the audited worktree
        // (a side effect on the audit subject) and the parser would see only
        // the stderr log. --no-stdout would do the same, so it is rejected
        // outright rather than failing closed through the parser.
        if (ExtraArgumentsSupplyFlag(options, "--no-stdout"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with --no-stdout in "
                + "ExtraArguments, which would write <asset>.report.json files into the audited "
                + "worktree instead of the stdout report this auditor parses. Remove it.")
            { IsDeterministic = true };

        // Absolute asset paths in the report would break the repo-relative
        // finding-location contract (and the ExcludePaths prefix filter),
        // because sandbox providers may translate the working directory the
        // parser relativizes against. The default relative-uri mode keeps
        // locations repo-relative.
        if (ExtraArgumentsSupplyFlag(options, "--absolute-path", "-p"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with --absolute-path (or -p) "
                + "in ExtraArguments, which would make report locations absolute instead of "
                + "repo-relative. Remove it.")
            { IsDeterministic = true };

        var args = new List<string> { "-o" };

        // An operator-supplied resource-validation flag wins outright; the
        // auditor never stacks a second one on it. The effective value is
        // captured so the combined raw output records the coverage the
        // scan actually ran with, not just the configured default.
        var effectiveResources = _validateResources;
        if (ExtraArgumentsSupplyFlag(options, "--no-validate-resources"))
            effectiveResources = false;
        else if (ExtraArgumentsSupplyFlag(options, "--validate-resources", "-r"))
            effectiveResources = true;
        _capture.ResourcesValidated = effectiveResources;

        if (!ExtraArgumentsSupplyFlag(options, "--validate-resources", "-r", "--no-validate-resources"))
            args.Add(effectiveResources ? "--validate-resources" : "--no-validate-resources");

        return args;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // The asset is the single positional input. It was validated when
        // this instance was minted, and is re-validated here because the
        // scan argv is the trust boundary: a future caller could mint this
        // scan with anything.
        IReadOnlyList<string> positionals = [GltfAuditorHarness.ContainAssetPath(_assetPath, "Targets")];
        return Task.FromResult(positionals);
    }

    /// <inheritdoc />
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => GltfAuditorHarness.EnsureToolVersionAsync(
            sandbox, workingDirectory, tool, options, _expectedVersion, ct);

    /// <inheritdoc />
    protected override Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
    {
        // Capture per-report metadata for the orchestrator's combined raw
        // output while the run that produced it is in progress. Best-effort:
        // a header must never fail a run the parser itself accepts or rejects.
        _capture.Summary = GltfValidatorJsonOutputParser.TrySummarize(result.Stdout);
        _capture.ExitCode = result.ExitCode;
        return base.ResolveParserInputAsync(
            sandbox, workingDirectory, tool, options, result, argv, scanRoot, ct);
    }
}
