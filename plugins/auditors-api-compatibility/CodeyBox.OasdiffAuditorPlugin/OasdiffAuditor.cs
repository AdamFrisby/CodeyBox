using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.OasdiffAuditorPlugin;

/// <summary>
/// API-compatibility auditor wrapping <c>oasdiff</c> (OpenAPI breaking-change
/// detection) on the shared <see cref="ExternalToolAuditorBase"/>: the base
/// supplies sandboxed invocation with a bounded timeout, per-stream output
/// caps, output parsing, severity mapping, exit-code classification, and
/// per-auditor configuration. This class adds the
/// <see cref="OasdiffBreakingFilesReportParser"/> for oasdiff's per-spec JSON
/// report, the pinned tool-version declaration via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, baseline and spec-scope
/// resolution for the audit's base branch, and the repository-config gate
/// below.
///
/// <para><b>Gate behaviour: blocking on ERR-level findings.</b> The auditor
/// runs <c>oasdiff breaking-files</c>, which reports upstream <c>ERR</c>
/// (definite breaking changes) and <c>WARN</c> (potential breaking changes)
/// levels only. The declared severity mapping sends <c>ERR</c> to
/// <see cref="AuditSeverity.Error"/> (fails the audit) and <c>WARN</c> to
/// <see cref="AuditSeverity.Warning"/> (advisory). Raw tool levels never
/// reach findings.</para>
///
/// <para><b>Exit-code convention (verified against oasdiff v1.32.x source —
/// not the common 0/1/2 convention).</b> <c>0</c> = every spec compared clean
/// or was newly added and skipped; <c>1</c> = the run completed and at least
/// one spec carries changes at or above the <c>--fail-on</c> level (this
/// auditor passes <c>WARN</c>, so 1 means "found something"). Failures are
/// typed <c>ReturnError</c>s with their own codes — 100 general execution
/// error, 101 invalid flags, 102 spec load failure, 103 glob load failure,
/// 104 diff failure, 105 print failure, 106 severity-levels file error,
/// 107 config-file problem, 110 unsupported format, 111 template error,
/// 114 invalid color mode, 121 ignore-file error, 122 flatten error,
/// 123 disallowed external $ref — and <c>126</c>/<c>127</c> are
/// cannot-execute / not-found. Everything except 0 and 1 is infrastructure,
/// never a verdict.</para>
///
/// <para><b>Baseline resolution.</b> A breaking-change check needs the "old"
/// API to compare against. Unless the operator pins a ref explicitly
/// (<see cref="BaseRefKey"/> scoped key or a <c>--base</c> flag in
/// <c>ExtraArguments</c>), the shared
/// <see cref="ExternalToolAuditorBase.ResolveMergeBaseAsync"/> helper resolves
/// the merge-base of <c>HEAD</c> and the work item's
/// <see cref="AuditContext.BaseBranch"/> (probing <c>origin/&lt;base&gt;</c>
/// first, then the bare branch name) — the same three-dot semantics the
/// pipeline's diff auditors use — and the auditor passes it to
/// <c>breaking-files --base</c>. An invalid base branch, an unresolvable
/// ref, or no common ancestor is a deterministic infrastructure failure —
/// never a pass. Resolution needs the <see cref="AuditContext"/> that
/// <c>BuildToolArguments</c> does not receive, so it runs inside the base's
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync"/> seam.</para>
///
/// <para><b>Spec scope.</b> Each audited OpenAPI document is one positional
/// <c>breaking-files</c> argument: an explicit <see cref="SpecPathsKey"/>
/// list when configured, otherwise every tracked or untracked
/// (non-gitignored) worktree file whose basename contains
/// <c>openapi</c> or <c>swagger</c> with a <c>.yaml</c>/<c>.yml</c>/<c>.json</c>
/// extension — oasdiff's own pre-commit convention — minus
/// <c>ExcludePaths</c>. Only regular, non-symlink files inside the worktree
/// are compared: a repo-committed symlink would redirect oasdiff's read
/// outside the audited tree, so the presence probe rejects any spec whose
/// path (leaf or ancestor directory) is a symlink. Specs absent from the
/// base ref are newly added and skipped by the tool itself. Zero resolved
/// specs, an unreadable configured path, or a path that is not a plain
/// relative file (a <c>:</c>-bearing or dash-leading name is rejected by
/// oasdiff's own argument contract) is a deterministic infrastructure
/// failure. A repository with no specs is a misconfiguration this auditor
/// says loudly, not a silent skip.</para>
///
/// <para><b>Version pin.</b> The check catalog, report shape, and exit
/// convention change between releases, so findings are only meaningful from
/// the build the auditor was verified against. <c>oasdiff --version</c> is
/// probed before every scan; a missing binary, an unrecognised version
/// string, or a version other than <c>ExpectedVersion</c> is an
/// infrastructure failure naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> oasdiff auto-loads
/// <c>.oasdiff.{json,yaml,yml,toml,hcl,...}</c> — and the legacy
/// <c>oasdiff.*</c> spellings — from the working directory (the audit
/// worktree root), and such a file can set <c>err-ignore</c>,
/// <c>warn-ignore</c>, <c>severity-levels</c>, <c>fail-on</c>,
/// <c>match-path</c>/<c>unmatch-path</c>, <c>deprecation-days-*</c> and
/// more: the audit subject writes that file, so a repository config could
/// downgrade or silence findings. An auditor its subject can silence is not
/// a gate, so by default the run fails closed as deterministic
/// infrastructure when any oasdiff config file exists at the worktree root.
/// An operator that deliberately trusts repo-authored oasdiff config sets
/// <c>TrustRepositorySuppression</c> in scoped config; an operator-supplied
/// <c>--config</c> via <c>ExtraArguments</c> remains honored either way (it
/// outranks both the env var and the cwd lookup inside oasdiff).</para>
///
/// <para><b>External $refs.</b> The scan passes
/// <c>--allow-external-refs=false</c>: audited specs are untrusted input and
/// an external $ref is an outbound fetch (SSRF) or a read outside the git
/// tree. A spec that needs one fails closed (exit 123 → infrastructure).
/// Operators opt back in through the dedicated
/// <see cref="AllowExternalRefsKey"/> scoped key — never via
/// <c>ExtraArguments</c>, which would bypass the capability declaration:
/// opting in flips <see cref="Required"/> to
/// <see cref="AuditCapabilities.Network"/> so the run is scheduled into a
/// network-capable audit sandbox profile for the fetches it enables.
/// In-repo relative file $refs are unaffected.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: oasdiff OpenAPI Breaking Changes",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "oasdiff",
    InstallHint = "provision the pinned oasdiff release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — the upstream release asset "
        + "oasdiff_" + DefaultExpectedVersion + "_linux_<arch>.tar.gz or `go install "
        + "github.com/oasdiff/oasdiff@v" + DefaultExpectedVersion + "` via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions; "
        + "no distro apt package carries it")]
[CodeyBoxPluginRequiresTool(
    "git",
    InstallHint = "git ships in the stock sandbox baseline; declared here because oasdiff "
        + "reads '<base>:<path>' revisions through git and this auditor resolves the "
        + "merge-base inside the audited clone")]
public sealed class OasdiffAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.oasdiff";

    /// <summary>
    /// oasdiff release the invocation, its exit convention, and its report
    /// shape were verified against. Operators running a different pinned
    /// build set <c>ExpectedVersion</c> in the plugin's scoped config to
    /// match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.32.1";

    /// <summary>
    /// Exit code oasdiff returns when the run completed and changes at or
    /// above <c>--fail-on</c> exist. Disjoint from oasdiff's error
    /// convention (100–123 typed failures, 126/127 cannot-execute), so only
    /// it and 0 are verdicts.
    /// </summary>
    internal const int BreakingChangesFoundExitCode = 1;

    /// <summary>
    /// Scoped-config key pinning the baseline git ref (a branch, tag, SHA —
    /// whatever <c>git show &lt;ref&gt;:&lt;path&gt;</c> resolves) the specs
    /// are compared against, instead of the merge-base default.
    /// </summary>
    public const string BaseRefKey = "BaseRef";

    /// <summary>
    /// Scoped-config key for the explicit, comma-separated list of
    /// repository-relative OpenAPI spec paths to compare. Overrides the
    /// filename-convention discovery.
    /// </summary>
    public const string SpecPathsKey = "SpecPaths";

    /// <summary>
    /// Scoped-config key opting in to repository-authored oasdiff config
    /// (<c>.oasdiff.*</c>/<c>oasdiff.*</c> at the worktree root). Default
    /// false: the audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// Scoped-config key opting in to external <c>$ref</c> resolution
    /// (<c>--allow-external-refs</c>). Default false: audited specs are
    /// untrusted input and an external $ref is an outbound fetch (SSRF) or a
    /// read outside the git tree. When true the auditor also declares
    /// <see cref="AuditCapabilities.Network"/> so the run is scheduled into a
    /// network-capable audit sandbox profile — the flag cannot be smuggled
    /// through <c>ExtraArguments</c> without that capability declaration.
    /// </summary>
    internal const string AllowExternalRefsKey = "AllowExternalRefs";

    private const string BaseFlag = "--base";
    private const string AllowExternalRefsFlag = "--allow-external-refs";

    /// <summary>Upper bound on specs compared in one run — argv growth and runtime both stay bounded.</summary>
    private const int MaxSpecPaths = 200;

    private const int MaxSpecPathChars = 512;
    private const int DiscoveryMaxOutputBytes = 256 * 1024;

    // Discovery follows oasdiff's own filename convention — its shipped
    // pre-commit hook matches openapi.{yaml,yml,json} — plus the swagger.*
    // spelling for OpenAPI 2.0 specs. Intentionally narrow: over-inclusion
    // fails closed (a matched non-spec is a load error, i.e. a loud audit
    // failure), so the default errs toward fewer, surer candidates and
    // SpecPaths covers the rest. git pathspec :(icase,glob) magic matches
    // case-insensitively at any depth; --others --exclude-standard adds
    // untracked-but-not-ignored worktree files.
    private static readonly IReadOnlyList<string> DiscoveryPathspecs =
    [
        ":(icase,glob)**/*openapi*.yaml",
        ":(icase,glob)**/*openapi*.yml",
        ":(icase,glob)**/*openapi*.json",
        ":(icase,glob)**/*swagger*.yaml",
        ":(icase,glob)**/*swagger*.yml",
        ":(icase,glob)**/*swagger*.json",
    ];

    // oasdiff reads <cwd>/.oasdiff.<ext> then <cwd>/oasdiff.<ext> for every
    // extension viper v1.21 registers (json, toml, yaml, yml, properties,
    // props, prop, hcl, tfvars, dotenv, env, ini, json5). The cwd is the
    // audited worktree root, so every spelling is gated — a file the probe
    // misses would silently steer the scan.
    private static readonly IReadOnlyList<string> RepositoryConfigNames = BuildRepositoryConfigNames();

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = every spec clean (or newly added and skipped); 1 = --fail-on
        // WARN tripped, i.e. at least one breaking-change finding. Both emit
        // the per-spec JSON report — both are verdicts. oasdiff's typed
        // ReturnError codes (100–123), usage errors, and 126/127 are all
        // "could not run" — infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, BreakingChangesFoundExitCode },
        // Vendored/dependency spec trees describe upstream contracts, not
        // the change under audit — noise that trains operators to ignore
        // the auditor. Discovery applies the same exclusion before the
        // comparison ever runs. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _baseRef = static () => null;
    private Func<IReadOnlyList<string>> _specPaths = static () => [];
    private Func<bool> _trustRepositorySuppression = static () => false;
    private Func<bool> _allowExternalRefs = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:oasdiff";

    /// <inheritdoc />
    public override AuditCapabilities Required =>
        _allowExternalRefs() ? AuditCapabilities.Network : AuditCapabilities.None;

    /// <inheritdoc />
    protected override string ToolName => "oasdiff";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } =
        new OasdiffBreakingFilesReportParser();

    /// <summary>
    /// Declared mapping from oasdiff's check vocabulary to
    /// <see cref="AuditSeverity"/>: <c>ERR</c> (definite breaking change)
    /// blocks as <see cref="AuditSeverity.Error"/>, <c>WARN</c> (potential
    /// breaking change) is advisory <see cref="AuditSeverity.Warning"/>, and
    /// <c>INFO</c> (non-breaking — unreachable under <c>breaking-files</c>,
    /// mapped anyway) is <see cref="AuditSeverity.Info"/>. Unknown or absent
    /// levels default to Warning; raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["ERR"] = AuditSeverity.Error,
            ["WARN"] = AuditSeverity.Warning,
            ["INFO"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        // The parser's contract is the per-spec JSON report on stdout; an
        // operator overriding --format/-f or --template through
        // ExtraArguments would silently void it (a yaml/text report parses
        // as infrastructure anyway — fail the configuration deterministically
        // instead, before the binary is even probed).
        if (ExtraArgumentsSupplyFlag(options, "--format", "-f", "--template"))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' requires oasdiff's per-spec JSON report on "
                + "stdout — ExtraArguments may not supply --format/-f or --template.")
            { IsDeterministic = true };

        // --allow-external-refs must come through the named scoped key, not
        // raw argv: opting in is also what flips this auditor's Required
        // capabilities to Network, and a verbatim ExtraArguments flag would
        // grant audited specs outbound fetches inside a sandbox profile that
        // was never declared network-capable.
        if (ExtraArgumentsSupplyFlag(options, AllowExternalRefsFlag))
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' does not accept '{AllowExternalRefsFlag}' via "
                + $"ExtraArguments — set CodeyBox:Plugins:{PluginId}:{AllowExternalRefsKey} to true "
                + "instead, which also schedules the audit into a network-capable sandbox profile "
                + "for the outbound $ref fetches it enables.")
            { IsDeterministic = true };

        return
        [
            "breaking-files",
            // Make "found something" distinguishable from "could not run":
            // without --fail-on oasdiff exits 0 even with findings. WARN is
            // the lowest level breaking-files reports, so exit 1 == at least
            // one finding. The exit code only drives classification; the JSON
            // report drives the findings either way.
            "--fail-on", "WARN",
            "--format", "json",
            "--color", "never",
            // Audited specs are untrusted input; an external $ref is an
            // outbound fetch or a read outside the git tree.
            _allowExternalRefs() ? AllowExternalRefsFlag : "--allow-external-refs=false",
        ];
    }

    /// <summary>
    /// Resolves the arguments that need the <see cref="AuditContext"/> or
    /// bounded sandbox probes and so cannot come from
    /// <see cref="BuildToolArguments"/>: the <c>--base</c> pair and the spec
    /// list. Exactly one baseline source wins — a configured
    /// <see cref="BaseRefKey"/>, an operator-supplied <c>--base</c> in
    /// <c>ExtraArguments</c>, or the merge-base of <c>HEAD</c> and
    /// <see cref="AuditContext.BaseBranch"/> resolved via bounded git probes.
    /// The spec list is <see cref="SpecPathsKey"/> when configured, else the
    /// filename-convention discovery; every entry is validated to a plain
    /// relative file path before it can reach argv.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var args = new List<string>();
        var configuredBase = _baseRef();
        var operatorBase = ExtraArgumentsSupplyFlag(options, BaseFlag);
        if (!string.IsNullOrWhiteSpace(configuredBase) && operatorBase)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has --base configured in both "
                + $"CodeyBox:Plugins:{PluginId}:{BaseRefKey} and ExtraArguments — set it "
                + "in exactly one place.")
            { IsDeterministic = true };

        if (!operatorBase)
        {
            // An operator-supplied ExtraArguments --base is appended verbatim
            // by the base class — nothing to emit here.
            args.Add(BaseFlag);
            args.Add(!string.IsNullOrWhiteSpace(configuredBase)
                ? ValidatedBaseRef(configuredBase!, BaseRefKey)
                : await ResolveMergeBaseAsync(
                        sandbox, workingDirectory, context, options, BaselineConfigHint, ct)
                    .ConfigureAwait(false));
        }

        var specs = await ResolveSpecPathsAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);
        args.AddRange(specs);
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _baseRef = () => scoped[BaseRefKey];
        _specPaths = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[SpecPathsKey]);
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        _allowExternalRefs = () =>
            bool.TryParse(scoped[AllowExternalRefsKey], out var allow) && allow;
        context.Logger.LogInformation(
            "OasdiffAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// oasdiff-specific precondition on the live path: unless the operator
    /// opted in, no oasdiff config file (<c>.oasdiff.*</c> or
    /// <c>oasdiff.*</c>) may exist at the audited worktree root — the cwd
    /// lookup would load it, and its ignore/severity/match-path keys are a
    /// suppression surface the audit subject controls. Fails closed as
    /// deterministic infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (_trustRepositorySuppression())
            return;

        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, RepositoryConfigNames, options, ct)
            .ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled config file(s) "
                + $"'{string.Join("', '", present)}' at the audited repository root — oasdiff "
                + "auto-loads '.oasdiff.*'/'oasdiff.*' from its working directory, and keys like "
                + "err-ignore, warn-ignore, severity-levels and unmatch-path would let the audit "
                + "subject silence findings. Remove the file(s), or set "
                + $"CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to trust "
                + "repository-controlled oasdiff configuration.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// The specs to compare: the configured <see cref="SpecPathsKey"/> list
    /// when set, else the filename-convention discovery. Configured paths
    /// are validated and confirmed present as regular files (a
    /// configured-but-absent-or-symlinked path is a deterministic
    /// configuration failure); discovered paths are validated,
    /// <c>ExcludePaths</c>-filtered, deduplicated, and silently intersected
    /// with regular-file presence (a tracked path deleted in the worktree
    /// has no revision side to compare, and a symlinked one would redirect
    /// the tool's read outside the audited tree). Zero results is a
    /// deterministic infrastructure failure — enabling this auditor on a
    /// repository with no OpenAPI specs is a loud misconfiguration, not a
    /// silent pass.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveSpecPathsAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _specPaths();
        if (configured.Count > 0)
        {
            var validated = new List<string>(configured.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in configured)
            {
                var normalized = NormalizeSpecPath(raw);
                if (normalized is null)
                    throw InvalidSpecPath(raw, SpecPathsKey);
                if (seen.Add(normalized))
                    validated.Add(normalized);
            }
            EnforceSpecBound(validated.Count);

            // A configured path absent from the worktree — or present only
            // as a symlink or non-regular file, which would make oasdiff's
            // read escape the audited tree — is a mistyped scope, not a
            // finding: name it instead of letting the tool's load failure
            // say it opaquely mid-scan.
            var present = await ProbeRepositoryRegularFilesPresentAsync(
                sandbox, workingDirectory, ToolName, validated, options, ct)
                .ConfigureAwait(false);
            var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
            var missing = validated.Where(p => !presentSet.Contains(p)).ToList();
            if (missing.Count > 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: auditor '{Name}' configured spec path(s) "
                    + $"'{string.Join("', '", missing)}' do not resolve to regular, non-symlink "
                    + "files inside the audited worktree — "
                    + $"fix CodeyBox:Plugins:{PluginId}:{SpecPathsKey}.")
                { IsDeterministic = true };
            return validated;
        }

        return await DiscoverSpecPathsAsync(sandbox, workingDirectory, options, ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> DiscoverSpecPathsAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var argv = new List<string>
        {
            "git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--",
        };
        argv.AddRange(DiscoveryPathspecs);

        var result = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "spec discovery",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = DiscoveryMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (result.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' spec discovery could not run: the sandbox "
                + "exec transport was unavailable.");
        if (result.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not list candidate spec files "
                + $"(git ls-files exit {result.ExitCode}) — a failed probe is infrastructure, not "
                + $"evidence that no specs exist. Set CodeyBox:Plugins:{PluginId}:{SpecPathsKey} "
                + "to bypass discovery.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        // NUL-separated names: the on-disk name reaches argv unambiguously.
        // Candidates that could never be a breaking-files argument (a ':'- or
        // '-'-leading name is read as a git revision or a flag by the tool)
        // or that the operator excluded are dropped here — discovery is the
        // default scope, and an un-auditable or excluded name can produce no
        // finding regardless.
        var discovered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeSpecPath(entry);
            if (normalized is null || !seen.Add(normalized) || IsPathExcluded(normalized, options))
                continue;
            discovered.Add(normalized);
        }
        EnforceSpecBound(discovered.Count);
        if (discovered.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' found no OpenAPI spec candidates — no worktree "
                + "file matches the default discovery convention (*openapi*.{yaml,yml,json}, "
                + $"*swagger*.{{yaml,yml,json}} outside ExcludePaths). Set "
                + $"CodeyBox:Plugins:{PluginId}:{SpecPathsKey} with an explicit list, or leave this "
                + "auditor disabled on repositories without OpenAPI specs.")
            { IsDeterministic = true };

        // ls-files reports the index; a tracked path deleted in the worktree
        // has no revision side to load, and a symlinked candidate would hand
        // oasdiff a read outside the audited tree — keep only regular,
        // non-symlink files inside the worktree.
        var present = await ProbeRepositoryRegularFilesPresentAsync(
            sandbox, workingDirectory, ToolName, discovered, options, ct)
            .ConfigureAwait(false);
        var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
        var specs = discovered.Where(presentSet.Contains).ToList();
        if (specs.Count == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' found OpenAPI spec candidates in git metadata "
                + "but none resolve to regular, non-symlink files inside the audited worktree — "
                + "nothing can be compared. This is infrastructure, not a pass.")
            { IsDeterministic = true };
        return specs;
    }

    /// <summary>
    /// Normalizes a candidate spec path to the only form
    /// <c>breaking-files</c> accepts — a plain relative file path — or
    /// returns null. oasdiff classifies any argument containing ':' as a
    /// git revision and '-' alone or URL-looking values as non-files, and a
    /// leading dash would parse as a flag; <c>..</c> segments could step
    /// outside the worktree. Backslashes normalize to '/' (a Windows-style
    /// configured path and a Unix filename bearing a literal '\' cannot be
    /// distinguished — the former is by far the common intent, and a
    /// backslash-bearing repo file simply won't match).
    /// </summary>
    private static string? NormalizeSpecPath(string? raw)
    {
        var path = ExternalToolJsonHelpers.NormalizePath(raw);
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.Length == 0 || path.Length > MaxSpecPathChars
            || path[0] == '/' || path[0] == '-'
            || path.IndexOf(':') >= 0
            || path.Any(char.IsControl))
            return null;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return null;
        }
        return path;
    }

    private static void EnforceSpecBound(int count)
    {
        if (count > MaxSpecPaths)
            throw new AuditUnavailableException(
                $"could-not-verify: spec scope resolved to {count} files, exceeding the bound of "
                + $"{MaxSpecPaths} — narrow it via {SpecPathsKey} or ExcludePaths.")
            { IsDeterministic = true };
    }

    private AuditUnavailableException InvalidSpecPath(string? raw, string source)
        => new(
            $"could-not-verify: auditor '{Name}' configured {source} entry "
            + $"'{TruncateForMessage(raw)}' is not a usable spec path — breaking-files accepts only "
            + "plain relative file paths (no leading '/' or '-', no ':' or '..', no control "
            + "characters).")
        { IsDeterministic = true };

    /// <summary>
    /// Validates a configured baseline ref that travels to the tool as an
    /// argv entry (<c>--base &lt;ref&gt;</c>) and onward to
    /// <c>git show &lt;ref&gt;:&lt;path&gt;</c>: bounded length, no leading
    /// dash (oasdiff's own checkRef rejects it too — a ref must never parse
    /// as an option), no whitespace or control characters, and none of the
    /// characters git forbids in refnames or that would split the
    /// ref:path pair. Suffix operators (<c>~</c>, <c>^</c>, <c>@</c>) stay
    /// legal — <c>HEAD~2</c> is a legitimate baseline.
    /// </summary>
    private static string ValidatedBaseRef(string value, string source)
    {
        var trimmed = value.Trim();
        const int maxChars = 256;
        if (trimmed.Length == 0 || trimmed.Length > maxChars
            || trimmed[0] == '-'
            || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.IndexOfAny([':', '?', '*', '[', '\\']) >= 0)
            throw new AuditUnavailableException(
                $"could-not-verify: configured '{source}' is not a usable git ref "
                + "(empty, overlong, leading '-', whitespace/control characters, '..', or "
                + "refname-forbidden characters).")
            { IsDeterministic = true };
        return trimmed;
    }

    private static string BaselineConfigHint
        => $"Set CodeyBox:Plugins:{PluginId}:{BaseRefKey} to a git ref (branch, tag, or SHA), or "
            + $"pass {BaseFlag} <ref> via ExtraArguments, to pin the baseline explicitly.";

    private static IReadOnlyList<string> BuildRepositoryConfigNames()
    {
        string[] extensions =
            ["json", "toml", "yaml", "yml", "properties", "props", "prop", "hcl",
             "tfvars", "dotenv", "env", "ini", "json5"];
        var names = new List<string>(extensions.Length * 2);
        foreach (var stem in new[] { ".oasdiff", "oasdiff" })
            foreach (var ext in extensions)
                names.Add($"{stem}.{ext}");
        return names;
    }
}
