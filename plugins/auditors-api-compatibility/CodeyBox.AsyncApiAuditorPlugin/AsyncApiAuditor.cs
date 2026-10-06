using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.AsyncApiAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>asyncapi diff</c> (AsyncAPI
/// document breaking-change detection) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the JSON diff report parser
/// (<see cref="AsyncApiDiffParser"/>), the pinned tool-version declaration
/// via <see cref="ExternalToolAuditorBase.VersionPin"/>, the local-document
/// preconditions below, and the validation-then-diff sequencing.
///
/// <para><b>Gate behaviour: blocking on breaking changes.</b> Every change
/// the tool classifies <c>breaking</c> (removed channels, operations, or
/// message payloads the built-in standard deems incompatible) maps to
/// <see cref="AuditSeverity.Error"/> and fails the audit; <c>unclassified</c>
/// (the standard cannot prove either way — needs human review) maps to
/// <see cref="AuditSeverity.Warning"/> and <c>non-breaking</c> (additive,
/// compatible changes) maps to <see cref="AuditSeverity.Info"/>, both
/// advisory. Raw tool levels never reach findings. A reported document
/// difference is a compatibility risk, not proof that a consumer breaks:
/// the diff knows nothing about real operation usage.</para>
///
/// <para><b>Validate, then diff.</b> The scan runs
/// <c>asyncapi diff &lt;OldSpec&gt; &lt;NewSpec&gt; --format json
/// --type &lt;CompatibilityPolicy&gt;</c>, but only after both documents
/// pass <c>asyncapi validate --fail-severity error</c> as bounded
/// pre-scan probes. Validation is load-bearing, not advisory: the diff
/// command short-circuits an invalid document to a silent exit <c>0</c>
/// with no report (verified against the 5.0.7
/// <c>src/apps/cli/commands/diff.ts</c> source), which a validate-first
/// gate must not mistake for a clean comparison. A missing baseline, an
/// unreadable document, an invalid schema, or an unsupported tool version
/// is coverage unavailable — infrastructure, never a pass.</para>
///
/// <para><b>Exit-code convention (verified against
/// <c>@asyncapi/cli</c> 5.0.7 source — not the common 0/1/2 convention).</b>
/// <c>0</c> = the diff completed and no breaking change exists
/// (non-breaking/unclassified changes may still be listed); <c>1</c> =
/// ambiguous: a thrown <c>DiffBreakingChangeError</c> exits <c>1</c> after
/// printing the JSON report, but a load, parse, or validation failure also
/// exits <c>1</c> through the base command's error handler with no JSON
/// report. Both are findings-producing <em>candidates</em>; the parser tells
/// them apart by content — a <c>changes</c> report with breaking entries
/// means findings, anything else means infrastructure. <c>126</c>/<c>127</c>
/// are cannot-execute / not-found — infrastructure. Anything else is an
/// unknown convention and fails loudly as infrastructure rather than being
/// guessed.</para>
///
/// <para><b>Version pin.</b> The classification standard and the JSON report
/// shape move between releases, so findings are only meaningful from the
/// build the auditor was verified against: <c>@asyncapi/cli</c>
/// <c>5.0.7</c>, provisioned through the declared tool requirement below
/// and confirmed at scan time by probing <c>asyncapi --version</c> before
/// every run. Operators running a different pinned build set
/// <c>ExpectedVersion</c> in the plugin's scoped config to match what they
/// provisioned; any other installed release is an infrastructure failure
/// naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled input.</b> The auditor accepts only local
/// repository-relative documents (<c>.json</c>/<c>.yaml</c>/<c>.yml</c>).
/// URLs, context names, absolute paths, <c>..</c> escapes, and scheme
/// prefixes are rejected before any probe runs. The restriction is
/// load-bearing: the CLI resolves each positional as a file, a URL, or a
/// stored <em>context name</em> — and a missing file falls through to
/// context lookup, then to auto-detecting an unrelated spec file in the
/// working directory (verified against the 5.0.7
/// <c>src/domains/models/SpecificationFile.ts</c> source), which would
/// silently compare the wrong documents. The pre-scan therefore proves
/// both specs are regular files (<c>test -f</c>) before validating them,
/// so the file branch of the tool's own resolution is the only reachable
/// one. The diff command reads no repository config file; the flags that
/// would steer what it measures or where its verdict goes
/// (<c>--overrides</c>, <c>--type</c>, <c>--format</c>,
/// <c>--diagnostics-format</c>, <c>--fail-severity</c>,
/// <c>--log-diagnostics</c>, <c>--save-output</c>, <c>--no-error</c>,
/// <c>--watch</c>, proxy flags) are owned by this auditor or rejected in
/// operator <c>ExtraArguments</c> as deterministic infrastructure failures.
/// Override-based reclassification is deliberately not offered: it would let
/// reported categories diverge from the tool's built-in standard — and, for
/// an in-repo overrides file, let the diff author tune its own gate.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>asyncapi diff &lt;OldSpec&gt; &lt;NewSpec&gt; --format json
/// --type &lt;CompatibilityPolicy&gt;</c> (default <c>all</c>) over exactly
/// the two configured documents — the tool never walks the repository, so
/// vendored or generated trees cannot produce findings and the auditor
/// ships no <c>ExcludePaths</c> default. Both specs are required with no
/// guessed default: document layout varies per repository, and any invented
/// default risks a silent no-op diff. An unset spec is a deterministic
/// infrastructure failure naming the key — enabling this auditor without
/// configuring it is a loud misconfiguration, not a silent skip. Identical
/// old/new pointers are likewise rejected: they would compare a document
/// against itself and pass vacuously.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: AsyncAPI Contract Compatibility",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "asyncapi",
    InstallHint = "provision the pinned AsyncAPI CLI release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline via npm (npm install -g @asyncapi/cli@"
        + DefaultExpectedVersion + ") through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd "
        + "or ExecutableProvisions — no distro apt package carries a pinned release")]
[CodeyBoxPluginRequiresTool(
    "node",
    InstallHint = "the AsyncAPI CLI runs on Node.js (see the pinned release's engines field — "
        + "5.x requires Node.js 18+); bake a Node.js runtime alongside the pinned CLI, otherwise "
        + "the presence probe fails closed as infrastructure")]
public sealed class AsyncApiAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.asyncapi";

    /// <summary>
    /// AsyncAPI CLI release the invocation, its exit convention, and its JSON
    /// report shape were verified against. Operators running a different
    /// pinned build set <c>ExpectedVersion</c> in the plugin's scoped config
    /// to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "5.0.7";

    /// <summary>Scoped-config key for the approved baseline (old) AsyncAPI document.</summary>
    public const string OldSpecKey = "OldSpec";

    /// <summary>Scoped-config key for the candidate (new) AsyncAPI document.</summary>
    public const string NewSpecKey = "NewSpec";

    /// <summary>Scoped-config key selecting which change categories the tool collects.</summary>
    public const string CompatibilityPolicyKey = "CompatibilityPolicy";

    /// <summary>
    /// Values accepted for <c>CompatibilityPolicy</c>, passed through as the
    /// tool's <c>--type</c> flag. The default <c>all</c> preserves every
    /// category; narrower selections collect only that category.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedCompatibilityPolicies =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "all",
            "breaking",
            "non-breaking",
            "unclassified",
        };

    /// <summary>Default policy: collect every change category.</summary>
    internal const string DefaultCompatibilityPolicy = "all";

    private const int MaxSpecPathChars = 1024;

    private static readonly string[] ContractAlteringFlags =
    [
        "-f", "--format",
        "-t", "--type",
        "--markdownSubtype",
        "-o", "--overrides",
        "-s", "--save-output",
        "-w", "--watch",
        "--no-error",
        "--log-diagnostics",
        "--diagnostics-format",
        "--fail-severity",
        "--proxyHost",
        "--proxyPort",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran, no breaking changes (advisory changes may still be
        // listed); 1 = breaking changes found OR could not run — the parser
        // tells them apart by content. Everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // No default: the tool reads exactly the two configured documents
        // and never walks the tree, so vendored or generated code cannot
        // produce findings to exclude.
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _oldSpec = static () => null;
    private Func<string?> _newSpec = static () => null;
    private Func<string?> _compatibilityPolicy = static () => DefaultCompatibilityPolicy;

    /// <inheritdoc />
    public override string Name => "codeybox:asyncapi";

    /// <inheritdoc />
    protected override string ToolName => "asyncapi";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser =>
        // Bound per invocation so the finding path echoes the current
        // scoped NewSpec pointer; operator edits apply without a restart.
        new AsyncApiDiffParser(_newSpec);

    /// <summary>
    /// Declared mapping from the diff classifier's category vocabulary to
    /// CodeyBox's <see cref="AuditSeverity"/>. The parser supplies the
    /// canonical category tokens; raw strings never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["breaking"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["unclassified"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["non-breaking"] = AuditSeverity.Info,
            ["nonbreaking"] = AuditSeverity.Info,
            ["safe"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // Validated here as well as in VerifyToolAsync: the base builds the
        // scan argv before its presence and version checks, so a mispointed
        // spec fails deterministically before any probe or scan runs.
        var (oldSpec, newSpec) = ResolveScanSpecs();
        return
        [
            "diff",
            oldSpec,
            newSpec,
            "--format", "json",
            "--type", ResolveCompatibilityPolicy(),
        ];
    }

    /// <inheritdoc />
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var (oldSpec, newSpec) = ResolveScanSpecs();

        foreach (var extra in options.ExtraArguments)
        {
            if (IsContractAlteringFlag(extra))
                throw new AuditUnavailableException(
                    $"Audit tool '{tool}' is misconfigured: ExtraArguments entry '{extra}' steers what "
                    + "the diff measures or where its verdict goes (--type/--format/--overrides/--save-output/--no-error/--watch/diagnostics/proxy flags). "
                    + $"Use scoped '{CompatibilityPolicyKey}' to select change categories.")
                { IsDeterministic = true };
        }

        await ThrowIfSpecsNotRegularFilesAsync(
            sandbox, workingDirectory, tool, oldSpec, newSpec, options, ct).ConfigureAwait(false);
        await ValidateDocumentAsync(
            sandbox, workingDirectory, tool, oldSpec, OldSpecKey, options, ct).ConfigureAwait(false);
        await ValidateDocumentAsync(
            sandbox, workingDirectory, tool, newSpec, NewSpecKey, options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _oldSpec = () => scoped[OldSpecKey];
        _newSpec = () => scoped[NewSpecKey];
        _compatibilityPolicy = () => scoped[CompatibilityPolicyKey];
        context.Logger.LogInformation(
            "AsyncApiAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private string ResolveCompatibilityPolicy()
    {
        var policy = _compatibilityPolicy()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(policy))
            return DefaultCompatibilityPolicy;
        if (!AllowedCompatibilityPolicies.Contains(policy))
            throw new AuditUnavailableException(
                $"Audit tool '{ToolName}' is misconfigured: scoped '{CompatibilityPolicyKey}' value "
                + $"'{policy}' is not a supported policy (supported: {string.Join(", ", AllowedCompatibilityPolicies.Order())}).")
            { IsDeterministic = true };
        return policy;
    }

    /// <summary>
    /// Resolves and validates both configured spec pointers plus their
    /// distinctness. Called from <see cref="BuildToolArguments"/> (so a
    /// mispointed spec fails before any probe runs) and from
    /// <see cref="VerifyToolAsync"/> (which additionally gates the
    /// file-presence and validation probes on the validated values).
    /// </summary>
    private (string OldSpec, string NewSpec) ResolveScanSpecs()
    {
        var oldSpec = ValidateSpecPointer(_oldSpec(), OldSpecKey, ToolName);
        var newSpec = ValidateSpecPointer(_newSpec(), NewSpecKey, ToolName);
        if (string.Equals(oldSpec, newSpec, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"Audit tool '{ToolName}' is misconfigured: scoped '{OldSpecKey}' and '{NewSpecKey}' "
                + "name the same document — a document compared against itself passes vacuously. "
                + $"Point '{OldSpecKey}' at the approved baseline and '{NewSpecKey}' at the candidate.")
            { IsDeterministic = true };
        return (oldSpec, newSpec);
    }

    /// <summary>
    /// Validates a configured spec pointer as a local repository-relative
    /// document path: no URLs, no context names (bare words without a
    /// document extension resolve through the CLI's context store), no
    /// absolute paths, no <c>..</c> escapes, and no scheme prefixes. Values
    /// travel to the tool only as argv entries, never through a shell.
    /// </summary>
    internal static string ValidateSpecPointer(string? pointer, string key, string tool)
    {
        if (string.IsNullOrWhiteSpace(pointer) || pointer.Trim().Length > MaxSpecPathChars)
            throw new AuditUnavailableException(
                $"Audit tool '{tool}' is misconfigured: scoped '{key}' must name the AsyncAPI document "
                + $"(a repository-relative .json/.yaml/.yml file of at most {MaxSpecPathChars} characters). "
                + $"Configure CodeyBox:Plugins:{PluginId}:{key}.")
            { IsDeterministic = true };

        var candidate = ValidatedRepoRelativeTarget(pointer.Trim(), key);
        var normalized = candidate.Replace('\\', '/');
        if (normalized.Contains("://", StringComparison.Ordinal)
            || HasSchemePrefix(normalized)
            || !IsSupportedDocumentExtension(normalized))
            throw new AuditUnavailableException(
                $"Audit tool '{tool}' is misconfigured: scoped '{key}' value '{candidate}' is not a "
                + "local AsyncAPI document — URLs, context names, and non-document paths are not "
                + "accepted. Point it at a repository-relative .json/.yaml/.yml file.")
            { IsDeterministic = true };
        return candidate;
    }

    private static bool IsSupportedDocumentExtension(string path)
        => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static bool HasSchemePrefix(string path)
    {
        var colon = path.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 0; i < colon; i++)
        {
            var c = path[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Proves both specs are regular files before validating them. The CLI
    /// resolves a positional as a file, a URL, or a stored context name —
    /// and a missing file falls through to context lookup, then to
    /// auto-detecting an unrelated spec file in the working directory,
    /// which would silently compare the wrong documents. The
    /// <c>test -f</c> probe reaches the sandbox as argv entries (never a
    /// shell string), so only the file branch of the tool's own resolution
    /// is reachable downstream.
    /// </summary>
    private static async Task ThrowIfSpecsNotRegularFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string oldSpec,
        string newSpec,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "spec presence check",
            new SandboxExec
            {
                Argv = ["sh", "-c", "test -f \"$1\" && test -f \"$2\"", "sh", oldSpec, newSpec],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not confirm both configured documents "
                + "are regular files in the audited worktree — a missing baseline or candidate is "
                + "coverage unavailable, never a verdict on the diff.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Runs <c>asyncapi validate --fail-severity error</c> over one document
    /// as a bounded precondition probe. A non-zero exit (invalid schema,
    /// unparseable document, unresolvable reference) is coverage unavailable
    /// naming the document — the diff command short-circuits invalid
    /// documents to a silent exit <c>0</c>, so validation must gate the scan
    /// rather than ride along with it.
    /// </summary>
    private static async Task ValidateDocumentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string spec,
        string key,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "spec validation",
            new SandboxExec
            {
                Argv = [tool, "validate", spec, "--fail-severity", "error"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not validate scoped '{key}' document "
                + $"'{spec}' (exit {probe.ExitCode}) — an invalid or unreadable document is coverage "
                + "unavailable, never a verdict on the diff.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
    }

    private static bool IsContractAlteringFlag(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return false;
        var value = argument.Trim();
        foreach (var flag in ContractAlteringFlags)
        {
            if (value.Equals(flag, StringComparison.Ordinal)
                || value.StartsWith(flag + "=", StringComparison.Ordinal))
                return true;
            // Joined short form (e.g. "-fjson"): the base documents and
            // handles this shape for single-dash value options, so the guard
            // matches it too.
            if (flag.Length == 2 && flag[0] == '-' && flag[1] != '-'
                && value.Length > flag.Length
                && value.StartsWith(flag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
