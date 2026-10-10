using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PipAuditAuditorPlugin;

/// <summary>
/// Dependency vulnerability auditor wrapping <c>pip-audit</c> (the Python
/// Packaging Authority's scanner for known vulnerabilities in Python
/// dependencies, queried against PyPI or OSV) on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the pip-audit-specific
/// arguments and knobs below, and the <c>PIP_AUDIT_OUTPUT</c> environment
/// removal that keeps the JSON report on stdout where the parser reads it.
///
/// <para><b>Gate behaviour: blocking (every reported vulnerability fails
/// the audit).</b> pip-audit reports no per-vulnerability severity, so
/// there is nothing to grade on: every reported CVE maps to
/// <see cref="AuditSeverity.Error"/> through the declared mapping default —
/// the same verdict pip-audit itself applies when it exits non-zero on any
/// match. Skipped dependencies (the parser's synthesized
/// <c>skipped</c> token) map to <see cref="AuditSeverity.Warning"/>:
/// advisory coverage gaps, never blocking. <c>MinimumSeverity</c> only
/// drops findings, it never raises them.</para>
///
/// <para><b>Exit-code convention (verified against pip-audit 2.10.1's
/// documented codes and its <c>_cli.py</c> source).</b> pip-audit does NOT
/// follow a "1 = findings, other non-zero = could not run" convention in a
/// way the exit code alone can express: <c>0</c> means "no known
/// vulnerabilities", but <c>1</c> means EITHER "one or more vulnerabilities
/// found" OR a fatal run failure (<c>_fatal</c> — an unresolvable
/// requirements file, no supported project file at the target, a dead
/// vulnerability service — also exits <c>1</c>). The discriminator is the
/// JSON manifest on stdout: the formatter writes it on every completed
/// run (JSON is a manifest format, so even a clean run emits
/// <c>{"dependencies": [...]}</c>), while a fatal failure emits only a
/// stderr diagnostic. The parser therefore fails closed as infrastructure
/// when stdout does not carry a pip-audit manifest — an exit-<c>1</c>
/// fatal error can never pass as a clean audit, and can never surface as
/// findings either. <c>2</c> is argparse usage errors,
/// <c>126</c>/<c>127</c> cannot-execute. Only <c>0</c> and <c>1</c> are
/// declared findings-producing.</para>
///
/// <para><b>Stream note.</b> The JSON report goes to <b>stdout</b>
/// (<c>--format json</c>); the human-readable summary ("Found N known
/// vulnerabilities…"/"No known vulnerabilities found") goes to stderr and
/// is retained in the raw output. The dedicated
/// <see cref="PipAuditJsonParser"/> reads stdout.</para>
///
/// <para><b>Version pin.</b> pip-audit's resolution and matching change
/// between releases, so findings are only meaningful from the build the
/// auditor was verified against. The auditor probes <c>pip-audit
/// --version</c> before every scan; a missing binary, an unrecognised
/// version string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a
/// finding.</para>
///
/// <para><b>Repository-controlled suppression: none exists.</b> pip-audit
/// takes no <c>--config</c> flag and loads no suppression or policy file
/// from the audited repository — the only finding-level control is the
/// operator-owned <c>--ignore-vuln</c> CLI flag, which this auditor derives
/// from the operator's scoped <c>ExcludedRules</c>, never from repo
/// content. The dependency manifests themselves
/// (<c>pyproject.toml</c>, requirements files) are the audit subject: they
/// define the version set under audit, and weakening them is visible in
/// the diff. Note that requirement files may carry resolver directives
/// (<c>--index-url</c>, <c>--extra-index-url</c>, <c>--find-links</c>)
/// that steer where pip-audit resolves packages from; operators auditing
/// untrusted trees should review those lines or pass <c>--no-deps</c> with
/// fully-pinned hashed requirements via <c>ExtraArguments</c>.</para>
///
/// <para><b>Scope and defaults.</b> The default scan target is the worktree
/// root as a project path (<c>pip-audit … .</c>), which audits the
/// <c>pyproject.toml</c> project (or <c>pylock.*.toml</c> lockfiles with
/// operator-supplied <c>--locked</c>). pip-audit resolves the declared
/// version set — it does not walk the file tree — so vendored or generated
/// trees are never reported and there is no vendored-code noise to exclude:
/// <c>ExcludePaths</c> defaults to empty, and findings carry package
/// identities (<c>name@version</c> in the title and description) rather
/// than file locations, because the tool supplies none. Repositories that
/// declare dependencies only through requirements files (no supported
/// project file at the root — pip-audit fails such a scan loudly) set
/// <c>RequirementsFiles</c> to audit those files with repeatable
/// <c>-r</c> instead. Auditing the sandbox's installed environment
/// (<c>--local</c>) is out of scope: the audit subject is the worktree,
/// not the baseline image.</para>
///
/// <para><b>Network.</b> pip-audit queries the configured vulnerability
/// service (default PyPI) and resolves the audited dependencies through
/// the package index, so the auditor declares
/// <see cref="AuditCapabilities.Network"/> — the service and index hosts
/// must be in the deployment's <c>AuditToolAllowedHosts</c> egress list.
/// There is no offline mode: fully offline deployments cannot run this
/// auditor and should leave it disabled.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: pip-audit Python Dependency Vulnerabilities",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "pip-audit",
    InstallHint = "provision the pinned pip-audit release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `python3 -m pip install "
        + "pip-audit==" + DefaultExpectedVersion + "` (Python 3.10 or newer with pip) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; no distro apt package carries a pinned pip-audit")]
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
    /// Scoped-config key for requirements files under audit
    /// (comma-separated repo-relative paths, repeatable <c>-r</c>). Unset
    /// audits the worktree root as a project path (<c>pyproject.toml</c>);
    /// set it for repositories whose dependencies live only in requirements
    /// files. Entries must stay inside the worktree — absolute paths and
    /// <c>..</c> segments are a deterministic configuration failure.
    /// </summary>
    public const string RequirementsFilesKey = "RequirementsFiles";

    /// <summary>
    /// Scoped-config key for the vulnerability service (<c>-s</c>):
    /// <c>pypi</c> (default), <c>osv</c>, or <c>esms</c>. Any other value is
    /// a deterministic configuration failure. The service is always passed
    /// explicitly so a future upstream default change cannot move the
    /// verdict source silently.
    /// </summary>
    public const string VulnerabilityServiceKey = "VulnerabilityService";

    /// <summary>Default vulnerability service: PyPI.</summary>
    public const string DefaultVulnerabilityService = "pypi";

    // pip-audit's -s choices, canonical lower-case spellings. Lookup is
    // case-insensitive for operator convenience; the emitted argv always
    // carries the canonical spelling because argparse matches choices
    // case-sensitively.
    private static readonly Dictionary<string, string> KnownVulnerabilityServices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pypi"] = "pypi",
            ["osv"] = "osv",
            ["esms"] = "esms",
        };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against pip-audit 2.10.1: 0 = ran, no vulnerabilities;
        // 1 = EITHER ran and matched (JSON manifest on stdout) OR a fatal
        // run failure (stderr diagnostic only — the parser fails closed, so
        // it can never pass or surface as findings). 2 = usage errors,
        // 126/127 cannot-execute — all infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Resolution-based matching reports package identities, never file
        // paths, and never walks vendored trees — there is no
        // vendored-code noise to exclude by default. Operators narrow by
        // rule id (IncludedRules/ExcludedRules) instead.
        ExcludePaths = [],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _requirementsFiles = static () => [];
    private Func<string?> _vulnerabilityService = static () => null;

    /// <inheritdoc />
    public override string Name => "codeybox:pip-audit";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "pip-audit";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new PipAuditJsonParser();

    /// <summary>
    /// Declared mapping from pip-audit's (absent) severity vocabulary to
    /// <see cref="AuditSeverity"/>. pip-audit 2.10.1 reports no
    /// per-vulnerability severity, so real findings arrive with a null tool
    /// level and take the mapping default
    /// (<see cref="AuditSeverity.Error"/> — any reported CVE blocks, the
    /// same verdict pip-audit itself applies). The shared default token set
    /// is reused so a future report shape carrying qualitative levels maps
    /// exactly like every other auditor's; the parser's synthesized
    /// <c>skipped</c> token for unaudited dependencies maps to
    /// <see cref="AuditSeverity.Warning"/> (advisory coverage gap). Raw
    /// tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } = BuildSeverityMapping();

    private static ExternalToolSeverityMapping BuildSeverityMapping()
    {
        var levels = new Dictionary<string, AuditSeverity>(
            ExternalToolSeverityMapping.Default.Levels, StringComparer.OrdinalIgnoreCase)
        {
            [PipAuditJsonParser.SkippedDependencyToken] = AuditSeverity.Warning,
        };
        return new ExternalToolSeverityMapping(levels, AuditSeverity.Error);
    }

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        RejectReservedExtraArguments(options);

        var args = new List<string>
        {
            "--format", "json",
            // Pinned explicitly (auto would already resolve to on for
            // JSON): the parser reads aliases and descriptions, and a
            // future default change must not shrink the report silently.
            "--desc", "on",
            "--aliases", "on",
            // Keeps progress chatter off the captured streams.
            "--progress-spinner", "off",
            // Always explicit: the verdict source must be the operator's
            // scoped choice, never a future upstream default.
            "--vulnerability-service", ValidatedService(_vulnerabilityService()),
        };

        // The tool matches the primary id and every alias when ignoring
        // (has_any_id), so ExcludedRules suppress CVE aliases here even
        // though the shared post-filter only sees the primary id. The
        // post-filter stays as the backstop.
        foreach (var rule in options.ExcludedRules)
        {
            if (string.IsNullOrWhiteSpace(rule))
                continue;
            args.Add("--ignore-vuln");
            args.Add(ValidatedArgumentValue(rule.Trim(), "ExcludedRules"));
        }

        var requirements = ValidatedRequirements(_requirementsFiles());
        if (requirements.Count == 0)
        {
            args.Add(".");
        }
        else
        {
            foreach (var requirement in requirements)
            {
                args.Add("-r");
                args.Add(requirement);
            }
        }

        return args;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolEnvironmentRemovals(
        ExternalToolAuditorOptions options)
    {
        // PIP_AUDIT_OUTPUT would redirect the JSON report off stdout into a
        // file the parser never reads (a silent verdict loss that only
        // surfaces as a parse failure); every other PIP_AUDIT_* knob the
        // tool honors from the environment is already pinned by an explicit
        // CLI flag, which argparse prefers over the environment.
        // Author-chosen constant, never untrusted data.
        return ["PIP_AUDIT_OUTPUT"];
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _requirementsFiles = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[RequirementsFilesKey]);
        _vulnerabilityService = () => scoped[VulnerabilityServiceKey];
        context.Logger.LogInformation(
            "PipAuditAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static string ValidatedService(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultVulnerabilityService;
        if (KnownVulnerabilityServices.TryGetValue(configured.Trim(), out var canonical))
            return canonical;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor 'codeybox:pip-audit' was configured with an unknown "
            + $"{VulnerabilityServiceKey} '{TruncateForMessage(configured)}' — expected one of "
            + "'pypi', 'osv', 'esms'. Set CodeyBox:Plugins:{PluginId}:{VulnerabilityServiceKey} "
            + "to a supported service.")
        { IsDeterministic = true };
    }

    private static List<string> ValidatedRequirements(IReadOnlyList<string> entries)
    {
        var validated = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            validated.Add(ValidatedRepoRelativeTarget(entry.Trim(), RequirementsFilesKey));
        }
        return validated;
    }

    private static void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        // Each reserved flag would redirect the report sink, reshape the
        // report the parser reads, retarget the scan, mutate the worktree,
        // or silently replace a scoped-config knob. Every arm names the
        // knob to use instead; the failure is deterministic so a bad
        // operator edit fails the audit loudly instead of hollowing it.
        var reservations = new (string Flag, string? Short, string Reason)[]
        {
            ("--format", "-f", "the auditor pins '--format json' for its report parser"),
            ("--output", "-o", "the report must stay on stdout where the parser reads it"),
            ("--fix", null, "'--fix' upgrades dependencies in place and must never run in an audit"),
            ("--dry-run", "-d", "'--dry-run' skips the auditing step, leaving no verdict"),
            ("--requirement", "-r", $"scan targets come from '{RequirementsFilesKey}'"),
            ("--local", "-l", "the audit subject is the worktree, not the sandbox's installed environment"),
            ("--vulnerability-service", "-s", $"the verdict source comes from '{VulnerabilityServiceKey}'"),
        };
        foreach (var (flag, shortFlag, reason) in reservations)
        {
            var supplied = shortFlag is null
                ? ExtraArgumentsSupplyFlag(options, flag)
                : ExtraArgumentsSupplyFlag(options, flag, shortFlag);
            if (supplied)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor 'codeybox:pip-audit' ExtraArguments must not supply "
                    + $"'{flag}' — {reason}.")
                { IsDeterministic = true };
        }
    }
}
