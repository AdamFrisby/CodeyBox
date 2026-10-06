using CodeyBox.Core;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// Single-module scan engine behind <see cref="SpirvValAuditor"/>: one
/// instance validates exactly one repo-relative <c>.spv</c> binary with
/// <c>spirv-val --target-env &lt;env&gt; &lt;module&gt;</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>. The base supplies the sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration; this class adds the spirv-val report parser
/// (<see cref="SpirvValDiagnosticParser"/>), the tool-version gate for a
/// two-component release scheme (see
/// <see cref="SpirvValHarness.EnsureToolVersionAsync"/>), and the
/// per-module argument shape.
///
/// <para>Instances are single-use: the orchestrator mints one per selected
/// module per run and never shares it across concurrent audits. The
/// per-module exit code is captured into the caller-supplied
/// <see cref="SpirvScanCapture"/> — an explicit holder, because
/// ExecutionContext state (such as <see cref="AsyncLocal{T}"/>) set inside
/// the scan does not flow back to the awaiting caller.</para>
/// </summary>
internal sealed class SpirvScanCapture
{
    /// <summary>Exit code of the scan invocation, if it ran.</summary>
    public int? ExitCode { get; set; }
}

/// <summary>
/// Single-module scan engine behind <see cref="SpirvValAuditor"/>.
/// </summary>
internal sealed class SpirvValSingleModuleScan : ExternalToolAuditorBase
{
    private readonly string _modulePath;
    private readonly string _targetEnvironment;
    private readonly ExternalToolAuditorOptions _options;
    private readonly Func<string?> _expectedVersion;
    private readonly SpirvScanCapture _capture;

    internal SpirvValSingleModuleScan(
        string modulePath,
        string targetEnvironment,
        ExternalToolAuditorOptions options,
        Func<string?> expectedVersion,
        SpirvScanCapture capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetEnvironment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ArgumentNullException.ThrowIfNull(capture);
        _modulePath = modulePath;
        _targetEnvironment = targetEnvironment;
        _options = options;
        _expectedVersion = expectedVersion;
        _capture = capture;
    }

    /// <inheritdoc />
    public override string Name => SpirvValAuditor.AuditorName;

    /// <inheritdoc />
    protected override string ToolName => SpirvValAuditor.ToolBinary;

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser =>
        new SpirvValDiagnosticParser(_modulePath);

    /// <inheritdoc />
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => () => _options;

    /// <summary>
    /// No declarative pin: SPIRV-Tools releases are two-component
    /// (<c>vYYYY.N</c>), which the shared three-component extraction cannot
    /// represent. The equivalent gate lives in
    /// <see cref="VerifyToolAsync"/>, which probes <c>spirv-val
    /// --version</c> and accepts its documented stdout-banner behavior
    /// instead.
    /// </summary>
    protected override ToolVersionPin? VersionPin => null;

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // spirv-val's flags are operator-to-tool control with
        // validation-masking members (`--relax-*`, `--skip-block-layout`,
        // `--scalar-block-layout`, `--before-hlsl-legalization`, `--max-*`
        // resource bounds), and a non-flag extra would ride along as a
        // second file operand (which the tool rejects) or `-` (standard
        // input — outside the SpirvTargets contract: no containment or
        // type checks). Reject every ExtraArguments entry
        // deterministically; the scoped keys cover the supported surface
        // (modules, target environment).
        if (options.ExtraArguments.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', options.ExtraArguments))}') — spirv-val flags "
                + $"and operands are managed by the auditor ({SpirvValAuditor.SpirvTargetsKey}, "
                + $"{SpirvValAuditor.TargetEnvironmentKey}); ExtraArguments has no supported spelling here. Remove it.")
            { IsDeterministic = true };

        // The target environment is always passed explicitly rather than
        // inherited from the tool default, and it is re-validated here
        // because the scan argv is the trust boundary: a future caller
        // could mint this scan with anything. There is no stage flag on
        // this tool — binaries encode their own entry points — and no
        // spirv-opt/remap/fuzz/rewrite flag is ever passed: validation
        // only, nothing is emitted and nothing is modified.
        var environment = ShaderValidationSupport.ValidateTargetEnvironment(
            _targetEnvironment,
            ShaderValidationSupport.DefaultTargetEnvironment,
            $"{SpirvValAuditor.PluginId}:{SpirvValAuditor.TargetEnvironmentKey}");
        return ["--target-env", environment];
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // The module is the single positional input: the tool accepts
        // exactly one binary per invocation. It was validated when this
        // instance was minted, and is re-contained here because the scan
        // argv is the trust boundary: a future caller could mint this scan
        // with anything. It cannot start with a dash (containment rejects
        // leading parameter dashes), so the tool cannot option-parse it; no
        // `--` separator is passed (the validator's contract documents
        // none, and `-` would mean standard input).
        IReadOnlyList<string> positionals =
        [
            SpirvValidationSupport.ContainModulePath(
                _modulePath, $"{SpirvValAuditor.PluginId}:{SpirvValAuditor.SpirvTargetsKey}"),
        ];
        return Task.FromResult(positionals);
    }

    /// <inheritdoc />
    protected override Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
        => SpirvValHarness.EnsureToolVersionAsync(
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
        // Capture the per-module exit for the orchestrator's combined raw
        // output while the run that produced it is in progress.
        // Best-effort: a header must never fail a run the parser itself
        // accepts or rejects.
        _capture.ExitCode = result.ExitCode;
        return base.ResolveParserInputAsync(
            sandbox, workingDirectory, tool, options, result, argv, scanRoot, ct);
    }
}
