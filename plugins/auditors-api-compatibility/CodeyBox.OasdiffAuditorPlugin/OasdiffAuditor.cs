using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OasdiffAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>oasdiff breaking</c> (OpenAPI breaking-change
/// detection) on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code classification,
/// severity mapping, finding identity, and per-auditor configuration. This class adds the JSON
/// breaking-changes report parser (<see cref="OasdiffBreakingJsonParser"/>), the pinned
/// tool-version declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// local-document preconditions below, and the validation-then-diff sequencing.
///
/// <para><b>Gate behaviour: blocking on definite breaking changes.</b> Every change the tool
/// classifies <c>ERR</c> (a removed success response, a deleted endpoint, a narrowed schema —
/// a consumer following the old contract can stop working) maps to
/// <see cref="AuditSeverity.Error"/> and fails the audit; <c>WARN</c> (a potential break the
/// definition cannot decide alone, e.g. a removed request parameter) maps to
/// <see cref="AuditSeverity.Warning"/> and is advisory. Raw tool levels never reach findings.
/// A reported difference is a contract risk, not proof that a consumer breaks: the diff knows
/// nothing about real operation usage.</para>
///
/// <para><b>Validate, then diff.</b> The scan runs
/// <c>oasdiff breaking &lt;BaseSpec&gt; &lt;Spec&gt; --format json --fail-on &lt;FailOn&gt;</c>,
/// but only after both documents pass <c>oasdiff validate --fail-on ERR</c> as bounded
/// pre-scan probes. Validation is load-bearing, not advisory: the breaking command loads a
/// document that is not an OpenAPI spec (valid YAML, wrong shape) as an empty API surface and
/// short-circuits to a silent exit <c>0</c> with an empty (<c>[]</c>) report (verified against
/// oasdiff 1.33.0), which a validate-first gate must not mistake for a clean comparison. A
/// missing baseline, an unreadable document, or an invalid spec is coverage unavailable —
/// infrastructure, never a pass.</para>
///
/// <para><b>Exit-code convention (verified against oasdiff 1.33.0 — not the common 0/1/2
/// convention).</b> <c>0</c> = the comparison completed with no breaking changes at or above
/// the <c>--fail-on</c> threshold (WARN-only entries still print on exit <c>0</c>);
/// <c>1</c> = the comparison completed and breaking changes at or above the threshold exist
/// (the JSON report prints on stdout); <c>100</c> = usage error (unknown flag);
/// <c>102</c> = a spec failed to load (missing file, unparseable content). Only <c>0</c> and
/// <c>1</c> are findings-producing exits; the parser tells them apart by content — a report
/// with at-or-above-threshold entries means findings, anything else means infrastructure.
/// <c>126</c>/<c>127</c> are cannot-execute / not-found — infrastructure. Anything else is an
/// unknown convention and fails loudly as infrastructure rather than being guessed.</para>
///
/// <para><b>Version pin.</b> The check catalogue and the JSON report shape move between
/// releases, so findings are only meaningful from the build the auditor was verified against:
/// oasdiff <c>1.33.0</c>, provisioned through the declared tool requirement below and confirmed
/// at scan time by probing <c>oasdiff --version</c> before every run. Operators running a
/// different pinned build set <c>ExpectedVersion</c> in the plugin's scoped config to match what
/// they provisioned; any other installed release is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled input.</b> The auditor accepts only local repository-relative
/// documents (<c>.json</c>/<c>.yaml</c>/<c>.yml</c>). URLs, absolute paths, <c>..</c> escapes,
/// and scheme prefixes are rejected before any probe runs. The restriction is load-bearing:
/// each positional also resolves as a URL or git revision, and a missing file fails the load —
/// so the pre-scan proves both specs are regular files (<c>test -f</c>) before validating them,
/// leaving only the file branch of the tool's own resolution reachable downstream. The flags
/// that would steer what the comparison measures or where its verdict goes (<c>--format</c>,
/// <c>--fail-on</c>, <c>--composed</c>, <c>--err-ignore</c>, <c>--warn-ignore</c>,
/// <c>--severity-levels</c>, <c>--template</c>, <c>--color</c>) are owned by this auditor or
/// rejected in operator <c>ExtraArguments</c> as deterministic infrastructure failures.
/// Ignore-file reclassification is deliberately not offered: it would let reported severities
/// diverge from the tool's built-in standard — and, for an in-repo ignore file, let the diff
/// author tune its own gate. Comparison tuning that cannot soften the verdict
/// (<c>--match-path</c>, <c>--flatten-allof</c>, <c>--allow-external-refs=false</c>, …) stays
/// available through <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is
/// <c>oasdiff breaking &lt;BaseSpec&gt; &lt;Spec&gt; --format json --fail-on &lt;FailOn&gt;</c>
/// (default <c>ERR</c>) over exactly the two configured documents — the tool never walks the
/// repository, so vendored or generated trees cannot produce findings and the auditor ships no
/// <c>ExcludePaths</c> default. Both specs are required with no guessed default: document
/// layout varies per repository, and any invented default risks a silent no-op diff. An unset
/// spec is a deterministic infrastructure failure naming the key — enabling this auditor without
/// configuring it is a loud misconfiguration, not a silent skip. Identical base/spec pointers
/// are likewise rejected: they would compare a document against itself and pass vacuously.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Oasdiff OpenAPI Breaking Changes",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "oasdiff",
    InstallHint = "provision the pinned oasdiff release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline from the upstream GitHub release "
        + "tarball (oasdiff_<version>_linux_<arch>.tar.gz plus checksums.txt) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions — "
        + "no distro apt package carries a pinned release")]
public sealed class OasdiffAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.oasdiff";

    /// <summary>
    /// oasdiff release the invocation, its exit convention, and its JSON report shape were
    /// verified against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.33.0";

    /// <summary>Scoped-config key for the approved baseline (old) OpenAPI document.</summary>
    public const string BaseSpecKey = "BaseSpec";

    /// <summary>Scoped-config key for the candidate (new) OpenAPI document.</summary>
    public const string SpecKey = "Spec";

    /// <summary>Scoped-config key selecting which severities fail the scan command.</summary>
    public const string FailOnKey = "FailOn";

    /// <summary>Fail-on value: exit 1 only on definite (ERR) breaking changes.</summary>
    internal const string ErrFailOn = "ERR";

    /// <summary>Fail-on value: exit 1 on definite or potential (ERR or WARN) breaking changes.</summary>
    internal const string WarnFailOn = "WARN";

    /// <summary>Default fail-on threshold: only definite breaking changes fail the command.</summary>
    internal const string DefaultFailOn = ErrFailOn;

    private const int MaxSpecPathChars = 1024;

    private static readonly string[] ContractAlteringFlags =
    [
        "-f", "--format",
        "-o", "--fail-on",
        "-c", "--composed",
        "--err-ignore",
        "--warn-ignore",
        "--severity-levels",
        "--template",
        "--color",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran, no breaking changes at the --fail-on threshold (WARN-only
        // entries may still be listed); 1 = breaking changes at or above the
        // threshold. Both are findings-producing candidates; the parser tells
        // "breaking changes found" apart from "could not run" by report
        // content. Everything else (100 usage error, 102 load failure, …) is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // No default: the tool reads exactly the two configured documents
        // and never walks the tree, so vendored or generated code cannot
        // produce findings to exclude.
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _baseSpec = static () => null;
    private Func<string?> _spec = static () => null;
    private Func<string?> _failOn = static () => DefaultFailOn;

    /// <inheritdoc />
    public override string Name => "codeybox:oasdiff";

    /// <inheritdoc />
    protected override string ToolName => "oasdiff";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser =>
        // Bound per invocation so the finding path and the fail-on threshold echo the
        // current scoped pointers; operator edits apply without a restart.
        new OasdiffBreakingJsonParser(_spec, _failOn);

    /// <summary>
    /// Declared mapping from oasdiff's severity vocabulary to CodeyBox's
    /// <see cref="AuditSeverity"/>. The parser supplies the canonical level tokens;
    /// raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["err"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["warn"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["low"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["hint"] = AuditSeverity.Info,
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
        var (baseSpec, spec) = ResolveScanSpecs();
        return
        [
            "breaking",
            baseSpec,
            spec,
            "--format", "json",
            "--fail-on", ResolveFailOn(),
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
        var (baseSpec, spec) = ResolveScanSpecs();

        foreach (var extra in options.ExtraArguments)
        {
            if (IsContractAlteringFlag(extra))
                throw new AuditUnavailableException(
                    $"Audit tool '{tool}' is misconfigured: ExtraArguments entry '{extra}' steers what "
                    + "the comparison measures or where its verdict goes (--format/--fail-on/--composed/ignore-files/severity-levels/template/color flags). "
                    + $"Use scoped '{FailOnKey}' to select the fail-on threshold and pass comparison tuning "
                    + "(--match-path, --flatten-allof, --allow-external-refs, …) that cannot soften the verdict.")
                { IsDeterministic = true };
        }

        await ThrowIfSpecsNotRegularFilesAsync(
            sandbox, workingDirectory, tool, baseSpec, spec, options, ct).ConfigureAwait(false);
        await ValidateDocumentAsync(
            sandbox, workingDirectory, tool, baseSpec, BaseSpecKey, options, ct).ConfigureAwait(false);
        await ValidateDocumentAsync(
            sandbox, workingDirectory, tool, spec, SpecKey, options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _baseSpec = () => scoped[BaseSpecKey];
        _spec = () => scoped[SpecKey];
        _failOn = () => scoped[FailOnKey];
        context.Logger.LogInformation(
            "OasdiffAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private string ResolveFailOn()
    {
        var failOn = _failOn()?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(failOn))
            return DefaultFailOn;
        if (!string.Equals(failOn, ErrFailOn, StringComparison.Ordinal)
            && !string.Equals(failOn, WarnFailOn, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"Audit tool '{ToolName}' is misconfigured: scoped '{FailOnKey}' value "
                + $"'{failOn}' is not a supported threshold (supported: {ErrFailOn}, {WarnFailOn}).")
            { IsDeterministic = true };
        return failOn;
    }

    /// <summary>
    /// Resolves and validates both configured spec pointers plus their
    /// distinctness. Called from <see cref="BuildToolArguments"/> (so a
    /// mispointed spec fails before any probe runs) and from
    /// <see cref="VerifyToolAsync"/> (which additionally gates the
    /// file-presence and validation probes on the validated values).
    /// </summary>
    private (string BaseSpec, string Spec) ResolveScanSpecs()
    {
        var baseSpec = ValidateSpecPointer(_baseSpec(), BaseSpecKey, ToolName);
        var spec = ValidateSpecPointer(_spec(), SpecKey, ToolName);
        if (string.Equals(baseSpec, spec, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"Audit tool '{ToolName}' is misconfigured: scoped '{BaseSpecKey}' and '{SpecKey}' "
                + "name the same document — a document compared against itself passes vacuously. "
                + $"Point '{BaseSpecKey}' at the approved baseline and '{SpecKey}' at the candidate.")
            { IsDeterministic = true };
        return (baseSpec, spec);
    }

    /// <summary>
    /// Validates a configured spec pointer as a local repository-relative
    /// document path: no URLs, no absolute paths, no <c>..</c> escapes, and no
    /// scheme prefixes. Values travel to the tool only as argv entries, never
    /// through a shell.
    /// </summary>
    internal static string ValidateSpecPointer(string? pointer, string key, string tool)
    {
        if (string.IsNullOrWhiteSpace(pointer) || pointer.Trim().Length > MaxSpecPathChars)
            throw new AuditUnavailableException(
                $"Audit tool '{tool}' is misconfigured: scoped '{key}' must name the OpenAPI document "
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
                + "local OpenAPI document — URLs and non-document paths are not "
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
    /// Proves both specs are regular files before validating them. A spec
    /// pointer that names no file must fail here — not inside the tool's own
    /// URL/revision resolution, which would report the wrong comparison's
    /// failure. The <c>test -f</c> probe reaches the sandbox as argv entries
    /// (never a shell string), so only the file branch of the tool's own
    /// resolution is reachable downstream.
    /// </summary>
    private static async Task ThrowIfSpecsNotRegularFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        string baseSpec,
        string spec,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "spec presence check",
            new SandboxExec
            {
                Argv = ["sh", "-c", "test -f \"$1\" && test -f \"$2\"", "sh", baseSpec, spec],
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
    /// Runs <c>oasdiff validate --fail-on ERR</c> over one document as a bounded
    /// precondition probe. A non-zero exit (invalid spec, unparseable document,
    /// unresolvable reference) is coverage unavailable naming the document — the
    /// breaking command short-circuits a non-OpenAPI document to a silent exit
    /// <c>0</c> with an empty report, so validation must gate the scan rather than
    /// ride along with it.
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
                Argv = [tool, "validate", "--fail-on", ErrFailOn, spec],
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
