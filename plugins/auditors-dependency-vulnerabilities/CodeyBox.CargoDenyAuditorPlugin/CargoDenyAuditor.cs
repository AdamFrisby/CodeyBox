using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.CargoDenyAuditorPlugin;

/// <summary>
/// Dependency auditor wrapping <c>cargo-deny</c> (Rust dependency, licence,
/// and source policy) on the shared <see cref="ExternalToolAuditorBase"/>: the
/// base supplies sandboxed invocation with a bounded timeout, per-stream
/// output caps, exit-code classification, severity mapping, finding identity,
/// and per-auditor configuration. This class adds the NDJSON stderr parser
/// (<see cref="CargoDenyJsonOutputParser"/>), the pinned tool-version
/// declaration via <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// repository-exceptions gate via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, and the
/// cargo-deny-specific arguments and knobs below.
///
/// <para><b>Gate behaviour: severity-driven.</b> Diagnostics the policy marks
/// denied carry severity <c>error</c> and map to
/// <see cref="AuditSeverity.Error"/> — they fail the audit; warn-level lints
/// map to <see cref="AuditSeverity.Warning"/> and are advisory. What is denied
/// versus warned is decided by the <c>deny.toml</c> in force — the audited
/// repository's by default, or an operator-pinned policy via
/// <c>ConfigPath</c>. <c>MinimumSeverity</c> only drops findings, it never
/// raises them.</para>
///
/// <para><b>Exit-code convention (verified against cargo-deny 0.20.x
/// source).</b> cargo-deny does NOT follow the common "1 = findings, 2 =
/// could not run" convention: <c>check</c> exits with a <em>bitset</em> of the
/// checks that produced errors — advisories <c>0x1</c>, bans <c>0x2</c>,
/// licenses <c>0x4</c>, sources <c>0x8</c> — so exits 0–15 are
/// findings-producing. But every run failure (bad flag, unparseable
/// <c>deny.toml</c>, failed <c>cargo metadata</c>, unreachable advisory
/// database) also exits <c>1</c> via <c>anyhow</c>. The discriminator is the
/// <c>{"type":"summary"}</c> record cargo-deny writes to stderr only when the
/// check completes: a findings-producing exit without a summary is a run
/// failure, and the parser fails closed as infrastructure. Diagnostics are
/// emitted only from the final print phase, so they can never precede a
/// bail-out. Clap usage errors exit <c>2</c>, Rust panics exit <c>101</c>,
/// <c>126</c>/<c>127</c> cannot-execute — all infrastructure.</para>
///
/// <para><b>Stream note.</b> In <c>--format json</c> mode cargo-deny writes
/// everything — log records, diagnostics, and the summary — to
/// <b>stderr</b>; stdout stays empty. The parser reads stderr.</para>
///
/// <para><b>Version pin.</b> cargo-deny's lint set and diagnostic vocabulary
/// change between releases, so findings are only meaningful from the build
/// the auditor was verified against. The auditor probes
/// <c>cargo-deny --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> The audited repository's
/// <c>deny.toml</c> (or <c>.deny.toml</c>/<c>.cargo/deny.toml</c>/<c>.config/deny.toml</c>)
/// is the policy contract under audit and is honored by default — weaken it
/// and the audit reports against the weakened policy, which is visible in
/// the diff; an operator who needs an org-fixed policy sets
/// <c>ConfigPath</c>. The exceptions files cargo-deny loads alongside —
/// <c>deny.exceptions.toml</c>, <c>.deny.exceptions.toml</c>,
/// <c>.cargo/deny.exceptions.toml</c> beside the manifest — are a
/// suppression surface that adds exceptions on top of the policy, so their
/// presence at the worktree root (or beside a configured
/// <c>ManifestPath</c>) fails closed as infrastructure by default; operators
/// who trust repo-authored exceptions set
/// <c>TrustRepositorySuppression</c>.</para>
///
/// <para><b>Scope and defaults.</b> cargo-deny's subject is the dependency
/// graph rooted at the manifest, not a file tree, so the default scope is
/// <c>cargo metadata</c> at the worktree root for all four checks
/// (advisories, bans, licenses, sources). Vendored dependencies are the
/// subject of the sources check, not noise, so no paths are excluded by
/// default — and cargo-deny's JSON diagnostics carry no file paths, so
/// <c>ExcludePaths</c> currently has nothing to match. Non-Rust
/// repositories fail loudly: <c>cargo metadata</c> cannot run, the run is
/// infrastructure, never a pass. Inclusion graphs are hidden by default to
/// bound per-diagnostic output; <c>InclusionGraphs</c> re-enables them.</para>
///
/// <para><b>Network.</b> The advisories check fetches the configured
/// advisory databases (the RustSec advisory-db by default) and
/// <c>cargo metadata</c> may reach the registry index, so the auditor
/// declares <see cref="AuditCapabilities.Network"/> — the hosts must be in
/// the deployment's <c>AuditToolAllowedHosts</c> egress list. Fully offline
/// deployments pre-seed the advisory database (and cargo cache) into the
/// baseline and set <c>Offline</c>; the declared capability permits egress,
/// it does not force it.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: cargo-deny Rust Dependency Policy",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "cargo-deny",
    InstallHint = "provision the pinned cargo-deny release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — `cargo install cargo-deny "
        + "--locked --version " + DefaultExpectedVersion + "` or the versioned upstream GitHub "
        + "release binary via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or "
        + "ExecutableProvisions; no distro apt package carries cargo-deny")]
[CodeyBoxPluginRequiresTool(
    "cargo",
    InstallHint = "cargo-deny invokes `cargo metadata` to build the crate graph — provision the "
        + "Rust toolchain into the sandbox baseline; not needed when the operator supplies "
        + "pre-generated metadata via the MetadataPath scoped config key")]
public sealed class CargoDenyAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.cargo-deny";

    /// <summary>
    /// cargo-deny release the invocation and its report are verified against.
    /// Operators running a different pinned build set <c>ExpectedVersion</c>
    /// in the plugin's scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.20.2";

    /// <summary>
    /// Scoped-config key for an explicit cargo-deny configuration file
    /// (<c>--config</c>, a root-level flag). Set it to pin an operator-owned
    /// policy; unset, cargo-deny resolves the audited repository's own
    /// <c>deny.toml</c> chain.
    /// </summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for <c>--manifest-path</c> — the <c>Cargo.toml</c>
    /// the crate graph is rooted at. Unset → <c>&lt;worktree&gt;/Cargo.toml</c>.
    /// </summary>
    public const string ManifestPathKey = "ManifestPath";

    /// <summary>
    /// Scoped-config key for <c>--metadata-path</c>: a pre-generated
    /// <c>cargo metadata</c> JSON document. When set, cargo-deny does not
    /// invoke <c>cargo</c> at all.
    /// </summary>
    public const string MetadataPathKey = "MetadataPath";

    /// <summary>
    /// Scoped-config key selecting which checks run (comma-separated;
    /// cargo-deny's positional <c>[WHICH...]</c> arguments):
    /// <c>advisories</c>, <c>bans</c>, <c>licenses</c>, <c>sources</c>,
    /// <c>all</c>. Unset → all four.
    /// </summary>
    public const string ChecksKey = "Checks";

    /// <summary>
    /// Scoped-config boolean for <c>--offline</c>: no network access of any
    /// kind — requires the advisory databases and the cargo cache to be
    /// pre-provisioned in the baseline.
    /// </summary>
    public const string OfflineKey = "Offline";

    /// <summary>
    /// Scoped-config boolean for <c>--locked</c>: assert the committed
    /// <c>Cargo.lock</c> is unchanged by metadata resolution.
    /// </summary>
    public const string LockedKey = "Locked";

    /// <summary>
    /// Scoped-config key for platform filters (comma-separated target
    /// triples; repeatable <c>-t/--target</c>). cargo-deny validates triples
    /// itself; a bad value is a loud run failure, not a pass.
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config boolean controlling whether diagnostics carry inverse
    /// dependency graphs (<c>graphs</c>). Default false — the graphs are
    /// large; the auditor passes <c>--hide-inclusion-graph</c>.
    /// </summary>
    public const string InclusionGraphsKey = "InclusionGraphs";

    /// <summary>
    /// Scoped-config key opting in to repository-authored exceptions files
    /// (<c>deny.exceptions.toml</c> and its dot variants). Default false: the
    /// audited repo must not be able to weaken the dependency policy it is
    /// audited against.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    private static readonly string[] RepositoryExceptionsFiles =
    [
        "deny.exceptions.toml",
        ".deny.exceptions.toml",
        ".cargo/deny.exceptions.toml",
    ];

    private static readonly IReadOnlySet<string> AllowedChecks =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "advisories", "bans", "licenses", "sources", "all",
        };

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // cargo-deny's check exit code is a bitset of checks that produced
        // errors — advisories 0x1, bans 0x2, licenses 0x4, sources 0x8 — so
        // every value in 0..15 is findings-producing. The exit code is
        // ambiguous on its own (run failures also exit 1): the summary record
        // on stderr is the discriminator, enforced by the parser. Everything
        // outside 0–15 is infrastructure.
        FindingsExitCodes = new HashSet<int>(Enumerable.Range(0, 16)),
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<string?> _manifestPath = static () => null;
    private Func<string?> _metadataPath = static () => null;
    private Func<IReadOnlyList<string>> _checks = static () => [];
    private Func<bool> _offline = static () => false;
    private Func<bool> _locked = static () => false;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<bool> _inclusionGraphs = static () => false;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:cargo-deny";

    /// <inheritdoc />
    public override AuditCapabilities Required => AuditCapabilities.Network;

    /// <inheritdoc />
    protected override string ToolName => "cargo-deny";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new CargoDenyJsonOutputParser();

    /// <summary>
    /// Declared mapping from cargo-deny's severity vocabulary (codespan
    /// <c>error</c>/<c>warning</c>/<c>note</c>/<c>help</c>/<c>bug</c>) to
    /// <see cref="AuditSeverity"/>. Denied lints (<c>error</c>) and
    /// <c>bug</c> — cargo-deny reporting an internal anomaly — map to
    /// <see cref="AuditSeverity.Error"/>; warnings are advisory; allowed-lint
    /// <c>note</c>/<c>help</c> records map to <see cref="AuditSeverity.Info"/>.
    /// Unknown levels default to <see cref="AuditSeverity.Warning"/>. Raw
    /// tool tokens never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["bug"] = AuditSeverity.Error,
            ["warning"] = AuditSeverity.Warning,
            ["note"] = AuditSeverity.Info,
            ["help"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var checks = _checks()
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var invalid = checks.Where(c => !AllowedChecks.Contains(c)).ToList();
        if (invalid.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has invalid Checks entries "
                + $"('{string.Join("', '", invalid)}'); set CodeyBox:Plugins:{PluginId}:{ChecksKey} "
                + "to a comma-separated subset of advisories, bans, licenses, sources, all.")
            { IsDeterministic = true };

        // Root options precede the subcommand — clap parses them only there.
        // ExtraArguments (appended after `check` by the shared base) reach
        // only the check subcommand's flags and positional WHICH names.
        var args = new List<string>
        {
            "--format", "json",
            // warn is the threshold that emits every reportable diagnostic —
            // note/help (allowed lints) stay suppressed, warnings and errors
            // stream as NDJSON records on stderr.
            "--log-level", "warn",
        };

        if (_offline())
            args.Add("--offline");
        if (_locked())
            args.Add("--locked");

        AddPathFlag(args, "--config", _configPath());
        AddPathFlag(args, "--manifest-path", _manifestPath());
        AddPathFlag(args, "--metadata-path", _metadataPath());

        foreach (var target in _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim()))
        {
            args.Add("--target");
            args.Add(target);
        }

        args.Add("check");

        if (!_inclusionGraphs())
            args.Add("--hide-inclusion-graph");

        args.AddRange(checks);
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _manifestPath = () => scoped[ManifestPathKey];
        _metadataPath = () => scoped[MetadataPathKey];
        _checks = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[ChecksKey]);
        _offline = () => bool.TryParse(scoped[OfflineKey], out var offline) && offline;
        _locked = () => bool.TryParse(scoped[LockedKey], out var locked) && locked;
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _inclusionGraphs = () =>
            bool.TryParse(scoped[InclusionGraphsKey], out var graphs) && graphs;
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "CargoDenyAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// cargo-deny-specific precondition on the live path: cargo-deny layers
    /// exceptions files (<c>deny.exceptions.toml</c> and dot variants) found
    /// beside the manifest on top of the configured policy — a
    /// repo-authored file that silently weakens the policy under audit.
    /// Unless the operator opted in via
    /// <see cref="TrustRepositorySuppressionKey"/>, their presence at the
    /// worktree root — or beside a configured <see cref="ManifestPathKey"/>
    /// — fails closed as infrastructure before the scan runs.
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

        // cargo-deny resolves exceptions files walking UP from the manifest
        // directory to the filesystem root, so every ancestor directory of a
        // configured ManifestPath inside the worktree is a load site — probe
        // them all (paths above the worktree are operator territory).
        var candidates = new List<string>(RepositoryExceptionsFiles);
        foreach (var dir in ManifestAncestors(_manifestPath()))
        {
            var prefix = dir + "/";
            foreach (var file in RepositoryExceptionsFiles)
                candidates.Add(prefix + file);
        }

        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox, workingDirectory, tool, candidates, options, ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found repository-controlled exceptions "
                + $"file(s) '{string.Join("', '", present)}' in the audited repository — "
                + "cargo-deny layers them on top of the configured policy, so the audit subject "
                + "could weaken the dependency policy it is audited against. Remove the file(s), "
                + $"or set CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey} to true to "
                + "trust repository-authored exceptions.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Every repository-relative ancestor directory of a configured manifest
    /// path, deepest first — the load sites cargo-deny walks when resolving
    /// exceptions files. Empty when <c>ManifestPath</c> is unset, rooted at
    /// the worktree, absolute, or contains <c>..</c> segments — the probe
    /// only understands in-worktree relative paths; escapes outside the
    /// worktree are operator territory and documented as such.
    /// </summary>
    private static IEnumerable<string> ManifestAncestors(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            yield break;
        var normalized = manifestPath.Trim().Replace('\\', '/').Trim('/');
        if (manifestPath.Trim().StartsWith('/')
            || normalized.Length == 0
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            yield break;
        var lastSlash = normalized.LastIndexOf('/');
        while (lastSlash >= 0)
        {
            var directory = normalized[..lastSlash].TrimEnd('/');
            if (directory.Length == 0)
                yield break;
            yield return directory;
            lastSlash = directory.LastIndexOf('/');
            normalized = directory;
        }
    }

    private static void AddPathFlag(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(flag);
        args.Add(value.Trim());
    }
}
