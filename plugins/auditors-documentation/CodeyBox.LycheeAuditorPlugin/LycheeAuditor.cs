using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.LycheeAuditorPlugin;

/// <summary>
/// Documentation auditor wrapping <c>lychee</c> (broken-link checking) on the
/// shared <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and per-auditor
/// configuration. This class adds the lychee JSON report parser
/// (<see cref="LycheeJsonOutputParser"/>), the pinned tool version via
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, the
/// repository-suppression gate via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, and the
/// offline/remote posture below.
///
/// <para><b>Gate behaviour: hybrid / severity-driven.</b> Confirmed link
/// failures (<c>error_map</c> — dead URLs, missing files, missing fragments)
/// map to <see cref="AuditSeverity.Error"/> and fail the audit. Request
/// timeouts (<c>timeout_map</c>) map to <see cref="AuditSeverity.Warning"/> and
/// are advisory only: a timeout is weak evidence of a broken link and should
/// not block on its own. With the default offline scope timeouts cannot
/// occur at all, so the default gate is effectively blocking on every
/// finding.</para>
///
/// <para><b>Exit-code convention (verified against lychee v0.24.x).</b>
/// lychee does NOT follow the common "exit 1 = findings" convention:
/// <c>0</c> = every non-excluded link checked OK; <c>2</c> = link check
/// failures (both emit the JSON report on stdout — both are verdicts);
/// <c>1</c> = missing inputs or any other runtime/configuration error;
/// <c>3</c> = errors in the config file; <c>126</c>/<c>127</c> = cannot
/// execute / not found. Only <c>{0, 2}</c> are declared
/// findings-producing; everything else is infrastructure. A verdict-class
/// exit without a JSON report on stdout still fails closed as infrastructure
/// through the parser.</para>
///
/// <para><b>Version pin.</b> A checker's request behaviour, defaults, and
/// report shape change between releases, so findings are only meaningful from
/// the build the auditor was verified against. The auditor probes
/// <c>lychee --version</c> before the scan; a missing binary, an
/// unrecognised version string, or a version other than
/// <c>ExpectedVersion</c> is an infrastructure failure naming the tool —
/// never a pass, never a finding.</para>
///
/// <para><b>Offline by default — remote links are not checked.</b> The
/// auditor runs under <see cref="AuditCapabilities.None"/> (no credentials, no
/// network) and passes <c>--offline</c>: lychee restricts checking to the
/// <c>file</c> scheme, so relative file links and in-repo fragments are
/// verified while remote <c>http(s)</c>/<c>mailto</c> links are reported as
/// excluded, not as findings. This keeps the audit deterministic — findings
/// never depend on the availability of an external site. Operators who need
/// remote URL checking set <c>CheckRemoteLinks</c>: the auditor then drops
/// <c>--offline</c> and declares <see cref="AuditCapabilities.Network"/>,
/// which provisions network egress for the audit sandbox (subject to the
/// project's audit-tool network profile). Findings in remote mode are
/// inherently non-deterministic.</para>
///
/// <para><b>Repository-controlled suppression.</b> The audit subject writes
/// the repository, and lychee honors surfaces authored inside it: the walker
/// skips input files matched by <c>.gitignore</c>, <c>.ignore</c>, or the
/// global ignore file; a <c>.lycheeignore</c> in the working directory is
/// loaded unconditionally (no flag disables it) and its lines become
/// URL-exclusion regexes; and when no <c>--config</c> is given lychee
/// auto-loads configuration from a repo-root <c>lychee.toml</c> (or a
/// <c>[lychee]</c> section in <c>Cargo.toml</c>/<c>pyproject.toml</c>/
/// <c>package.json</c>). By default the scan neutralizes all three channels:
/// it passes <c>--no-ignore</c> so ignore rules cannot hide committed
/// documentation from the crawl, fails closed as infrastructure when a
/// repo-root <c>.lycheeignore</c> exists (checked through the shared
/// fail-closed file-presence probe), and pins configuration with
/// <c>--config /dev/null</c>, which disables every default config-file
/// lookup (an operator <c>ConfigPath</c> or <c>--config</c> in
/// <c>ExtraArguments</c> takes precedence over the pin).
/// <c>TrustRepositorySuppression</c> opts back in to the repository's own
/// suppression surfaces — it drops <c>--no-ignore</c> and the
/// <c>.lycheeignore</c> gate and lets lychee's default config lookup run.
/// Because <c>--no-ignore</c> also walks gitignored build output, the
/// vendored/generated <c>ExcludePaths</c> defaults double as the crawl
/// boundary via <c>--exclude-path</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>lychee .</c>: the worktree
/// is walked recursively, filtered to lychee's documentation extensions
/// (Markdown, HTML, CSS, XML, plaintext). Hidden files are included
/// (<c>--hidden</c>) because documentation legitimately lives under
/// dot-directories such as <c>.github/</c>; <c>.git/</c> is excluded. Links
/// under vendored (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>,
/// <c>.venv/</c>, <c>venv/</c>) and generated (<c>dist/</c>, <c>build/</c>,
/// <c>out/</c>, <c>coverage/</c>, <c>bin/</c>, <c>obj/</c>, <c>target/</c>)
/// prefixes are excluded at extraction time (<c>--exclude-path</c>, derived
/// from <c>ExcludePaths</c>) and findings under them are dropped as well:
/// problems there belong to upstream packages or build output, not the
/// change under audit — a boundary that matters more under
/// <c>--no-ignore</c>, which no longer lets <c>.gitignore</c> keep such
/// trees out of the walk. <c>--include-fragments</c> checks <c>#anchor</c>
/// targets inside local files — a common real-world docs break. Root-absolute
/// links (<c>/docs/x.md</c>) resolve against <c>RootDirectory</c> when
/// configured; otherwise lychee flags them, which is the honest result — the
/// deploy root is unknowable without configuration.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: Lychee Broken Link Checker",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "lychee",
    InstallHint = "provision the pinned lychee release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries a "
        + "version pin — e.g. 'cargo install lychee --locked --version " + DefaultExpectedVersion
        + "' or the lychee-v" + DefaultExpectedVersion
        + " release tarball via CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class LycheeAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.lychee";

    /// <summary>
    /// lychee release the invocation and its report shape are verified
    /// against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "0.24.2";

    /// <summary>Scoped-config key for an explicit lychee configuration file path.</summary>
    public const string ConfigPathKey = "ConfigPath";

    /// <summary>
    /// Scoped-config key for the lychee inputs (comma-separated files, globs,
    /// or directories). Replaces the default whole-tree <c>.</c> input.
    /// </summary>
    public const string InputsKey = "Inputs";

    /// <summary>
    /// Scoped-config key resolving root-absolute links (<c>/docs/x.md</c>) in
    /// local files. Passed to lychee's <c>--root-dir</c>; must be an absolute
    /// path inside the sandbox.
    /// </summary>
    public const string RootDirectoryKey = "RootDirectory";

    /// <summary>
    /// Scoped-config key opting in to remote link checking. When true the
    /// scan drops <c>--offline</c> and the auditor declares
    /// <see cref="AuditCapabilities.Network"/> so the audit sandbox is
    /// provisioned with network egress.
    /// </summary>
    public const string CheckRemoteLinksKey = "CheckRemoteLinks";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression —
    /// walker ignore files (<c>.gitignore</c>/<c>.ignore</c>),
    /// <c>.lycheeignore</c>, and the default lychee config files
    /// (<c>lychee.toml</c> and friends). Default false: the audited repo must
    /// not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>Synthesized rule id for <c>error_map</c> entries (lychee reports no rule ids).</summary>
    internal const string BrokenLinkRuleId = "lychee/broken-link";

    /// <summary>Synthesized rule id for <c>timeout_map</c> entries.</summary>
    internal const string TimeoutRuleId = "lychee/timeout";

    // Passing any --config disables lychee's default config-file lookup
    // entirely (the default only runs when no --config is given). /dev/null
    // parses as an empty config, pinning built-in defaults so repo-authored
    // lychee.toml / Cargo.toml / pyproject.toml / package.json sections cannot
    // steer the audit. An operator ConfigPath or ExtraArguments --config
    // outranks the pin; TrustRepositorySuppression drops it.
    private const string PinnedEmptyConfigPath = "/dev/null";

    // Loaded unconditionally from the process working directory; there is no
    // flag to redirect or disable it, so presence at the repo root is gated.
    private const string RepositoryIgnoreFile = ".lycheeignore";

    private static ExternalToolAuditorOptions CreateDefaults() => new()
    {
        // 0 = all checked links OK; 2 = link check failures. Both emit the
        // JSON report — both are verdicts. 1 (missing input / runtime /
        // config error), 3 (config-file error), and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Findings in VCS internals, vendored/dependency trees, and generated
        // build output do not describe the change under audit — noise that
        // trains operators to ignore the auditor. Each entry is also passed
        // to lychee as --exclude-path so those files are never crawled; under
        // --no-ignore this list is the only thing keeping gitignored build
        // output out of the walk. Operators re-include a path by overriding
        // ExcludePaths in scoped config.
        ExcludePaths =
        [
            ".git/",
            "vendor/",
            "third_party/",
            "node_modules/",
            ".venv/",
            "venv/",
            "dist/",
            "build/",
            "out/",
            "coverage/",
            "bin/",
            "obj/",
            "target/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = CreateDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _configPath = static () => null;
    private Func<string?> _rootDirectory = static () => null;
    private Func<IReadOnlyList<string>> _inputs = static () => [];
    private Func<bool> _checkRemoteLinks = static () => false;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:lychee";

    /// <inheritdoc />
    protected override string ToolName => "lychee";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new LycheeJsonOutputParser();

    /// <summary>
    /// Offline by default: the audit sandbox runs this auditor with no
    /// credentials and no network. When the operator enables
    /// <c>CheckRemoteLinks</c> the auditor declares
    /// <see cref="AuditCapabilities.Network"/> so the sandbox is provisioned
    /// with egress.
    /// </summary>
    public override AuditCapabilities Required =>
        _checkRemoteLinks() ? AuditCapabilities.Network : AuditCapabilities.None;

    /// <summary>
    /// Declared mapping from lychee's failure vocabulary to
    /// <see cref="AuditSeverity"/>: <c>error_map</c> entries ("error") are
    /// confirmed link failures — <see cref="AuditSeverity.Error"/>;
    /// <c>timeout_map</c> entries ("timeout") are weak, flaky evidence —
    /// <see cref="AuditSeverity.Warning"/>. Anything the parser cannot
    /// classify keeps the fail-closed <see cref="AuditSeverity.Error"/>
    /// default. Raw tool levels never reach findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["error"] = AuditSeverity.Error,
            ["timeout"] = AuditSeverity.Warning,
        }, AuditSeverity.Error);

    /// <inheritdoc />
    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    /// <inheritdoc />
    protected override ToolVersionPin? VersionPin =>
        new(PluginId, _expectedVersion, DefaultExpectedVersion, ["--version"]);

    /// <inheritdoc />
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
        var operatorSuppliesConfig = ExtraArgumentsSupplyFlag(options, "--config", "-c");
        var operatorSuppliesRootDir = ExtraArgumentsSupplyFlag(options, "--root-dir");

        var args = new List<string>
        {
            // The stats report on stdout is the verdict; the parser reads
            // error_map/timeout_map which are populated on every run.
            "--format", "json",
            "--no-progress",
            // Docs legitimately live under dot-directories (.github/, .gitlab/).
            // .git/ stays out via the ExcludePaths-derived --exclude-path below.
            "--hidden",
            // Broken in-repo #anchors are a common docs failure and cost no
            // network requests for local targets.
            "--include-fragments",
        };

        // The default sandbox has no egress; without this every remote URL
        // would be a network-error finding instead of an exclusion.
        if (!_checkRemoteLinks())
            args.Add("--offline");

        // Repository-authored walker suppression (.gitignore, .ignore, the
        // global ignore file) would let the audit subject hide a documented
        // broken link from the crawl — the subject writes those files. Unless
        // the operator opts in to repository suppression, the walker ignores
        // them; the ExcludePaths-derived --exclude-path flags below keep
        // vendored/generated trees out of the crawl instead.
        if (!_trustRepositorySuppression())
            args.Add("--no-ignore");

        // Config precedence: explicit operator config, else the pinned empty
        // config that disables repo config loading, else (when trusting the
        // repository) lychee's own default lookup.
        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath) && !operatorSuppliesConfig)
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }
        else if (!_trustRepositorySuppression() && !operatorSuppliesConfig)
        {
            args.Add("--config");
            args.Add(PinnedEmptyConfigPath);
        }

        var rootDirectory = _rootDirectory();
        if (!string.IsNullOrWhiteSpace(rootDirectory) && !operatorSuppliesRootDir)
        {
            args.Add("--root-dir");
            args.Add(rootDirectory.Trim());
        }

        // Keep excluded trees out of the crawl entirely — links inside them
        // would be checked (wasted requests) before the findings-level
        // ExcludePaths filter could drop them. Each entry is translated into
        // an anchored regex matching the base's exact/prefix semantics.
        foreach (var entry in options.ExcludePaths)
        {
            var pattern = ToExcludePathPattern(entry);
            if (pattern is not null)
            {
                args.Add("--exclude-path");
                args.Add(pattern);
            }
        }

        var inputs = _inputs();
        if (inputs.Count > 0)
            args.AddRange(inputs);
        else
            args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, CreateDefaults());
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _configPath = () => scoped[ConfigPathKey];
        _rootDirectory = () => scoped[RootDirectoryKey];
        _inputs = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[InputsKey]);
        _checkRemoteLinks = () =>
            bool.TryParse(scoped[CheckRemoteLinksKey], out var check) && check;
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "LycheeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// lychee-specific precondition on the live path beyond the base's pinned
    /// version check: unless the operator opted in, a repo-root
    /// <c>.lycheeignore</c> — which lychee would load unconditionally — fails
    /// closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (!_trustRepositorySuppression())
            await ThrowIfRepositoryIgnoreFilePresentAsync(sandbox, workingDirectory, tool, options, ct)
                .ConfigureAwait(false);
    }

    private async Task ThrowIfRepositoryIgnoreFilePresentAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // lychee loads <cwd>/.lycheeignore unconditionally — its lines become
        // URL-exclusion regexes the audit subject could use to hide a broken
        // link — and no flag redirects or disables that load, so presence is
        // gated rather than outranked. (.gitignore/.ignore need no gate: the
        // scan runs --no-ignore so they cannot suppress input files.)
        var present = await ProbeRepositoryFilesPresentAsync(
            sandbox,
            workingDirectory,
            tool,
            [RepositoryIgnoreFile],
            options,
            ct).ConfigureAwait(false);
        if (present.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found '{RepositoryIgnoreFile}' at the root of the "
                + "audited repository — lychee loads it unconditionally and honors its URL-exclusion "
                + "patterns, so the audit subject could use it to hide a broken link. Remove the file, "
                + $"or set CodeyBox:Plugins:{PluginId}:{TrustRepositorySuppressionKey}"
                + " to true to trust repository-controlled suppression surfaces.")
            { IsDeterministic = true };
    }

    /// <summary>
    /// Translates an <see cref="ExternalToolAuditorOptions.ExcludePaths"/>
    /// entry into a lychee <c>--exclude-path</c> regex preserving the base's
    /// semantics: a trailing-<c>/</c> entry excludes everything beneath that
    /// root-level directory prefix; any other entry excludes exactly that
    /// root-relative path. Anchored at the start (allowing the walker-emitted
    /// <c>"./"</c> prefix) so nested namesakes like <c>docs/vendor/</c> are not
    /// swept up. Every entry is <see cref="Regex.Escape"/>d — operator config
    /// is treated as data, never as pattern text.
    /// </summary>
    internal static string? ToExcludePathPattern(string? entry)
    {
        var normalized = NormalizeExcludePathEntry(entry);
        if (normalized is null)
            return null;
        return normalized.EndsWith('/')
            ? @"^(?:\./)?" + Regex.Escape(normalized)
            : @"^(?:\./)?" + Regex.Escape(normalized) + "$";
    }
}
