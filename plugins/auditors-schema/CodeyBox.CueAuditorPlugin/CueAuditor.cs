using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CueAuditorPlugin;

/// <summary>
/// Schema auditor wrapping <c>cue vet -c</c> (CUE constraint validation of
/// generated configuration) on the shared <see cref="ExternalToolAuditorBase"/>:
/// the base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the <c>cue vet</c>
/// diagnostic parser (<see cref="CueVetOutputParser"/>), the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>,
/// and the CUE-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: blocking.</b> CUE reports no severities — a
/// diagnostic means the input was not proven valid — so every reported
/// diagnostic maps to <see cref="AuditSeverity.Error"/> and fails the audit.
/// There is no advisory mode; narrow scope with <c>SchemaPaths</c>,
/// <c>InputPaths</c>, <c>SchemaExpression</c>, <c>ExcludedRules</c>, or
/// <c>ExcludePaths</c> instead.</para>
///
/// <para><b>Exit-code convention (verified against cue 0.17.1 by running the
/// provisioned binary across clean, violation-bearing, incomplete-value,
/// missing-file, bad-flag, broken-YAML, and unresolved-import invocations;
/// consistent with <c>cue help vet</c>: "The command is silent when it
/// succeeds; otherwise it reports any errors found").</b> <c>cue vet</c>
/// exits <c>0</c> silently when validation succeeds and <c>1</c> both when
/// validation fails (diagnostics on stderr) <em>and</em> when the run
/// itself failed (missing file, bad flag, unresolved import — plain text on
/// stderr, no diagnostic blocks). The discriminator is therefore the
/// diagnostic output, not the exit code: exits <c>0</c> and <c>1</c> are
/// findings-producing, and an exit without a parseable diagnostic fails
/// closed as infrastructure through the parser. A silent exit <c>0</c> is a
/// checked pass only because <see cref="VerifyToolAsync"/> proves the
/// configured schema/input files exist and are nonempty before the scan —
/// there is no operand-less invocation that could pass vacuously. Any other
/// exit — including <c>126</c>/<c>127</c> (cannot execute / not found) — is
/// infrastructure.</para>
///
/// <para><b>Version pin.</b> The validation surface (language version,
/// standard library, diagnostic shape) changes between releases, so
/// findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>cue version</c> before the scan (which
/// prints <c>cue version v0.17.1</c> plus the language and Go versions; the
/// shared first-token extraction pins the tool version); a missing binary,
/// an unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The auditor never walks the tree: every
/// validated file is an explicit operator-configured operand
/// (<c>SchemaPaths</c> plus <c>InputPaths</c>), resolved inside the audited
/// worktree. Registry and URL inputs (e.g. <c>cue.dev/x/foo@latest</c>),
/// module-wide patterns (<c>./...</c>), directories, and anything outside
/// the worktree are rejected deterministically. The default scan declares
/// no network capability and the sandbox grants none, so an import that
/// can only resolve over the network fails closed as infrastructure rather
/// than silently passing — vendor module dependencies into the baseline or
/// the audited tree instead. Ambient value injection (<c>-t/--inject</c>,
/// <c>-T/--inject-vars</c>), scope widening (<c>-C/--chdir</c>,
/// <c>-I/--proto_path</c>, <c>-n/--name</c> directory expansion), failure masking
/// (<c>-i/--ignore</c>), and output rewriting (<c>-s/--simplify</c>) have no
/// scoped knob and are rejected in <c>ExtraArguments</c>: any flag-looking
/// extra fails closed with a pointer to the scoped keys, and non-flag
/// extras are validated as local operands under the same containment and
/// extension policy. This auditor only ever invokes the <c>vet</c>
/// subcommand — mutation and module commands (<c>export</c>, <c>fix</c>,
/// <c>trim</c>, <c>mod get/publish</c>) have no path to execution.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: CUE Schema Validation",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cue",
    InstallHint = "provision the pinned cue release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "cue — by fetching the versioned upstream GitHub release binary via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class CueAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cue";

    /// <summary>
    /// cue release the invocation and its findings are verified against.
    /// Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.17.1";

    /// <summary>
    /// Scoped-config key for the operator/project-approved CUE constraint
    /// files (comma-separated repo-relative <c>.cue</c> paths, at least one
    /// required). Passed as the leading <c>cue vet</c> operands so data
    /// files validate against them.
    /// </summary>
    public const string SchemaPathsKey = "SchemaPaths";

    /// <summary>
    /// Scoped-config key for the generated configuration under validation
    /// (comma-separated repo-relative files). Accepts the file formats
    /// <c>cue vet</c> documents: CUE (<c>.cue</c>) and data files
    /// (<c>.json</c>, <c>.jsonl</c>, <c>.ndjson</c>, <c>.yaml</c>,
    /// <c>.yml</c>, <c>.toml</c>, <c>.txt</c>). Optional: when empty, the
    /// schemas themselves are still type-checked.
    /// </summary>
    public const string InputPathsKey = "InputPaths";

    /// <summary>
    /// Scoped-config key for an optional CUE expression selecting the schema
    /// (cue's <c>-d/--schema</c>, e.g. <c>#Service</c>). Unset → each file
    /// is checked against the root of the loaded CUE.
    /// </summary>
    public const string SchemaExpressionKey = "SchemaExpression";

    // cue vet never reads a response file or env-provided operands, so the
    // only scope-widening surface is argv: these suffixes are the file
    // formats `cue help vet` documents (CUE sources plus validatable data).
    // Anything else — a directory, `./...`, a registry reference — is
    // rejected before it can select unbounded scope. Compared
    // case-insensitively: filesystems and editors disagree about case, and
    // the tool accepts either spelling.
    private static readonly IReadOnlyList<string> SchemaExtensions = [".cue"];

    private static readonly IReadOnlyList<string> InputExtensions =
        [".cue", ".json", ".jsonl", ".ndjson", ".yaml", ".yml", ".toml", ".txt"];

    // Bounded probe proving every configured operand exists and is nonempty
    // before the scan runs: file names ride as "$@" (never through a shell
    // string), one name per argv entry, and any missing or empty operand
    // fails the probe. The presence probe above already names missing files;
    // this probe closes the vacuous-pass shape — an empty schema or input
    // that `cue vet` would wave through silently.
    private const string NonEmptyProbeScript =
        "for f in \"$@\"; do test -s \"./$f\" || exit 1; done; exit 0";

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = silent validation success (only reachable with verified
        // nonempty operands — see VerifyToolAsync); 1 = validation failures
        // with diagnostics on stderr OR a run failure (missing file, bad
        // flag, unresolved import) with plain text on stderr. The
        // diagnostic blocks are the discriminator: exit 1 without one fails
        // closed through the parser. Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings under vendored and dependency trees describe upstream
        // packages, not the change under audit. Operators re-include a path
        // by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _schemaPaths = static () => [];
    private Func<IReadOnlyList<string>> _inputPaths = static () => [];
    private Func<string?> _schemaExpression = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:cue";

    /// <summary>
    /// The scan validates explicit local files with no fetches, so the
    /// auditor needs no network egress and runs in the most restrictive
    /// sandbox. Imports that can only resolve over the network fail closed
    /// as infrastructure under this sandbox rather than silently passing.
    /// </summary>
    public override AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "cue";

    /// <summary>
    /// The plugin-local <c>cue vet</c> diagnostic parser: CUE reports no
    /// severities or rule ids, so every diagnostic block becomes a finding
    /// with the synthesized <c>cue/validation</c> rule id and the single
    /// severity level the declared mapping turns into an error. Tool-failure
    /// blocks (missing files, unresolved imports, fetch failures) throw and
    /// fail closed as infrastructure.
    /// </summary>
    protected override IExternalToolOutputParser OutputParser { get; } = new CueVetOutputParser();

    /// <summary>
    /// Declared mapping for the single severity level the parser emits.
    /// CUE has no severity vocabulary — any diagnostic means "not proven
    /// valid" — so the only level and the unrecognized default both map to
    /// <see cref="AuditSeverity.Error"/>. Raw tool tokens never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["invalid"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // cue vet's flags are operator-to-tool control with failure-masking
        // members (-i/--ignore proceeds in the presence of errors;
        // -C/--chdir escapes the worktree; -t/-T inject ambient values;
        // -d/--schema and -c/--concrete are the auditor's own contract), and
        // cue parses flags anywhere in the vector — so a flag-looking extra
        // cannot be quarantined by position. Reject every flag-looking
        // ExtraArguments entry deterministically; the scoped keys cover the
        // supported surface and non-flag extras below are validated as
        // local operands under the same containment policy.
        var flagLike = options.ExtraArguments
            .Where(static arg => arg.Length > 0 && IsParameterDash(arg[0]))
            .ToList();
        if (flagLike.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with flag-like ExtraArguments "
                + $"('{TruncateForMessage(string.Join(' ', flagLike))}') — cue vet flags are managed by the "
                + $"auditor (SchemaPaths, {InputPathsKey}, {SchemaExpressionKey}); failure-masking, "
                + "scope-widening, and inject flags have no supported spelling here. Remove it.")
            { IsDeterministic = true };

        var args = new List<string> { "vet", "-c" };

        var expression = ValidatedExpression(_schemaExpression());
        if (expression is not null)
        {
            args.Add("-d");
            args.Add(expression);
        }

        var schemas = _schemaPaths()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ValidatedOperand(p, $"{PluginId}:{SchemaPathsKey}", SchemaExtensions))
            .ToList();
        if (schemas.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has no usable '{SchemaPathsKey}' — configure at least "
                + $"one repo-relative .cue constraint file under CodeyBox:Plugins:{PluginId}:{SchemaPathsKey}. "
                + "An operand-less run would validate whatever the tool finds by itself, which this "
                + "auditor never does.")
            { IsDeterministic = true };

        var inputs = _inputPaths()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ValidatedOperand(p, $"{PluginId}:{InputPathsKey}", InputExtensions))
            .ToList();

        // The `--` separator keeps operands that begin with odd-but-legal
        // characters (digits, `+`) from ever parsing as flags; cue (cobra)
        // accepts it (verified against the pinned release).
        args.Add("--");
        args.AddRange(schemas);
        args.AddRange(inputs);

        foreach (var extra in options.ExtraArguments)
            args.Add(ValidatedOperand(extra, $"{PluginId}:ExtraArguments", InputExtensions));

        return args;
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
        var schemas = _schemaPaths()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ValidatedOperand(p, $"{PluginId}:{SchemaPathsKey}", SchemaExtensions))
            .ToList();
        if (schemas.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has no usable '{SchemaPathsKey}' — configure at least "
                + $"one repo-relative .cue constraint file under CodeyBox:Plugins:{PluginId}:{SchemaPathsKey}.")
            { IsDeterministic = true };
        var inputs = _inputPaths()
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(p => ValidatedOperand(p, $"{PluginId}:{InputPathsKey}", InputExtensions))
            .ToList();
        ValidatedExpression(_schemaExpression());
        foreach (var extra in options.ExtraArguments)
        {
            if (extra.Length > 0 && IsParameterDash(extra[0]))
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' was configured with flag-like ExtraArguments "
                    + $"('{TruncateForMessage(extra)}') — cue vet flags are managed by the auditor. Remove it.")
                { IsDeterministic = true };
            ValidatedOperand(extra, $"{PluginId}:ExtraArguments", InputExtensions);
        }

        // Liveness: every configured operand must exist in the audited tree.
        // A missing schema or input is a broken configuration (or a tree
        // that drifted under the config) — unavailable, never a pass.
        var operands = schemas.Concat(inputs).Distinct(StringComparer.Ordinal).ToList();
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

        // Nonemptiness: an empty schema or input validates vacuously (cue
        // vet is silent on empty unification), so silence afterward would be
        // an unchecked pass. The probe fails closed on any empty operand.
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
                + $"schema/input file is empty (or vanished after the presence check). Empty operands "
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
        _schemaPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[SchemaPathsKey]);
        _inputPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[InputPathsKey]);
        _schemaExpression = () => scoped[SchemaExpressionKey];
        context.Logger.LogInformation(
            "CueAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string ValidatedOperand(string value, string source, IReadOnlyList<string> allowedExtensions)
    {
        var validated = ValidatedRepoRelativeTarget(value, source);
        var normalized = validated.Replace('\\', '/');
        if (normalized.Contains("://", StringComparison.Ordinal) || normalized.Contains('@'))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(validated)}') "
                + "must be a local worktree file — registry references and URLs are never fetched "
                + "during an audit. Vendor the constraints or data into the tree instead.")
            { IsDeterministic = true };
        if (!allowedExtensions.Any(ext => normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' entry ('{TruncateForMessage(validated)}') "
                + $"must name an explicit file ({string.Join(", ", allowedExtensions)}). Directories, "
                + "module patterns such as './...', and registry references are not accepted — the "
                + "auditor validates an explicit file set, nothing it discovers by itself.")
            { IsDeterministic = true };
        return validated;
    }

    private static string? ValidatedExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var validated = ValidatedArgumentValue(value.Trim(), $"{PluginId}:{SchemaExpressionKey}");
        if (validated.Contains("://", StringComparison.Ordinal) || validated.Contains('@'))
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{PluginId}:{SchemaExpressionKey}' "
                + $"('{TruncateForMessage(validated)}') must be a local schema expression such as "
                + "'#Service' — registry references are never fetched during an audit.")
            { IsDeterministic = true };
        return validated;
    }
}
