using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// Shader auditor validating configured standalone GLSL/ESSL targets with
/// <c>glslangValidator</c> on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the shared
/// shader-validation family seam (<see cref="IShaderValidationBackend"/> with
/// the glslang backend, target/stage/environment resolution in
/// <see cref="ShaderValidationSupport"/>) plus the glslang-specific knobs
/// below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on every
/// finding.</b> Diagnostics the tool reports at <c>ERROR</c> level (invalid
/// shaders) map to <see cref="AuditSeverity.Error"/> and fail the audit;
/// <c>WARNING</c> maps to <see cref="AuditSeverity.Warning"/> and is
/// advisory. <c>MinimumSeverity</c> only drops findings, it never raises
/// them: there is no mode in which a warning fails the audit.</para>
///
/// <para><b>Exit-code convention (checked against the glslangValidator
/// contract for the pinned release: 0 = valid, non-zero = diagnostics
/// reported).</b> <c>0</c> = ran: valid (no diagnostics) or advisory
/// warnings with diagnostics. <c>1</c> = ran with errors (ERROR diagnostics)
/// or could not run (missing input, bad flags — usage text, never a
/// diagnostic line). Both <c>0</c> and <c>1</c> are findings-producing
/// verdicts; a non-zero exit without a parseable diagnostic fails closed as
/// infrastructure through the parser. <c>126</c>/<c>127</c> = cannot
/// execute / not found — infrastructure. Anything else is an unknown
/// convention and fails loudly as infrastructure rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> The validation surface (language versions,
/// diagnostic shape) changes between releases, so findings are only
/// meaningful from the build the auditor was checked against. The auditor
/// probes <c>glslangValidator --version</c> before the scan (which prints
/// <c>Glslang Version: 14.3.0</c> as its first version token); a missing
/// binary, an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The auditor never walks the tree: every
/// validated file is an explicit operator-configured <c>ShaderTargets</c>
/// entry (<c>path</c> or <c>path:stage</c>), resolved inside the audited
/// worktree with containment and shader-extension checks. All targets are
/// compiled and linked together in ONE <c>glslangValidator</c> invocation as
/// one shader program — that is the tool's native multi-file mode, and the
/// per-asset <c>file:line</c> diagnostics map back to finding locations.
/// Targets that cannot link as one program (independent same-stage entry
/// points, mismatched stage interfaces) surface as infrastructure with
/// guidance, never as findings against the diff and never as a pass.
/// Validation only: no SPIR-V is emitted (<c>-V</c>/<c>-o</c> are never
/// passed) and nothing in the repository is modified. Validating emitted
/// SPIR-V binaries is the <see cref="SpirvValAuditor"/> in this same
/// assembly, sharing the family seam. An empty target list
/// or an empty target file is infrastructure, not a pass.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: glslang Shader Validation",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "glslangValidator",
    AptPackage = "glslang-tools",
    InstallHint = "provision glslangValidator into the sandbox baseline via apt (AptPackage glslang-tools) or "
        + "the versioned upstream glslang release binary — then pin the exact release your baseline "
        + "installs (see ExpectedVersion, default " + DefaultExpectedVersion + ") and keep the two in "
        + "step through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class GlslangAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.glslang";

    /// <summary>
    /// glslang release the invocation and its findings are checked against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "14.3.0";

    /// <summary>
    /// Scoped-config key for the standalone shader files under validation
    /// (comma-separated repo-relative entries, at least one required). Each
    /// entry is <c>path</c> or <c>path:stage</c> (e.g.
    /// <c>shaders/water.vert</c>, <c>shaders/common.glsl:frag</c>). Stages
    /// resolve per entry: explicit suffix first, canonical stage extension
    /// second, <c>DefaultStage</c> last.
    /// </summary>
    public const string ShaderTargetsKey = "ShaderTargets";

    /// <summary>
    /// Scoped-config key for the fallback stage (e.g. <c>frag</c>) applied to
    /// targets with neither an explicit suffix nor a canonical stage
    /// extension (notably <c>.glsl</c> files, which carry no stage in their
    /// name). Unset means every target must resolve its stage otherwise.
    /// </summary>
    public const string DefaultStageKey = "DefaultStage";

    /// <summary>
    /// Scoped-config key for the shader target environment passed to
    /// <c>--target-env</c> (e.g. <c>vulkan1.0</c>, <c>vulkan1.3</c>,
    /// <c>opengl</c>). Always passed explicitly; unset means
    /// <c>vulkan1.0</c>.
    /// </summary>
    public const string TargetEnvironmentKey = "TargetEnvironment";

    // Bounded probe proving every configured target exists and is nonempty
    // before the scan runs: file names ride as "$@" (never through a shell
    // string), one name per argv entry, and any missing or empty target
    // fails the probe. The presence probe above already names missing files;
    // this probe closes the vacuous-pass shape — an empty shader the tool
    // would wave through silently.
    private const string NonEmptyProbeScript =
        "for f in \"$@\"; do test -s \"./$f\" || exit 1; done; exit 0";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = valid (silent or advisory warnings with diagnostics);
        // 1 = errors with ERROR diagnostics OR a run failure (missing
        // input, bad flags) with plain text. The diagnostic lines are the
        // discriminator: exit 1 without one fails closed through the
        // parser. Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings under vendored and dependency trees describe upstream
        // packages, not the change under audit. Operators re-include a path
        // by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private readonly IShaderValidationBackend _backend = new GlslangValidationBackend();

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _shaderTargets = static () => [];
    private Func<string?> _defaultStage = static () => null;
    private Func<string?> _targetEnvironment = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:glslang";

    /// <summary>
    /// The scan validates explicit local files with no fetches, so the
    /// auditor needs no network egress and runs in the most restrictive
    /// sandbox. Anything the tool cannot resolve locally fails closed as
    /// infrastructure under this sandbox rather than silently passing.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => _backend.ToolName;

    /// <summary>
    /// The glslang backend's text-diagnostic parser: per-asset
    /// <c>ERROR</c>/<c>WARNING</c> lines become findings with the
    /// synthesized <c>glslang/validation</c> rule id; program-level link
    /// failures and exits without diagnostics throw and fail closed as
    /// infrastructure.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser => _backend.OutputParser;

    /// <summary>
    /// Declared mapping from glslang's diagnostic levels to CodeyBox's
    /// <see cref="AuditSeverity"/>. Only <c>ERROR</c> fails the audit;
    /// <c>WARNING</c> is advisory by design. Raw levels never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, _backend.VersionProbeArguments);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // glslangValidator's flags are operator-to-tool control with
        // validation-masking members (`-E` preprocesses only, `-o`
        // redirects output, `--stdin` escapes the configured file set), and
        // the tool reads flags anywhere in the vector — so no extra can be
        // quarantined by position, and a non-flag extra would ride along as
        // an unvalidated file operand outside the ShaderTargets contract
        // (no containment, extension, presence, or emptiness checks). Reject
        // every ExtraArguments entry deterministically; the scoped keys cover
        // the supported surface (targets, stage, target environment).
        if (options.ExtraArguments.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', options.ExtraArguments))}') — glslangValidator flags "
                + $"and operands are managed by the auditor ({ShaderTargetsKey}, {DefaultStageKey}, "
                + $"{TargetEnvironmentKey}); ExtraArguments has no supported spelling here. Remove it.")
            { IsDeterministic = true };

        var targets = ResolveTargetSet();
        var environment = ResolvedEnvironment();
        return _backend.BuildValidationArguments(targets, environment);
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // The target files ride as positional operands after the backend
        // flags. They are already contained repo-relative paths (no leading
        // dash possible), so the tool cannot option-parse them; no `--`
        // separator is passed (the validator's contract documents none).
        IReadOnlyList<string> files = ResolveTargetSet().Targets
            .Select(static t => t.Path)
            .ToList();
        return Task.FromResult(files);
    }

    /// <inheritdoc />
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Pure configuration validation first (deterministic, no sandbox
        // needed): the same guards BuildToolArguments enforces, checked here
        // so a misconfigured auditor fails before the version probe even
        // runs and the message names the scoped key, not the tool.
        var targets = ResolveTargetSet();
        ResolvedEnvironment();
        if (options.ExtraArguments.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', options.ExtraArguments))}') — glslangValidator flags "
                + "and operands are managed by the auditor. Remove it.")
            { IsDeterministic = true };

        // Liveness: every configured target must exist in the audited tree.
        // A missing target is a broken configuration (or a tree that drifted
        // under the config) — unavailable, never a pass.
        var operands = targets.Targets.Select(static t => t.Path).Distinct(StringComparer.Ordinal).ToList();
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, operands, options, ct).ConfigureAwait(false);
        var missing = operands
            .Except(present, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (missing.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' input verification failed — "
                + $"{missing.Count} configured file(s) not found in the audited worktree: "
                + $"'{TruncateForMessage(string.Join("', '", missing))}'. The check did not run, "
                + "so this is infrastructure, not a verdict on the change.")
            { IsDeterministic = true };

        // Nonemptiness: an empty shader validates vacuously, so silence
        // afterward would be an unchecked pass. The probe fails closed on
        // any empty target.
        var nonempty = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "input verification",
            new SandboxExec
            {
                Argv = ["sh", "-c", NonEmptyProbeScript, "sh", .. operands],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);
        if (nonempty.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(nonempty.ExitCode);
        if (nonempty.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' input verification failed — a configured "
                + "shader target file is empty (or vanished after the presence check). Empty targets "
                + "validate vacuously, so a silent tool success would prove nothing; this is "
                + "infrastructure, not a verdict on the change.")
            { IsDeterministic = true };
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _shaderTargets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[ShaderTargetsKey]);
        _defaultStage = () => scoped[DefaultStageKey];
        _targetEnvironment = () => scoped[TargetEnvironmentKey];
        context.Logger.LogInformation(
            "GlslangAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private ShaderTargetSet ResolveTargetSet()
        => ShaderValidationSupport.ResolveTargets(
            _shaderTargets(), _defaultStage(), PluginId, ShaderTargetsKey, DefaultStageKey);

    private string ResolvedEnvironment()
        => ShaderValidationSupport.ValidateTargetEnvironment(
            _targetEnvironment(), ShaderValidationSupport.DefaultTargetEnvironment, $"{PluginId}:{TargetEnvironmentKey}");
}
