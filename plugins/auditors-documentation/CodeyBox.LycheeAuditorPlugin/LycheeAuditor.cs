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
/// (<see cref="LycheeJsonOutputParser"/>), the pinned tool-version probe and
/// the repository-suppression gate via
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, and the offline/remote
/// posture below.
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
/// <para><b>Repository-controlled suppression.</b> lychee honors surfaces
/// authored inside the audited repository: a <c>.lycheeignore</c> file in the
/// working directory is loaded unconditionally (no flag disables it) and its
/// lines become URL-exclusion regexes, and when no <c>--config</c> is given
/// lychee auto-loads configuration from a repo-root <c>lychee.toml</c> (or a
/// <c>[lychee]</c> section in <c>Cargo.toml</c>/<c>pyproject.toml</c>/
/// <c>package.json</c>). The audit subject writes that repository, so by
/// default a <c>.lycheeignore</c> at the repo root fails closed as
/// infrastructure and the scan pins configuration with
/// <c>--config /dev/null</c>, which disables every default config-file lookup
/// (an operator <c>ConfigPath</c> or <c>--config</c> in <c>ExtraArguments</c>
/// takes precedence over the pin). <c>TrustRepositorySuppression</c> opts in
/// to the repository's own suppression surfaces. Note lychee also skips input
/// files matched by <c>.gitignore</c>/<c>.ignore</c> rules; the auditor keeps
/// that default because <c>--no-ignore</c> would crawl gitignored build
/// output present in the worktree. Residual gap, documented not hidden: a
/// subject could commit a file and then exclude it via <c>.gitignore</c> so
/// lychee's walker skips it — matching a dedicated suppression file's effect
/// through a file nearly every repo legitimately has, which is why it is not
/// gated like <c>.lycheeignore</c>.</para>
///
/// <para><b>Scope and defaults.</b> The scan is <c>lychee .</c>: the worktree
/// is walked recursively, filtered to lychee's documentation extensions
/// (Markdown, HTML, CSS, XML, plaintext). Hidden files are included
/// (<c>--hidden</c>) because documentation legitimately lives under
/// dot-directories such as <c>.github/</c>; <c>.git/</c> is excluded. Links
/// under vendored (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>)
/// and generated (<c>dist/</c>, <c>build/</c>, <c>out/</c>, <c>coverage/</c>)
/// prefixes are excluded at extraction time (<c>--exclude-path</c>, derived
/// from <c>ExcludePaths</c>) and findings under them are dropped as well:
/// problems there belong to upstream packages or build output, not the
/// change under audit. <c>--include-fragments</c> checks <c>#anchor</c>
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
    /// <c>.lycheeignore</c> and the default lychee config files
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

    private const int ProbeMaxOutputBytes = 16 * 1024;
    private const int MessageValueMaxChars = 64;
    // The precondition probes are liveness checks, not the scan: they never
    // need more than this and share the operator-configured timeout below it.
    private static readonly TimeSpan ProbeTimeoutCap = TimeSpan.FromSeconds(30);
    private static readonly Regex VersionPattern = new(
        @"\d+\.\d+\.\d+[\w.\-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = all checked links OK; 2 = link check failures. Both emit the
        // JSON report — both are verdicts. 1 (missing input / runtime /
        // config error), 3 (config-file error), and everything else is
        // infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 2 },
        // Findings in VCS internals, vendored/dependency trees, and generated
        // build output do not describe the change under audit — noise that
        // trains operators to ignore the auditor. Each entry is also passed
        // to lychee as --exclude-path so those files are never crawled.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths =
        [
            ".git/",
            "vendor/",
            "third_party/",
            "node_modules/",
            "dist/",
            "build/",
            "out/",
            "coverage/",
        ],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
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
    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
    {
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

        // Config precedence: explicit operator config, else the pinned empty
        // config that disables repo config loading, else (when trusting the
        // repository) lychee's own default lookup.
        var configPath = _configPath();
        if (!string.IsNullOrWhiteSpace(configPath)
            && !options.ExtraArguments.Contains("--config", StringComparer.Ordinal)
            && !options.ExtraArguments.Contains("-c", StringComparer.Ordinal))
        {
            args.Add("--config");
            args.Add(configPath.Trim());
        }
        else if (!_trustRepositorySuppression()
            && !options.ExtraArguments.Contains("--config", StringComparer.Ordinal)
            && !options.ExtraArguments.Contains("-c", StringComparer.Ordinal))
        {
            args.Add("--config");
            args.Add(PinnedEmptyConfigPath);
        }

        var rootDirectory = _rootDirectory();
        if (!string.IsNullOrWhiteSpace(rootDirectory)
            && !options.ExtraArguments.Contains("--root-dir", StringComparer.Ordinal))
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
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped["ExpectedVersion"];
        _configPath = () => scoped[ConfigPathKey];
        _rootDirectory = () => scoped[RootDirectoryKey];
        _inputs = () => SplitList(scoped[InputsKey]);
        _checkRemoteLinks = () =>
            bool.TryParse(scoped[CheckRemoteLinksKey], out var check) && check;
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "LycheeAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// lychee-specific preconditions on the live path: the pinned scanner
    /// version, and — unless the operator opted in — absence of a repo-root
    /// <c>.lycheeignore</c>, which lychee would load unconditionally. Both
    /// fail closed as infrastructure before the scan runs.
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        await ThrowIfToolVersionMismatchAsync(sandbox, workingDirectory, tool, options, ct)
            .ConfigureAwait(false);
        if (!_trustRepositorySuppression())
            await ThrowIfRepositoryIgnoreFilePresentAsync(sandbox, workingDirectory, tool, options, ct)
                .ConfigureAwait(false);
    }

    private async Task ThrowIfToolVersionMismatchAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _expectedVersion();
        var expected = NormalizeVersion(configured);
        if (expected is null)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' has an unparseable ExpectedVersion "
                + $"('{TruncateForMessage(configured)}'); set CodeyBox:Plugins:{PluginId}:ExpectedVersion "
                + $"to a {tool} release such as '{DefaultExpectedVersion}'.")
            { IsDeterministic = true };

        var result = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "version check",
            new SandboxExec
            {
                Argv = [tool, "--version"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        var reported = ExtractVersion(result.Stdout);
        if (result.ExecutionUnavailable
            || result.ExitCode != 0
            || reported is null)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' version could not be determined "
                + $"(exit {result.ExitCode}). The pinned release is required before the scan can run — "
                + $"a missing or foreign '{tool}' is infrastructure, not a verdict on the diff.",
                result.ExitCode,
                result.Stdout + "\n" + result.Stderr);

        if (!string.Equals(reported, expected, StringComparison.Ordinal))
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' is version {reported}, but this auditor is "
                + $"pinned to {expected}. A different checker version changes the link checks and the "
                + "findings; provision the pinned release or set ExpectedVersion to the version you provisioned.")
            { IsDeterministic = true };
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
        // gated rather than outranked. (-L covers a dangling symlink that -e
        // would miss.)
        var presence = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "suppression check",
            new SandboxExec
            {
                Argv = ["sh", "-c", "[ -e \"$1\" ] || [ -L \"$1\" ]", "sh", RepositoryIgnoreFile],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);

        if (presence.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' suppression check could not run: the sandbox exec "
                + "transport was unavailable.");
        if (presence.ExitCode == 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' found '{RepositoryIgnoreFile}' at the root of the "
                + "audited repository — lychee loads it unconditionally and honors its URL-exclusion "
                + "patterns, so the audit subject could use it to hide a broken link. Remove the file, "
                + "or set CodeyBox:Plugins:" + PluginId + ":" + TrustRepositorySuppressionKey
                + " to true to trust repository-controlled suppression surfaces.",
                presence.ExitCode,
                presence.Stdout + "\n" + presence.Stderr)
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
        if (string.IsNullOrWhiteSpace(entry))
            return null;
        var normalized = entry.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Length == 0)
            return null;
        return normalized.EndsWith('/')
            ? @"^(?:\./)?" + Regex.Escape(normalized)
            : @"^(?:\./)?" + Regex.Escape(normalized) + "$";
    }

    private static TimeSpan ProbeTimeout(ExternalToolAuditorOptions options)
    {
        var timeout = EffectiveTimeout(options);
        return timeout > ProbeTimeoutCap ? ProbeTimeoutCap : timeout;
    }

    private static string? ExtractVersion(string stdout)
    {
        // `lychee --version` prints "lychee 0.24.2".
        var match = VersionPattern.Match(stdout);
        return match.Success ? match.Value : null;
    }

    private static string? NormalizeVersion(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured)
            ? DefaultExpectedVersion
            : configured.Trim();
        var match = VersionPattern.Match(value);
        return match.Success ? match.Value : null;
    }

    private static string TruncateForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var single = SingleLine(value);
        return single.Length > MessageValueMaxChars
            ? single[..MessageValueMaxChars] + "…"
            : single;
    }

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => item.Length > 0)
                .ToList();
}
