using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DetectSecretsAuditorPlugin;

/// <summary>
/// Secrets auditor wrapping Yelp's <c>detect-secrets</c> on the shared
/// <see cref="ExternalToolAuditorBase"/>: the base supplies sandboxed
/// invocation with a bounded timeout, per-stream output caps, severity
/// mapping, exit-code classification, and per-auditor configuration. This
/// class declares the pinned tool version through the base's
/// <see cref="ExternalToolAuditorBase.VersionPin"/>, adds the scan-scope and
/// repository-suppression gates below through
/// <see cref="ExternalToolAuditorBase.VerifyToolAsync"/>, and routes the
/// operator-supplied baseline plumbing through
/// <see cref="ExternalToolAuditorBase.ResolveContextArgumentsAsync"/> and
/// <see cref="ExternalToolAuditorBase.ResolveParserInputAsync"/>.
///
/// <para><b>Gate behaviour: blocking by default.</b> detect-secrets emits no
/// severity vocabulary — every report entry is a potential credential — so
/// the declared mapping sends every result to <see cref="AuditSeverity.Error"/>
/// and any surviving finding fails the audit. That is the intended gate for
/// a secrets scanner; narrow scope with <c>ExcludedRules</c> /
/// <c>ExcludePaths</c> or an audited <c>BaselineFile</c> rather than
/// expecting advisory severity.</para>
///
/// <para><b>Exit-code convention (verified against detect-secrets 1.5.0
/// <c>main.py</c> — NOT the common "1 = findings" convention).</b>
/// <c>detect-secrets scan</c> returns <c>0</c> unconditionally: a scan that
/// found secrets and a clean scan both exit <c>0</c>, and the baseline JSON
/// document on stdout is the verdict (the <c>results</c> object; the tool
/// even emits <c>"results": {}</c> for a clean scan). Every non-zero exit
/// means "could not run": <c>1</c> is an unhandled exception (argparse
/// post-processing failures such as an unreadable baseline exit 1 too),
/// <c>2</c> is an argparse usage error, and <c>126</c>/<c>127</c> are
/// cannot-execute/not-found. Findings-producing exits are therefore exactly
/// <c>{0}</c>; everything else is infrastructure.</para>
///
/// <para><b>The silent-empty trap.</b> Without <c>--all-files</c> the scan
/// enumerates git-tracked files via <c>git ls-files</c>: on a non-git
/// directory, or below the worktree root, detect-secrets logs a warning,
/// scans nothing, and still exits <c>0</c> with an empty <c>results</c> —
/// indistinguishable from a clean repo. The same silent-skip applies per
/// file: the tool never unquotes git's C-quoted listing output, so a
/// tracked file whose name needs quoting (non-ASCII, quote, backslash, or
/// control bytes) is silently never scanned. <see cref="VerifyToolAsync"/>
/// therefore fails closed unless the working directory is the root of a
/// git worktree AND <c>git ls-files</c> emits no C-quoted entry (one
/// bounded probe). An operator-supplied <c>--all-files</c> disables the
/// gate because that mode walks the filesystem and needs no git.</para>
///
/// <para><b>No verification calls.</b> The scan always passes
/// <c>--no-verify</c>: without it detect-secrets would ship each found
/// secret to its provider's API to test liveness — network egress carrying
/// credential material out of the audit sandbox — and would silently drop
/// credentials verified as dead, which weakens the gate and makes findings
/// depend on network reachability.</para>
///
/// <para><b>Version pin.</b> A scanner's detectors change between releases,
/// so findings are only meaningful from the build the auditor was verified
/// against. The auditor probes <c>detect-secrets --version</c> before every
/// scan; a missing binary, an unrecognised version string, or a version
/// other than <c>ExpectedVersion</c> is an infrastructure failure naming the
/// tool — never a pass, never a finding.</para>
///
/// <para><b>Repository-controlled suppression.</b> The only suppression
/// surface detect-secrets's <c>scan</c> honors from inside the audited
/// repository is the inline <c>pragma: allowlist secret</c> comment — the
/// audit subject could annotate a leaked line and silence it. By default the
/// scan passes <c>--disable-filter
/// detect_secrets.filters.allowlist.is_line_allowlisted</c> so pragmas are
/// ignored; an operator that trusts repository-authored suppressions sets
/// <c>TrustRepositorySuppression</c> in scoped config. A committed
/// <c>.secrets.baseline</c> is NOT loaded by <c>scan</c> (baselines only
/// apply via an explicit <c>--baseline</c>, which this auditor drives only
/// through the guarded <see cref="BaselineFileKey"/> knob), so it cannot
/// suppress findings — but it IS excluded from findings by default because
/// its <c>hashed_secret</c> hex entries re-trip the entropy detectors as
/// pure noise.</para>
///
/// <para><b>Operator-supplied baseline.</b> detect-secrets's signature
/// workflow is an auditable baseline file: entries a human marked
/// <c>is_secret: false</c> during <c>detect-secrets audit</c> stay suppressed
/// while everything else is reported. Setting <c>BaselineFile</c> to an
/// absolute path makes the auditor canonicalize it inside the sandbox,
/// reject it when it resolves inside the audited worktree (the audit subject
/// must not author the file that silences its own findings), copy it into
/// the per-run scratch directory — <c>--baseline</c> rewrites the file it
/// is given, and the operator's copy must never be mutated — and pass it to
/// the scan with <c>--force-use-all-plugins</c> so the detector set comes
/// from the pinned tool, not from whatever plugin list the baseline file
/// recorded. In baseline mode the report is written to the file instead of
/// stdout, so <see cref="ResolveParserInputAsync"/> reads the per-run copy
/// back through a bounded read.</para>
///
/// <para><b>Scope and defaults.</b> The scan target is <c>.</c> — every
/// git-tracked file in the worktree at its current state (detect-secrets
/// does not scan history; covering committed-but-since-removed secrets is
/// the gitleaks auditor's job). Findings under vendored/dependency trees
/// (<c>vendor/</c>, <c>third_party/</c>, <c>node_modules/</c>) and in a
/// repo-root <c>.secrets.baseline</c> are excluded by default: vendored
/// secrets belong to upstream code, and the baseline file's own
/// <c>hashed_secret</c> hex strings self-flag — both are noise that trains
/// operators to ignore the auditor. Operators re-include a path by
/// overriding <c>ExcludePaths</c>.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: detect-secrets Secrets",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "detect-secrets",
    InstallHint = "provision the pinned detect-secrets release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — it is a Python package with no "
        + "distro apt package: install it pinned (e.g. 'pipx install detect-secrets=="
        + DefaultExpectedVersion + "' or 'pip install detect-secrets==" + DefaultExpectedVersion
        + "') so the detect-secrets entry point lands on PATH, via CodeyBox:MultipassExtraRuncmd / "
        + "CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class DetectSecretsAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.detect-secrets";

    /// <summary>
    /// detect-secrets release the invocation and its report shape are
    /// verified against. Operators running a different pinned build set
    /// <c>ExpectedVersion</c> in the plugin's scoped config to match what
    /// they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "1.5.0";

    /// <summary>
    /// Scoped-config key for an operator-supplied auditable baseline file
    /// (absolute path in the sandbox). Set → the file is canonicalized,
    /// rejected when it resolves inside the audited worktree, copied into
    /// the per-run scratch directory, and passed as <c>--baseline</c> so
    /// entries marked <c>is_secret: false</c> by an audit stay suppressed.
    /// Unset → a plain cataloguing scan that reports every potential secret.
    /// </summary>
    public const string BaselineFileKey = "BaselineFile";

    /// <summary>
    /// Scoped-config key opting in to repository-authored suppression
    /// (inline <c>pragma: allowlist secret</c> comments). Default false: the
    /// audited repo must not be able to silence the audit.
    /// </summary>
    internal const string TrustRepositorySuppressionKey = "TrustRepositorySuppression";

    /// <summary>
    /// File name of the per-run copy of the operator baseline inside
    /// <see cref="ExternalToolAuditorBase.PerRunTempDirectoryPath"/>.
    /// <c>--baseline</c> rewrites the file it is handed, so the scan reads
    /// and writes a throwaway copy — the operator's file is never mutated.
    /// </summary>
    internal const string BaselineCopyFileName = "operator-baseline.json";

    // The single suppression surface detect-secrets honors from inside the
    // audited repository: `pragma: allowlist secret` / `allowlist secret`
    // comments disable reporting on that line. Disabled by default — the
    // audit subject authors those comments.
    private const string AllowlistFilterPath =
        "detect_secrets.filters.allowlist.is_line_allowlisted";

    // One-shot guard for baseline mode: create the per-run scratch dir and
    // copy the operator's canonicalized baseline into it. $1 is the scratch
    // dir, $2 the canonical source — both arrive as argv, never spliced in.
    // The scan then rewrites only the copy (`--baseline` writes back to the
    // file it is given), leaving the operator's original untouched.
    private const string BaselineCopyScript =
        "d=\"$1\" && mkdir -m 700 -p \"$d\" && cp -- \"$2\" \"$d/" + BaselineCopyFileName + "\"";

    // Working-directory precondition, one bounded probe whose exit code
    // names which gate failed:
    //   3 — not a worktree root: $(…) empties on git failure, so a missing
    //       git binary, a non-git directory, and a non-root subdirectory
    //       (--show-prefix non-empty) all collapse into this failure.
    //   4 — `git ls-files` itself failed inside a directory that claims to
    //       be a worktree root; tracked-file coverage cannot be confirmed.
    //   5 — the listing holds a C-quoted name. detect-secrets's
    //       util/git.py::get_tracked_files inserts each raw `git ls-files`
    //       output line into its scan set WITHOUT unquoting, while git
    //       C-quotes any name containing '"', '\', a control byte, or a
    //       byte >= 0x80 (core.quotePath default: a tracked 'sëcrets.txt'
    //       is emitted as "s\303\253crets.txt"). The os.walk-derived real
    //       path then fails the membership test and the file is silently
    //       never scanned — exit 0, empty contribution to `results`, a
    //       clean pass for files that were never read. Git wraps every
    //       quoted entry in double quotes, so a line starting with '"' is
    //       an exact detector, and the probe sees the same quoting the
    //       tool's own ls-files call produces (repository git config —
    //       core.quotePath — applies equally to both).
    //   0 — coverage is provably complete.
    private const string GitWorktreeRootScript =
        "test \"$(git rev-parse --is-inside-work-tree 2>/dev/null)\" = true"
        + " && test -z \"$(git rev-parse --show-prefix 2>/dev/null)\" || exit 3;"
        + " listing=$(git ls-files) || exit 4;"
        + " printf '%s\\n' \"$listing\" | grep -q '^\"' && exit 5;"
        + " exit 0";

    private const int TrackedEnumerationFailedExit = 4;
    internal const int QuotedTrackedNamesExit = 5;

    // Flags whose presence in ExtraArguments would redirect the report off
    // stdout, load executable/suppressing content from an unguarded path,
    // or silently reshape scan coverage. Rejected deterministically (naming
    // the knob to use instead) rather than failing later as an opaque parse
    // failure or silently weakening the gate:
    //   --baseline         redirects the report into the named file and
    //                      loads plugin/filter settings from it — it must
    //                      travel through the guarded, canonicalized
    //                      BaselineFile knob.
    //   -p/--plugin, -f/--filter
    //                      load Python code from paths that could resolve
    //                      inside the audited worktree, letting the audit
    //                      subject execute code in the scan process —
    //                      install custom detectors/filters into the
    //                      baseline image instead.
    //   --word-list, --gibberish-model
    //                      same unguarded-file-load class (feature-gated
    //                      wordlist and gibberish-model files).
    //   -C/--custom-root   retargets the scan at a different root while the
    //                      worktree precondition probes the working
    //                      directory — coverage would silently shrink.
    //   --only-allowlisted flips the scan to pragma-only mode, so
    //                      repo-authored allowlist comments alone decide
    //                      findings.
    // Python argparse resolves unambiguous long-option prefixes by default
    // (allow_abbrev), so RejectReservedExtraArguments also rejects tokens
    // that resolve to a reserved flag by abbreviation ('--bas', '--plug').
    private static readonly (string Long, string Short)[] ReservedFlags =
    [
        ("--baseline", ""),
        ("--plugin", "-p"),
        ("--filter", "-f"),
        ("--word-list", ""),
        ("--gibberish-model", ""),
        ("--custom-root", "-C"),
        ("--only-allowlisted", ""),
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // Verified against detect-secrets 1.5.0 main.py: `scan` returns 0
        // unconditionally — clean and findings-producing runs share exit 0
        // and the `results` object in the baseline JSON on stdout is the
        // verdict. 1 = unhandled exception (including argparse
        // post-processing failures), 2 = usage error, 126/127 =
        // cannot-execute. {0} is the only findings-producing exit.
        FindingsExitCodes = new HashSet<int> { 0 },
        // vendor/, third_party/, node_modules/: findings in vendored or
        // dependency trees belong to upstream code, not the change under
        // audit — noise that trains operators to ignore the auditor.
        // .secrets.baseline: the tool's own baseline artifact; it is never
        // loaded by `scan` (baselines apply only via --baseline), but its
        // hashed_secret hex entries re-trip the entropy detectors, so
        // every repo already using detect-secrets would report itself.
        // Operators re-include a path by overriding ExcludePaths in scoped
        // config.
        ExcludePaths = [".secrets.baseline", "vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<string?> _baselineFile = static () => null;
    private Func<bool> _trustRepositorySuppression = static () => false;

    /// <inheritdoc />
    public override string Name => "codeybox:detect-secrets";

    /// <inheritdoc />
    protected override string ToolName => "detect-secrets";

    /// <inheritdoc />
    protected override IExternalToolOutputParser OutputParser { get; } = new DetectSecretsBaselineParser();

    /// <summary>
    /// detect-secrets assigns no per-result severity — every entry in the
    /// baseline's <c>results</c> is a detected credential — so the declared
    /// mapping is total: the parser's verification-state tokens
    /// (<c>verified</c>, <c>unverified</c>) and anything else map to
    /// <see cref="AuditSeverity.Error"/>. Raw tool vocabulary never reaches
    /// findings.
    /// </summary>
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["verified"] = AuditSeverity.Error,
            ["unverified"] = AuditSeverity.Error,
        }, AuditSeverity.Error);

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
            // `scan` prints the baseline document (the report) on stdout when
            // --baseline is absent; the parser reads `results` from it.
            "scan",
            // --no-verify: no outbound liveness calls carrying found secrets
            // to provider APIs, and no filter dropping provider-rejected
            // credentials — the sandbox scan stays deterministic and offline.
            "--no-verify",
        };
        if (!_trustRepositorySuppression())
            args.AddRange(["--disable-filter", AllowlistFilterPath]);

        // "." scans every git-tracked file under the worktree root (the
        // precondition in VerifyToolAsync proves the working directory is a
        // worktree root, because detect-secrets silently scans nothing and
        // still exits 0 outside one).
        args.Add(".");
        return args;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _baselineFile = () => scoped[BaselineFileKey];
        _trustRepositorySuppression = () =>
            bool.TryParse(scoped[TrustRepositorySuppressionKey], out var trust) && trust;
        context.Logger.LogInformation(
            "DetectSecretsAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the <c>--baseline</c> argument pair when an operator supplies
    /// <see cref="BaselineFileKey"/>: canonicalizes the configured path in
    /// the sandbox (rejecting any resolution inside the audited worktree —
    /// the baseline suppresses findings, so the audit subject must not
    /// author it), copies it into the per-run scratch directory because the
    /// tool rewrites the file it is given, and returns the flag pair plus
    /// <c>--force-use-all-plugins</c> so the detector set comes from the
    /// pinned tool rather than the baseline's recorded plugin list. The
    /// value in argv is the canonicalized path the guard produced.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ResolveContextArgumentsAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var configured = _baselineFile();
        if (string.IsNullOrWhiteSpace(configured))
            return [];

        var canonical = await CanonicalizeOutsideWorktreeAsync(
            sandbox, workingDirectory, configured.Trim(), BaselineFileKey, options, ct)
            .ConfigureAwait(false);

        var scratchDirectory = PerRunTempDirectoryPath;
        var copy = await ExecToolBoundedAsync(
            sandbox,
            ToolName,
            "baseline copy",
            new SandboxExec
            {
                Argv = ["sh", "-c", BaselineCopyScript, "sh", scratchDirectory, canonical],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);
        if (copy.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' baseline copy could not run: the sandbox "
                + "exec transport was unavailable.");
        if (copy.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolName}' could not stage the configured "
                + $"{BaselineFileKey} '{TruncateForMessage(configured)}' into the per-run scratch "
                + $"directory (exit {copy.ExitCode}) — the baseline suppresses findings, so an "
                + "unreadable one is infrastructure, not a verdict on the diff.",
                copy.ExitCode,
                copy.Stdout + "\n" + copy.Stderr);

        return
        [
            "--baseline", Path.Combine(scratchDirectory, BaselineCopyFileName),
            "--force-use-all-plugins",
        ];
    }

    /// <summary>
    /// detect-secrets-specific preconditions beyond the base's presence and
    /// version checks: unless the operator's ExtraArguments genuinely put
    /// the scan in <c>--all-files</c> mode (only tokens before a bare
    /// <c>--</c> terminator count — argparse treats later ones as
    /// positional paths), the scan enumerates git-tracked files — and
    /// outside a git worktree (or below its root) it scans NOTHING and
    /// still exits 0 with an empty <c>results</c>, which would read as a
    /// clean pass for a scan that never ran. The same silent-skip applies
    /// to tracked file names git must C-quote: detect-secrets compares the
    /// raw <c>git ls-files</c> output line to walked paths and such files
    /// never match, so one bounded probe also fails closed when the
    /// listing emits any quoted entry (a leading <c>"</c> line is an exact
    /// detector).
    /// </summary>
    protected override async Task VerifyToolAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        if (AllFilesScanConfigured(options))
            return;

        var probe = await ExecToolBoundedAsync(
            sandbox,
            tool,
            "worktree check",
            new SandboxExec
            {
                Argv = ["sh", "-c", GitWorktreeRootScript, "sh"],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = ProbeMaxOutputBytes,
                MaxStderrBytes = ProbeMaxOutputBytes,
                KillOnOutputLimit = true,
            },
            ProbeTimeout(options),
            ct).ConfigureAwait(false);
        if (probe.ExecutionUnavailable)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' worktree check could not run: the sandbox "
                + "exec transport was unavailable.");
        if (probe.ExitCode == QuotedTrackedNamesExit)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' would silently skip tracked files whose "
                + "names git must C-quote (a '\"', '\\', control byte, or byte >= 0x80 in the "
                + "name — e.g. 'sëcrets.txt'): detect-secrets compares the quoted `git ls-files` "
                + "entry verbatim against walked paths and never scans such files, yet still "
                + "exits 0 — a clean pass for files never read. Rename the tracked files, or "
                + "pass --all-files in ExtraArguments to scan the filesystem instead.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
        if (probe.ExitCode == TrackedEnumerationFailedExit)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' could not enumerate the worktree's "
                + "git-tracked files — coverage cannot be confirmed, so this is infrastructure, "
                + "not a verdict on the diff.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{tool}' requires the audit working directory to be "
                + "the root of a git worktree — outside one, detect-secrets scans no files but "
                + "still exits 0 with an empty report, which would look like a clean pass for a "
                + "scan that never ran. Provision a git worktree, or pass --all-files in "
                + "ExtraArguments to scan the filesystem instead.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
    }

    /// <summary>
    /// Baseline mode redirects the report: with <c>--baseline</c> the tool
    /// writes the merged baseline document to the file instead of stdout,
    /// leaving stdout empty. The report is therefore read back from the
    /// per-run copy through a separate bounded sandbox read — the seam the
    /// base provides for reports that must not ride the captured scan
    /// streams. A missing, oversized, or unreadable report fails closed as
    /// infrastructure — never a pass. Without <see cref="BaselineFileKey"/>
    /// the captured stdout is the report (the base's default).
    /// </summary>
    protected override async Task<ExternalToolParseInput> ResolveParserInputAsync(
        ISandbox sandbox,
        string workingDirectory,
        string tool,
        ExternalToolAuditorOptions options,
        SandboxExecResult result,
        IReadOnlyList<string> argv,
        string? scanRoot,
        CancellationToken ct)
    {
        // The baseline decision is read from the argv this run invoked — a
        // hot config reload landing between ResolveContextArgumentsAsync
        // and this hook cannot flip the report source mid-run. A bare
        // "--baseline" token can only be the pair this auditor emitted:
        // RejectReservedExtraArguments refuses it in ExtraArguments (even
        // positionally, after a `--` terminator).
        var reportPath = BaselineReportPath(argv);
        if (reportPath is null)
            return await base.ResolveParserInputAsync(
                sandbox, workingDirectory, tool, options, result, argv, scanRoot, ct)
                .ConfigureAwait(false);

        return await ReadReportFileParseInputAsync(
            sandbox, workingDirectory, tool, options, result, reportPath, scanRoot, ct)
            .ConfigureAwait(false);
    }

    // The staged per-run baseline copy path when the run's argv carried a
    // `--baseline <path>` pair; null otherwise.
    private static string? BaselineReportPath(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (string.Equals(argv[i], "--baseline", StringComparison.Ordinal))
                return argv[i + 1];
        }
        return null;
    }

    /// <summary>
    /// True when the operator's ExtraArguments genuinely put the scan in
    /// <c>--all-files</c> mode. argparse stops option parsing at a bare
    /// <c>--</c> — a <c>--all-files</c> after it is a positional path, not
    /// the flag, so it must not skip the worktree gate — and resolves
    /// unambiguous long-option prefixes, so an abbreviation like
    /// <c>--all</c> counts too.
    /// </summary>
    private static bool AllFilesScanConfigured(ExternalToolAuditorOptions options)
    {
        var optionTokens = options.ExtraArguments
            .TakeWhile(static arg => arg != "--")
            .ToArray();
        return optionTokens.Any(static arg => ResolvesToLongOption(arg, "--all-files"));
    }

    // Python argparse resolves unambiguous long-option prefixes by default
    // (allow_abbrev=True): '--bas=x' reaches the parser as '--baseline=x'.
    // A token resolves to `longOption` when the part before any '=' is the
    // option itself or a non-empty '--'-prefixed proper prefix of it; the
    // bare '--' terminator (length 2) never matches.
    private static bool ResolvesToLongOption(string argument, string longOption)
    {
        var eq = argument.IndexOf('=', StringComparison.Ordinal);
        var token = eq >= 0 ? argument[..eq] : argument;
        return token.Length > 2
            && token.StartsWith("--", StringComparison.Ordinal)
            && longOption.StartsWith(token, StringComparison.Ordinal);
    }

    private void RejectReservedExtraArguments(ExternalToolAuditorOptions options)
    {
        var offenders = new List<string>();
        foreach (var (longFlag, shortFlag) in ReservedFlags)
        {
            // ResolvesToLongOption covers the separated and '--flag=value'
            // forms AND argparse's prefix abbreviations; the shared matcher
            // additionally covers a short flag's joined '-fvalue' form.
            var supplied =
                options.ExtraArguments.Any(arg => ResolvesToLongOption(arg, longFlag))
                || (shortFlag.Length > 0 && ExtraArgumentsSupplyFlag(options, shortFlag));
            if (supplied)
                offenders.Add(longFlag);
        }
        if (offenders.Count == 0)
            return;
        throw new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' was configured with ExtraArguments carrying "
            + $"reserved flag(s) '{string.Join("', '", offenders)}' — they would redirect the "
            + $"report off stdout (--baseline goes through the guarded CodeyBox:Plugins:{PluginId}:"
            + $"{BaselineFileKey} knob instead), load code or data files from a path that could "
            + "resolve inside the audited worktree (-p/--plugin, -f/--filter, --word-list, "
            + "--gibberish-model — install custom detectors into the sandbox baseline image "
            + "instead), or silently reshape scan coverage (-C/--custom-root, "
            + "--only-allowlisted).")
        { IsDeterministic = true };
    }
}
