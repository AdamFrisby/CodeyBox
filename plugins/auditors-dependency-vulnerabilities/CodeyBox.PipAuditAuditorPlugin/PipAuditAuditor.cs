using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PipAuditAuditorPlugin;

/// <summary>
/// Dependency auditor wrapping <c>pip-audit</c> (Python dependency CVEs)
/// on the shared <see cref="ExternalToolAuditorBase"/>: the base supplies
/// sandboxed invocation with a bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// per-auditor configuration. This class adds the JSON report parser
/// (<see cref="PipAuditJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, and
/// the pip-audit-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: blocking by default.</b> pip-audit's own gate is
/// "any known vulnerability fails the run" (it exits <c>1</c> for any vuln,
/// with no severity gradation — the JSON report carries no per-vulnerability
/// severity). The declared mapping preserves that: a vulnerability with no
/// severity signal maps to <see cref="AuditSeverity.Error"/> and fails the
/// audit. Reports that do carry a severity token map it the same way every
/// other auditor maps it (<c>high</c> → Error, <c>medium</c> → Warning,
/// <c>low</c> → Info). <c>MinimumSeverity</c> only drops findings, it never
/// raises them, so operators who want this auditor advisory-only raise the
/// threshold instead.</para>
///
/// <para><b>Exit-code convention (verified against pip-audit 2.10.x
/// source, <c>pip_audit/_cli.py</c>).</b> pip-audit does NOT follow the
/// common "1 = findings, 2 = could not run" convention: it exits <c>1</c>
/// both when vulnerabilities are found (<c>sys.exit(1)</c> after printing
/// the report) and when the run fails outright — every <c>_fatal</c> path
/// (unresolvable requirements input, missing project file, unreachable
/// vulnerability service, strict-mode skip) also exits <c>1</c> via
/// <c>sys.exit(1)</c>. The discriminator is the JSON manifest pip-audit
/// prints to <b>stdout</b> only when the audit completes (the human-readable
/// <c>Found … / No known vulnerabilities found</c> summary always goes to
/// stderr): exits 0–1 <em>with</em> a parseable manifest are verdicts; exit
/// <c>1</c> without one is a run failure, and the parser fails closed as
/// infrastructure. Argument misuse exits <c>2</c> via argparse, panics and
/// tracebacks exit non-zero with no manifest — all infrastructure.
/// <c>126</c>/<c>127</c> cannot-execute — infrastructure. Anything else is
/// an unknown convention and fails loudly as infrastructure rather than
/// being guessed.</para>
///
/// <para><b>Stream note.</b> In <c>--format json</c> mode the report goes to
/// <b>stdout</b> (or to the <c>--output</c> file when one is given — do not
/// set it; an empty stdout fails closed as infrastructure). The parser reads
/// stdout; stderr carries only the progress spinner and the summary line.
/// <c>--progress-spinner off</c> keeps spinner control sequences out of the
/// captured streams.</para>
///
/// <para><b>Version pin.</b> pip-audit's report shape changed between
/// releases (a bare dependency array became
/// <c>{"dependencies":[…],"fixes":[…]}</c>), so findings are only meaningful
/// from the build the auditor was verified against. The auditor probes
/// <c>pip-audit --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding. The parser accepts both report shapes so a
/// pinned older release keeps parsing.</para>
///
/// <para><b>Repository-controlled input.</b> Unlike scanners with their own
/// suppression files, pip-audit takes all of its input from the invocation:
/// the requirements files, the project directory, and the
/// <c>--ignore-vuln</c> list all come from this auditor's scoped config (or
/// its defaults), so there is no repository file the audit subject can drop
/// in to silence the scan outside the diff. The requirements content itself
/// is repository-controlled resolution input by tool design — a requirement
/// can name an arbitrary index or URL — so the sandbox egress allowlist is
/// the containment for resolution traffic; see the plugin README.</para>
///
/// <para><b>Scope and defaults.</b> The default scan is
/// <c>pip-audit --format json -r requirements.txt</c> at the worktree root:
/// the resolved dependency set of the root requirements file, not a file
/// tree. Vendored trees are never resolved by pip-audit, so no paths are
/// excluded by default — and findings carry no file paths, so
/// <c>ExcludePaths</c> currently has nothing to match. Repositories without
/// a root <c>requirements.txt</c> fail loudly: pip-audit rejects the missing
/// input file, the run is infrastructure, never a pass — point
/// <c>Requirements</c> at the real requirements files or <c>ProjectPath</c>
/// at a <c>pyproject.toml</c>/<c>pylock.toml</c> project directory instead.
/// Non-Python repositories likewise fail loudly.</para>
///
/// <para><b>Network.</b> Every run queries the configured vulnerability
/// service (PyPI by default, OSV or ESMS via <c>VulnerabilityService</c>)
/// and pip-audit resolves requirements through pip, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> unconditionally — there is no
/// offline mode. The service hosts must be in the deployment's
/// <c>AuditToolAllowedHosts</c> egress list.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: pip-audit Python Dependency CVEs",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "pip-audit",
    InstallHint = "provision the pinned pip-audit release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `python3 -m pip install "
        + "\"pip-audit==" + DefaultExpectedVersion + "\"` (Python 3.10 or newer) — through "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; no distro apt package carries a version pin")]
public sealed class PipAuditAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.pip-audit";

    /// <summary>
    /// pip-audit release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.10.1";

    /// <summary>
    /// Scoped-config key for the requirements files to audit
    /// (comma-separated, repo-relative; each becomes a <c>-r</c> argument).
    /// Unset or blank → <c>requirements.txt</c> at the worktree root.
    /// Ignored when <see cref="ProjectPathKey"/> is set without an explicit
    /// <c>Requirements</c> value — an explicit <c>Requirements</c> value
    /// always wins, mirroring pip-audit's own precedence.
    /// </summary>
    public const string RequirementsKey = "Requirements";

    /// <summary>
    /// Default requirements file audited when <c>Requirements</c> is unset.
    /// </summary>
    public const string DefaultRequirements = "requirements.txt";

    /// <summary>
    /// Scoped-config key for auditing a local Python project directory
    /// (<c>pyproject.toml</c> / <c>pylock.*.toml</c>) instead of requirements
    /// files — pip-audit's positional <c>project_path</c> argument. Unset by
    /// default. Repositories whose Python project lives in a subdirectory
    /// point this at that directory.
    /// </summary>
    public const string ProjectPathKey = "ProjectPath";

    /// <summary>
    /// Scoped-config key for pip-audit's <c>-s/--vulnerability-service</c>:
    /// <c>osv</c>, <c>pypi</c>, or <c>esms</c>. Unset → pip-audit's own
    /// default (<c>pypi</c>). Anything else is a deterministic configuration
    /// failure.
    /// </summary>
    public const string VulnerabilityServiceKey = "VulnerabilityService";

    /// <summary>
    /// Scoped-config boolean for pip-audit's <c>-l/--local</c>: audit only
    /// the packages installed in the sandbox's local environment that satisfy
    /// the requirements, skipping system packages. Default false.
    /// </summary>
    public const string LocalKey = "Local";

    /// <summary>
    /// Scoped-config key for vulnerability ids to ignore (comma-separated;
    /// each becomes a <c>--ignore-vuln</c> argument). Uses pip-audit's own
    /// vocabulary — bare <c>PYSEC-</c>/<c>CVE-</c>/<c>GHSA-</c> ids, matching
    /// aliases — for tool-side suppression. Prefer the shared
    /// <c>ExcludedRules</c> knob for auditor-side filtering; both use the
    /// same bare-id vocabulary.
    /// </summary>
    public const string IgnoreVulnsKey = "IgnoreVulns";

    private static readonly IReadOnlySet<string> AllowedServices =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "osv", "pypi", "esms" };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran clean (manifest with empty vulns); 1 = ran with findings
        // OR failed outright (_fatal also exits 1). The JSON manifest on
        // stdout is the discriminator, enforced by the parser: exit 1
        // without a parseable manifest fails closed as infrastructure.
        // Exit 2 (argparse misuse) and everything else is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _requirements = static () => [];
    private Func<string?> _projectPath = static () => null;
    private Func<string?> _vulnerabilityService = static () => null;
    private Func<bool> _local = static () => false;
    private Func<IReadOnlyList<string>> _ignoreVulns = static () => [];

    /// <inheritdoc />
    public override string Name => "codeybox:pip-audit";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "pip-audit";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PipAuditJsonOutputParser();

    /// <summary>
    /// Declared mapping from pip-audit's severity vocabulary to
    /// <see cref="AuditSeverity"/>. pip-audit's JSON report currently emits
    /// no per-vulnerability severity, so the parser supplies null and every
    /// confirmed CVE takes the declared default,
    /// <see cref="AuditSeverity.Error"/> — a known vulnerability with no
    /// severity signal fails the audit, matching pip-audit's own
    /// exit-1-on-any-vuln gate. <c>high</c>/<c>medium</c>/<c>low</c> map the
    /// same way every other auditor maps them, so a report that does carry
    /// those tokens is not a unique dialect. Raw tool tokens never reach
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
            ["negligible"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var service = _vulnerabilityService()?.Trim();
        if (!string.IsNullOrWhiteSpace(service) && !AllowedServices.Contains(service))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an invalid {VulnerabilityServiceKey} "
                + $"('{TruncateForMessage(service)}'); set CodeyBox:Plugins:{PluginId}:{VulnerabilityServiceKey} "
                + "to osv, pypi, or esms.")
            { IsDeterministic = true };

        var args = new List<string>
        {
            // The parser reads the JSON manifest from stdout; any other
            // format is a loud run failure, never a pass.
            "--format", "json",
            // The spinner writes control sequences to the captured streams;
            // keep them out of the report.
            "--progress-spinner", "off",
        };

        if (!string.IsNullOrWhiteSpace(service))
        {
            args.Add("--vulnerability-service");
            args.Add(service.ToLowerInvariant());
        }

        if (_local())
            args.Add("--local");

        foreach (var vuln in _ignoreVulns()
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v.Trim()))
        {
            args.Add("--ignore-vuln");
            args.Add(vuln);
        }

        var requirements = _requirements()
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => r.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var projectPath = _projectPath()?.Trim();
        if (requirements.Count > 0)
        {
            foreach (var requirement in requirements)
            {
                args.Add("-r");
                args.Add(requirement);
            }
        }
        else if (!string.IsNullOrWhiteSpace(projectPath))
        {
            args.Add(projectPath);
        }
        else
        {
            args.Add("-r");
            args.Add(DefaultRequirements);
        }

        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _requirements = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[RequirementsKey]);
        _projectPath = () => scoped[ProjectPathKey];
        _vulnerabilityService = () => scoped[VulnerabilityServiceKey];
        _local = () => bool.TryParse(scoped[LocalKey], out var local) && local;
        _ignoreVulns = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[IgnoreVulnsKey]);
        context.Logger.LogInformation(
            "PipAuditAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private const int MessageValueMaxChars = 64;

    private static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }
}
