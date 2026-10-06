using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GltfValidatorAuditorPlugin;

/// <summary>
/// Asset auditor wrapping the official Khronos <c>gltf_validator</c> CLI:
/// each explicitly selected asset is validated with its own bounded
/// <c>gltf_validator -o &lt;asset&gt;</c> invocation through
/// <see cref="GltfValidatorSingleAssetScan"/> (which extends the shared
/// <see cref="ExternalToolAuditorBase"/> and therefore inherits its
/// sandboxed invocation, bounded timeout, per-stream output caps, exit-code
/// classification, severity mapping, finding identity, and configuration),
/// and this orchestrator aggregates the per-asset results. Shared sandbox
/// and validation policy (timeouts, transport-loss classification, path
/// containment) is reused through <see cref="GltfAuditorHarness"/>, not
/// duplicated: the CLI's <c>--stdout</c> report mode accepts exactly one
/// asset, so a single-invocation auditor cannot represent
/// one-report-per-asset validation.
///
/// <para>Enumeration is bounded (<c>MaxAssets</c>), every asset gets the
/// same per-invocation timeout, and any asset that cannot be verified fails
/// the whole run closed as infrastructure — partial coverage is never a
/// pass.</para>
///
/// <para><b>Gate behaviour: hybrid / severity-driven.</b> The validator
/// classifies each issue as Error, Warning, Information, or Hint. Errors
/// (broken JSON, schema violations, bad accessor data, unresolvable
/// references) map to <see cref="AuditSeverity.Error"/> and block the
/// merge; warnings (questionable but loadable content) map to
/// <see cref="AuditSeverity.Warning"/> and stay advisory; information and
/// hints map to <see cref="AuditSeverity.Info"/>. Operators adjust the gate
/// with <c>MinimumSeverity</c> or the validator's own per-code selection
/// via operator-owned <c>-c/--config</c> in <c>ExtraArguments</c>.</para>
///
/// <para><b>Exit-code convention (verified against the upstream
/// <c>lib/cmd_line.dart</c>).</b> The CLI exits <c>0</c> when the asset has
/// no errors (warnings/infos/hints may still be reported) and <c>1</c> when
/// at least one error was found — and also <c>1</c> for usage failures (no
/// input, directory input combined with <c>--stdout</c>, unknown input).
/// Both <c>0</c> and <c>1</c> are findings-producing verdicts: a completed
/// single-asset scan always writes the JSON report to stdout, while a usage
/// failure writes the version/usage banner to stderr and no report. The
/// discriminator is therefore the report itself — an exit without a
/// parseable <c>{"issues":{"messages":[…]}}</c> document on stdout fails
/// closed as infrastructure through the parser. <c>126</c>/<c>127</c>
/// (cannot execute / not found) and any other exit are infrastructure.</para>
///
/// <para><b>Version pin.</b> The CLI defines no <c>--version</c> flag, so
/// the shared <c>&lt;tool&gt; --version</c> probe can never succeed and no
/// declarative <see cref="ToolVersionPin"/> is set. The equivalent gate runs
/// per asset in the scan engine: invoking the bare binary prints
/// <c>glTF 2.0 Validator, version &lt;release&gt;</c> with usage text, and
/// the run fails closed unless that token is present and exactly equals
/// <c>ExpectedVersion</c> — a missing binary, an unrecognised banner, or a
/// foreign release is infrastructure naming the tool, never a pass.</para>
///
/// <para><b>Scope and defaults.</b> With no <c>Targets</c> configured the
/// auditor discovers <c>*.gltf</c>/<c>*.glb</c> files with a fixed
/// <c>find</c> probe (argv entries, never a shell), pruning
/// <c>.git</c>, <c>vendor</c>, <c>third_party</c>, <c>node_modules</c>,
/// <c>dist</c>, <c>build</c>, <c>out</c>, and <c>coverage</c>. Findings
/// under the default <c>ExcludePaths</c> are additionally dropped
/// post-scan. Every target — discovered or configured — must be a regular
/// file inside the worktree with a <c>.gltf</c>/<c>.glb</c> suffix:
/// absolute paths, <c>..</c> segments, leading-dash names, URI schemes
/// (remote references), symlinks, and directories fail closed, so untrusted
/// repository data can neither select a scan target outside the approved
/// roots nor smuggle a tool flag into the scan argv. Referenced
/// buffers/images resolve inside the audit sandbox via the tool's own
/// relative-URI loading; the tool never fetches remote or absolute URIs
/// (upstream skips non-relative URIs and reports <c>NON_RELATIVE_URI</c>,
/// which this auditor preserves as a finding rather than muting).</para>
///
/// <para><b>What conformance cannot say.</b> The report proves structural
/// conformance to the glTF 2.0 specification — not visual quality, not
/// runtime performance, and not that any particular engine or importer
/// (including Unity) will accept or render the asset. Findings must not be
/// read as any of those claims.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: glTF Asset Validator",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "gltf_validator",
    InstallHint = "provision the pinned gltf_validator release (see ExpectedVersion, default "
        + DefaultExpectedVersion + ") into the sandbox baseline — no distro apt package carries "
        + "gltf_validator — by fetching the versioned upstream release archive "
        + "(gltf_validator-VERSION-PLATFORM from the KhronosGroup/glTF-Validator releases) via "
        + "CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class GltfValidatorAuditor : IAuditor, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.gltf-validator";

    /// <summary>Stable auditor name carried on every finding.</summary>
    public const string AuditorName = "codeybox:gltf-validator";

    /// <summary>Bare binary invoked in the sandbox.</summary>
    public const string ToolBinary = "gltf_validator";

    /// <summary>
    /// glTF-Validator release the invocation and its findings are verified
    /// against (upstream <c>pubspec.yaml</c> version; the CLI banner prints
    /// <c>glTF 2.0 Validator, version &lt;release&gt;</c>). Operators running
    /// a different pinned build set <c>ExpectedVersion</c> in the plugin's
    /// scoped config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2.0.0-dev.3.11";

    /// <summary>
    /// Scoped-config key for explicit scan targets (comma-separated
    /// repo-relative <c>.gltf</c>/<c>.glb</c> paths). Unset → bounded asset
    /// discovery (see the class documentation). Set → discovery is skipped
    /// and the entries are contained (repo-relative, regular files, no
    /// <c>..</c>, no URI schemes, glTF suffix only).
    /// </summary>
    public const string TargetsKey = "Targets";

    /// <summary>
    /// Scoped-config key bounding how many assets one run validates
    /// (discovered or configured). Beyond the bound the run fails closed as
    /// deterministic infrastructure directing the operator to scope
    /// <c>Targets</c> — the scan never silently covers a subset.
    /// </summary>
    public const string MaxAssetsKey = "MaxAssets";

    /// <summary>
    /// Scoped-config key bounding the on-disk size (bytes) of any single
    /// scanned asset. The validator loads the whole asset into memory, so an
    /// unbounded asset is an unbounded allocation: oversize assets fail
    /// closed as deterministic infrastructure instead of being skipped or
    /// scanned.
    /// </summary>
    public const string MaxAssetBytesKey = "MaxAssetBytes";

    /// <summary>
    /// Scoped-config boolean for the validator's resource validation
    /// (buffer contents, accessor data, image decoding). Default true:
    /// disabling it narrows coverage to JSON/schema conformance, which the
    /// combined raw output records per run.
    /// </summary>
    public const string ValidateResourcesKey = "ValidateResources";

    /// <summary>Default bound on assets validated per run.</summary>
    internal const int DefaultMaxAssets = 25;

    /// <summary>Ceiling applied to a configured <c>MaxAssets</c> value.</summary>
    internal const int MaxConfiguredAssets = 500;

    /// <summary>Default per-asset size bound (128 MiB).</summary>
    internal const long DefaultMaxAssetBytes = 128L * 1024L * 1024L;

    /// <summary>Floor applied to a configured <c>MaxAssetBytes</c> value.</summary>
    internal const long MinConfiguredAssetBytes = 4096;

    /// <summary>Ceiling applied to a configured <c>MaxAssetBytes</c> value (2 GiB).</summary>
    internal const long MaxConfiguredAssetBytes = 2L * 1024L * 1024L * 1024L;

    private const int DiscoveryProbeMaxStdoutBytes = 64 * 1024;

    // Fixed discovery probe: argv entries only, never a shell, and nothing
    // interpolated from configuration. -printf emits NUL-separated
    // name/size records (%P = path without the "./" prefix) so hostile
    // filenames (spaces, newlines, quotes) cannot corrupt the record
    // protocol — NUL cannot appear in a file name.
    private static readonly IReadOnlyList<string> AssetDiscoveryArgv =
    [
        "find", ".",
        "-type", "d",
        "(", "-name", ".git",
        "-o", "-name", "vendor",
        "-o", "-name", "third_party",
        "-o", "-name", "node_modules",
        "-o", "-name", "dist",
        "-o", "-name", "build",
        "-o", "-name", "out",
        "-o", "-name", "coverage",
        ")", "-prune",
        "-o", "-type", "f",
        "(", "-iname", "*.gltf",
        "-o", "-iname", "*.glb",
        ")", "-printf", "%P\\0%s\\0",
    ];

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = scanned with no errors (warnings/infos/hints may still be
        // reported); 1 = errors found (JSON report on stdout) OR a usage
        // failure (no report — fails closed through the parser). Every
        // other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings in vendored/dependency trees and generated build output
        // describe upstream or generated assets, not the change under
        // audit. Operators re-include a path by overriding ExcludePaths in
        // scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    /// <summary>One selected asset: repo-relative path and on-disk size in bytes.</summary>
    internal sealed record AssetTarget(string Path, long SizeBytes);

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _targets = static () => [];
    private Func<int> _maxAssets = static () => DefaultMaxAssets;
    private Func<long> _maxAssetBytes = static () => DefaultMaxAssetBytes;
    private Func<bool> _validateResources = static () => true;

    /// <inheritdoc />
    public string Name => AuditorName;

    /// <inheritdoc />
    public string Kind => "tool";

    /// <summary>
    /// The validator reads local files only: upstream never fetches remote
    /// or absolute URIs (they are skipped and reported as
    /// <c>NON_RELATIVE_URI</c>), so the auditor declares no network egress.
    /// </summary>
    public AuditCapabilities Required => AuditCapabilities.None;

    /// <summary>
    /// Validates selected glTF/GLB assets one by one — each with its own
    /// bounded <c>gltf_validator -o &lt;asset&gt;</c> invocation through
    /// <see cref="GltfValidatorSingleAssetScan"/> — and aggregates the
    /// findings.
    /// </summary>
    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(context);

        var options = _optionsAccessor() ?? new ExternalToolAuditorOptions();

        // Pure configuration validation first: malformed Targets fail
        // deterministically before any sandbox probe runs.
        var configured = _targets()
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Trim())
            .ToList();
        var requested = configured.Count > 0 ? NormalizeConfiguredTargets(configured) : null;

        // Fail fast on a missing or foreign binary before enumerating
        // assets: with no usable tool nothing that follows can verify
        // anything. Each per-asset scan re-runs the same gate at its own
        // sink, so scans stay pinned however they are invoked.
        await GltfAuditorHarness.RequireBinaryPresentAsync(
            sandbox, workingDirectory, ToolBinary, options, ct).ConfigureAwait(false);
        await GltfAuditorHarness.EnsureToolVersionAsync(
            sandbox, workingDirectory, ToolBinary, options, _expectedVersion, ct).ConfigureAwait(false);

        var assets = requested is not null
            ? await CheckConfiguredTargetsAsync(
                sandbox, workingDirectory, requested, options, ct).ConfigureAwait(false)
            : await DiscoverAssetsAsync(sandbox, workingDirectory, options, ct).ConfigureAwait(false);

        if (assets.Count == 0)
            return new AuditResult(
                true,
                [],
                RawOutput: $"Tool: {ToolBinary}\nNo .gltf/.glb assets selected — nothing to validate.");

        var validateResources = _validateResources();
        var cap = Math.Max(1, options.MaxFindings);
        var combinedCap = GltfAuditorHarness.CombinedOutputCap(options);
        var findings = new List<AuditFinding>(Math.Min(assets.Count * 4, cap));
        var raw = new StringBuilder();
        var rawTruncated = false;
        var passed = true;
        var dropped = 0;

        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            var capture = new ScanReportCapture();
            var scan = new GltfValidatorSingleAssetScan(
                asset.Path, options, _expectedVersion, validateResources, capture);
            AuditResult result;
            try
            {
                result = await scan.RunAsync(sandbox, workingDirectory, context, ct)
                    .ConfigureAwait(false);
            }
            catch (AuditUnavailableException ex)
            {
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolBinary}' asset "
                    + $"'{GltfAuditorHarness.TruncateMessage(asset.Path)}': {GltfAuditorHarness.SingleLineMessage(ex.Message)}",
                    ex)
                { IsDeterministic = ex.IsDeterministic };
            }

            passed = passed && result.Passed;
            AppendAssetSection(raw, combinedCap, ref rawTruncated, asset, capture, result);

            foreach (var finding in result.Findings)
            {
                if (findings.Count >= cap)
                {
                    dropped++;
                    continue;
                }
                findings.Add(finding);
            }
        }

        if (dropped > 0)
            AppendCapped(
                raw, combinedCap, ref rawTruncated,
                $"[findings truncated: {dropped} finding(s) beyond MaxFindings {cap} were dropped]");
        if (rawTruncated)
            raw.Append($"[combined raw output truncated to {combinedCap} bytes]");

        return new AuditResult(passed, findings, RawOutput: raw.ToString());
    }

    private static void AppendAssetSection(
        StringBuilder raw,
        int combinedCap,
        ref bool rawTruncated,
        AssetTarget asset,
        ScanReportCapture capture,
        AuditResult result)
    {
        var summary = capture.Summary ?? "report summary unavailable";
        var exit = capture.ExitCode?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        AppendCapped(raw, combinedCap, ref rawTruncated, $"=== {asset.Path} ===");
        AppendCapped(
            raw, combinedCap, ref rawTruncated,
            $"report: exit={exit} {summary} resources-validated={capture.ResourcesValidated}");
        AppendCapped(raw, combinedCap, ref rawTruncated, result.RawOutput ?? string.Empty);
    }

    private static void AppendCapped(
        StringBuilder raw,
        int combinedCap,
        ref bool rawTruncated,
        string text)
    {
        if (raw.Length >= combinedCap)
        {
            rawTruncated = true;
            return;
        }
        raw.AppendLine(text);
        if (raw.Length > combinedCap + 4096)
            rawTruncated = true;
    }

    /// <summary>
    /// Pure selection of explicitly configured targets: containment,
    /// dedupe, deterministic ordering, and the asset-count bound. No
    /// sandbox interaction — malformed configuration fails here, before
    /// any probe runs.
    /// </summary>
    private List<string> NormalizeConfiguredTargets(IReadOnlyList<string> configured)
    {
        var normalized = configured
            .Select(entry => GltfAuditorHarness.ContainAssetPath(entry, $"{PluginId}:{TargetsKey}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        ThrowIfOverAssetBound(normalized.Count);
        return normalized;
    }

    private async Task<IReadOnlyList<AssetTarget>> CheckConfiguredTargetsAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> normalized,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {

        // Every configured target must be a regular file the tool can read:
        // find echoes back exactly the start points that are regular files,
        // so a missing path, a directory, or a symlink (never followed)
        // surfaces as a count mismatch and fails closed instead of silently
        // narrowing the scan — and a symlink can never smuggle an
        // out-of-worktree target past containment.
        var argv = new List<string>(normalized.Count + 4) { "find" };
        argv.AddRange(normalized);
        argv.Add("-maxdepth");
        argv.Add("0");
        argv.Add("-type");
        argv.Add("f");
        argv.Add("-printf");
        argv.Add("%p\\0%s\\0");

        var probe = await GltfAuditorHarness.ExecBoundedAsync(
            sandbox,
            ToolBinary,
            "glTF target check",
            new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = DiscoveryProbeMaxStdoutBytes,
                MaxStderrBytes = GltfAuditorHarness.ProbeOutputCap,
                KillOnOutputLimit = true,
            },
            GltfAuditorHarness.ProbeTimeoutFor(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
        if (probe.ExitCode is 126 or 127)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' asset target check could not run "
                + $"(exit {probe.ExitCode} — the 'find' helper is missing or not executable in the sandbox).");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' asset target check failed "
                + $"(exit {probe.ExitCode}) — a configured {TargetsKey} entry may name a path "
                + "outside the worktree or unreadable from the sandbox.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
        if (probe.OutputLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' asset target check produced more than "
                + $"{DiscoveryProbeMaxStdoutBytes} bytes of output — narrow {TargetsKey}.")
            { IsDeterministic = true };

        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in ParseNameSizePairs(probe.Stdout, "target check"))
            found.TryAdd(GltfAuditorHarness.ContainAssetPath(pair.Name, $"{PluginId}:{TargetsKey}"), pair.SizeBytes);
        var missing = normalized.Where(path => !found.ContainsKey(path)).ToList();
        if (missing.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' {TargetsKey} entries are not readable "
                + $"regular files ('{GltfAuditorHarness.TruncateMessage(string.Join(", ", missing))}'): missing paths, "
                + "directories, and symlinks are rejected — a scan that silently skipped them would "
                + "report a verdict on assets it never read.")
            { IsDeterministic = true };

        return normalized
            .Select(path => WithSizeGate(path, found[path]))
            .ToList();
    }

    private async Task<IReadOnlyList<AssetTarget>> DiscoverAssetsAsync(
        ISandbox sandbox,
        string workingDirectory,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        var probe = await GltfAuditorHarness.ExecBoundedAsync(
            sandbox,
            ToolBinary,
            "glTF asset discovery",
            new SandboxExec
            {
                Argv = AssetDiscoveryArgv,
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = DiscoveryProbeMaxStdoutBytes,
                MaxStderrBytes = GltfAuditorHarness.ProbeOutputCap,
                KillOnOutputLimit = true,
            },
            GltfAuditorHarness.ProbeTimeoutFor(options),
            ct).ConfigureAwait(false);

        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
        if (probe.ExitCode is 126 or 127)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' glTF asset discovery could not run "
                + $"(exit {probe.ExitCode} — the 'find' helper is missing or not executable in the sandbox).");
        if (probe.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' glTF asset discovery failed "
                + $"(exit {probe.ExitCode}). Set CodeyBox:Plugins:{PluginId}:{TargetsKey} to explicit "
                + "asset paths to skip discovery.",
                probe.ExitCode,
                probe.Stdout + "\n" + probe.Stderr);
        if (probe.OutputLimitExceeded)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' glTF asset discovery produced more than "
                + $"{DiscoveryProbeMaxStdoutBytes} bytes of output. Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit asset paths to skip discovery.")
            { IsDeterministic = true };

        var validated = new List<(string Path, long SizeBytes)>();
        foreach (var pair in ParseNameSizePairs(probe.Stdout, "discovery"))
            validated.Add((GltfAuditorHarness.ContainAssetPath(pair.Name, "discovery"), pair.SizeBytes));

        var discovered = validated
            .Select(static entry => entry.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();

        ThrowIfOverAssetBound(discovered.Count);

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in validated)
            sizes.TryAdd(entry.Path, entry.SizeBytes);
        return discovered
            .Select(path => WithSizeGate(path, sizes[path]))
            .ToList();
    }

    private void ThrowIfOverAssetBound(int count)
    {
        var max = _maxAssets();
        if (count > max)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' selected {count} glTF assets, exceeding "
                + $"the bound of {max} ({MaxAssetsKey}). Set CodeyBox:Plugins:{PluginId}:{TargetsKey} "
                + "to explicit asset paths to scope the scan — the scan never silently covers a subset.")
            { IsDeterministic = true };
    }

    private AssetTarget WithSizeGate(string path, long sizeBytes)
    {
        var max = _maxAssetBytes();
        if (sizeBytes > max)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' asset '{GltfAuditorHarness.TruncateMessage(path)}' is "
                + $"{sizeBytes} bytes, exceeding {MaxAssetBytesKey} {max}: the validator loads the "
                + "whole asset into memory, so an oversize asset is infrastructure — never a silent "
                + $"skip. Narrow {TargetsKey} or raise CodeyBox:Plugins:{PluginId}:{MaxAssetBytesKey}.")
            { IsDeterministic = true };
        return new AssetTarget(path, sizeBytes);
    }

    private static IReadOnlyList<(string Name, long SizeBytes)> ParseNameSizePairs(
        string stdout,
        string operation)
    {
        // Records are NUL-separated name/size pairs; NUL cannot appear in a
        // file name, so hostile names cannot corrupt the protocol. Anything
        // else shaped is probe chatter and fails closed.
        var records = stdout.Split('\0');
        if (records.Length > 0 && records[^1].Length == 0)
            records = records[..^1];
        if (records.Length % 2 != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' glTF {operation} returned output outside "
                + "the expected name/size record shape. The scan scope cannot be trusted, so this is "
                + "infrastructure, not a verdict on the diff.")
            { IsDeterministic = true };
        var pairs = new List<(string Name, long SizeBytes)>(records.Length / 2);
        for (var i = 0; i < records.Length; i += 2)
        {
            if (!long.TryParse(
                    records[i + 1].Trim(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var size)
                || size < 0)
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolBinary}' glTF {operation} returned an "
                    + "unparseable asset size. The scan scope cannot be trusted, so this is "
                    + "infrastructure, not a verdict on the diff.")
                { IsDeterministic = true };
            pairs.Add((records[i], size));
        }
        return pairs;
    }

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _targets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[TargetsKey]);
        _maxAssets = () => ResolveMaxAssets(scoped[MaxAssetsKey]);
        _maxAssetBytes = () => ResolveMaxAssetBytes(scoped[MaxAssetBytesKey]);
        _validateResources = () => !bool.TryParse(scoped[ValidateResourcesKey], out var validate) || validate;
        context.Logger.LogInformation(
            "GltfValidatorAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static int ResolveMaxAssets(string? raw)
    {
        if (int.TryParse(
                raw?.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var configured)
            && configured > 0)
            return Math.Min(configured, MaxConfiguredAssets);
        return DefaultMaxAssets;
    }

    private static long ResolveMaxAssetBytes(string? raw)
    {
        if (long.TryParse(
                raw?.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var configured)
            && configured > 0)
            return Math.Clamp(configured, MinConfiguredAssetBytes, MaxConfiguredAssetBytes);
        return DefaultMaxAssetBytes;
    }
}
