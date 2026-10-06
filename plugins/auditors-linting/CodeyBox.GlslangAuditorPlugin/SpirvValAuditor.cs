using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// SPIR-V module auditor validating explicitly selected candidate
/// binaries with <c>spirv-val</c> (SPIRV-Tools) on the shared shader
/// auditor family: each module is validated with its own bounded
/// <c>spirv-val --target-env &lt;env&gt; &lt;module&gt;</c> invocation
/// through <see cref="SpirvValSingleModuleScan"/> (which extends the
/// shared <see cref="ExternalToolAuditorBase"/> and therefore inherits
/// its sandboxed invocation, bounded timeout, per-stream output caps,
/// exit-code classification, severity mapping, finding identity, and
/// configuration), and this orchestrator aggregates the per-module
/// results. Target environments resolve through the shared
/// <see cref="ShaderValidationSupport.ValidateTargetEnvironment"/>, and
/// module selection through
/// <see cref="SpirvValidationSupport.ResolveModules"/>, so the family
/// policy cannot drift between the source and binary validators.
///
/// <para><b>Gate behaviour: hybrid / severity-driven — not blocking on
/// every finding.</b> Diagnostics the tool reports at <c>error</c> level
/// (invalid modules) map to <see cref="AuditSeverity.Error"/> and fail
/// the audit; <c>warning</c> maps to <see cref="AuditSeverity.Warning"/>
/// and is advisory. <c>MinimumSeverity</c> only drops findings, it never
/// raises them: there is no mode in which a warning fails the audit.</para>
///
/// <para><b>Exit-code convention (checked against the spirv-val contract
/// for the pinned release: 0 = valid, 1 = invalid or tool error).</b>
/// <c>0</c> = ran: valid (silent — inputs were verified present,
/// nonempty, and magic-checked first, so silence is a checked pass).
/// <c>1</c> = module diagnostics (findings) OR a tool-operation failure
/// (missing input, unsupported environment, second operand — the
/// diagnostic text is the discriminator, and operation failures fail
/// closed as infrastructure through the parser). <c>126</c>/<c>127</c> =
/// cannot execute / not found — infrastructure. Anything else is an
/// unknown convention and fails loudly as infrastructure rather than
/// being guessed.</para>
///
/// <para><b>Version pin.</b> The validation rules change between releases,
/// so findings are only meaningful from the build the auditor was checked
/// against. SPIRV-Tools versions its releases <c>vYYYY.N</c> (two
/// components — e.g. <c>spirv-val --version</c> prints <c>SPIRV-Tools
/// v2025.1 unknown hash, …</c>), which the shared three-component
/// extraction cannot represent, so the pin is enforced by
/// <see cref="SpirvValHarness.EnsureToolVersionAsync"/> instead of a
/// declarative pin. A missing binary, an unrecognised version string, or
/// a version other than <c>ExpectedVersion</c> is an infrastructure
/// failure naming the tool — never a pass, never a finding.</para>
///
/// <para><b>Scope and defaults.</b> The auditor never walks the tree:
/// every validated module is an explicit operator-configured
/// <c>SpirvTargets</c> entry (a repo-relative <c>.spv</c> binary),
/// resolved inside the audited worktree with containment and the
/// binary-only extension gate. Each module is validated with its own
/// invocation — the tool accepts exactly one binary per run — and any
/// module that cannot be verified fails the whole run closed as
/// infrastructure: partial coverage is never a pass. Validation only: no
/// spirv-opt/remap/fuzz/rewrite flag is ever passed, no output is
/// emitted, and nothing in the repository is modified. An empty target
/// list, a missing or empty target, an overlarge target, or a target
/// without the SPIR-V magic number is infrastructure, not a pass. A
/// validator position (<c>line N</c>) addresses an instruction inside the
/// binary, never a source line of any repository file, so findings carry
/// the module path with no line number and the position stays in the
/// message verbatim.</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: SPIR-V Module Validation",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "spirv-val",
    AptPackage = "spirv-tools",
    InstallHint = "provision spirv-val into the sandbox baseline via apt (AptPackage spirv-tools) or "
        + "the versioned upstream SPIRV-Tools release binary — then pin the exact release your baseline "
        + "installs (see ExpectedVersion, default " + DefaultExpectedVersion + ") and keep the two in "
        + "step through CodeyBox:MultipassExtraRuncmd / CodeyBox:Incus:ExtraRuncmd or ExecutableProvisions")]
public sealed class SpirvValAuditor : IAuditor, IPluginInitializer
{
    /// <summary>Plugin id used in <c>Plugins:Enabled</c> and the scoped-config section.</summary>
    public const string PluginId = "codeybox.spirv-val";

    /// <summary>Stable auditor name carried on every finding.</summary>
    public const string AuditorName = "codeybox:spirv-val";

    /// <summary>Bare binary invoked in the sandbox.</summary>
    public const string ToolBinary = "spirv-val";

    /// <summary>
    /// SPIRV-Tools release the invocation and its findings are checked
    /// against (upstream tag <c>v2025.1</c>; <c>spirv-val --version</c>
    /// prints <c>SPIRV-Tools v2025.1 …</c>). Operators running a different
    /// pinned build set <c>ExpectedVersion</c> in the plugin's scoped
    /// config to match what they provisioned.
    /// </summary>
    public const string DefaultExpectedVersion = "2025.1";

    /// <summary>
    /// Scoped-config key for the candidate SPIR-V modules under validation
    /// (comma-separated repo-relative entries, at least one required). Each
    /// entry names an assembled <c>.spv</c> binary; duplicates are folded.
    /// </summary>
    public const string SpirvTargetsKey = "SpirvTargets";

    /// <summary>
    /// Scoped-config key for the target environment passed to
    /// <c>--target-env</c> (e.g. <c>vulkan1.0</c>, <c>vulkan1.3</c>,
    /// <c>spv1.6</c>). Always passed explicitly; unset means
    /// <c>vulkan1.0</c>. Values the tool does not recognise fail closed as
    /// infrastructure — the tool is the environment oracle.
    /// </summary>
    public const string TargetEnvironmentKey = "TargetEnvironment";

    /// <summary>
    /// Scoped-config key bounding the on-disk size (bytes) of any single
    /// scanned module. The validator loads the whole module into memory,
    /// so an unbounded module is an unbounded allocation: oversize modules
    /// fail closed as deterministic infrastructure instead of being
    /// skipped or scanned.
    /// </summary>
    public const string MaxModuleBytesKey = "MaxModuleBytes";

    /// <summary>Default per-module size bound (64 MiB).</summary>
    internal const long DefaultMaxModuleBytes = 64L * 1024L * 1024L;

    /// <summary>Floor applied to a configured <c>MaxModuleBytes</c> value.</summary>
    internal const long MinConfiguredModuleBytes = 1024;

    /// <summary>Ceiling applied to a configured <c>MaxModuleBytes</c> value (2 GiB).</summary>
    internal const long MaxConfiguredModuleBytes = 2L * 1024L * 1024L * 1024L;

    // Bounded probe proving every configured module is a readable regular
    // SPIR-V binary under the size cap before the scan runs: file names ride
    // as "$@" (never through a shell string), one name per argv entry, and
    // the size cap interpolates as a validated integer literal (never
    // operator text). Distinct exits name the cause: 11 = not a regular
    // file (directory, or vanished after the presence check), 12 = empty
    // (validates vacuously — silence would prove nothing), 13 = unreadable,
    // 14 = over the size cap, 15 = missing SPIR-V magic (not a module).
    private const string ModuleCheckScriptTemplate = """
        max={0}
        for f in "$@"; do
          test -f "./$f" || exit 11
          test -s "./$f" || exit 12
          sz=$(wc -c <"./$f") || exit 13
          [ "$((sz))" -le "$max" ] || exit 14
          od -A n -t x1 -N 4 -- "./$f" | grep -q "{1}" || exit 15
        done
        exit 0
        """;

    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        // 0 = ran valid (silent — inputs were verified present, nonempty,
        // and magic-checked first); 1 = module diagnostics (findings) OR a
        // tool-operation failure (no diagnostic — fails closed through the
        // parser). Every other exit is infrastructure.
        FindingsExitCodes = new HashSet<int> { 0, 1 },
        // Findings under vendored and dependency trees describe upstream
        // blobs, not the change under audit. Operators re-include a path
        // by overriding ExcludePaths in scoped config.
        ExcludePaths = ["vendor/", "third_party/", "node_modules/"],
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;
    private Func<string?> _expectedVersion = static () => DefaultExpectedVersion;
    private Func<IReadOnlyList<string>> _spirvTargets = static () => [];
    private Func<string?> _targetEnvironment = static () => null;
    private Func<long> _maxModuleBytes = static () => DefaultMaxModuleBytes;

    /// <inheritdoc />
    public string Name => AuditorName;

    /// <inheritdoc />
    public string Kind => "tool";

    /// <summary>
    /// The scan validates explicit local files with no fetches, so the
    /// auditor needs no network egress and runs in the most restrictive
    /// sandbox. Anything the tool cannot resolve locally fails closed as
    /// infrastructure under this sandbox rather than silently passing.
    /// </summary>
    public AuditCapabilities Required => AuditCapabilities.None;

    /// <summary>
    /// Validates selected SPIR-V modules one by one — each with its own
    /// bounded <c>spirv-val --target-env &lt;env&gt; &lt;module&gt;</c>
    /// invocation through <see cref="SpirvValSingleModuleScan"/> — and
    /// aggregates the findings. Any module that cannot be verified fails
    /// the whole run closed as infrastructure: partial coverage is never
    /// a pass.
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

        // Pure configuration validation first (deterministic, no sandbox
        // needed): malformed targets, an unusable environment, or any
        // ExtraArguments entry fail here before any probe runs.
        var modules = SpirvValidationSupport.ResolveModules(
            _spirvTargets(), PluginId, SpirvTargetsKey);
        var environment = ShaderValidationSupport.ValidateTargetEnvironment(
            _targetEnvironment(),
            ShaderValidationSupport.DefaultTargetEnvironment,
            $"{PluginId}:{TargetEnvironmentKey}");
        if (options.ExtraArguments.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: auditor '{Name}' was configured with ExtraArguments "
                + $"('{SpirvValHarness.TruncateMessage(string.Join(' ', options.ExtraArguments))}') — spirv-val flags "
                + $"and operands are managed by the auditor ({SpirvTargetsKey}, {TargetEnvironmentKey}); "
                + "ExtraArguments has no supported spelling here. Remove it.")
            { IsDeterministic = true };

        // Fail fast on a missing or foreign binary before touching the
        // tree: with no usable tool nothing that follows can verify
        // anything. Each per-module scan re-runs the same gate at its own
        // sink, so scans stay pinned however they are invoked.
        await SpirvValHarness.RequireBinaryPresentAsync(
            sandbox, workingDirectory, ToolBinary, options, ct).ConfigureAwait(false);
        await SpirvValHarness.EnsureToolVersionAsync(
            sandbox, workingDirectory, ToolBinary, options, _expectedVersion, ct).ConfigureAwait(false);

        await CheckConfiguredModulesAsync(
            sandbox, workingDirectory, modules.Modules, options, ct).ConfigureAwait(false);

        var cap = Math.Max(1, options.MaxFindings);
        var combinedCap = SpirvValHarness.CombinedOutputCap(options);
        var findings = new List<AuditFinding>(Math.Min(modules.Modules.Count * 4, cap));
        var raw = new StringBuilder();
        var rawTruncated = false;
        var passed = true;
        var dropped = 0;

        foreach (var module in modules.Modules)
        {
            ct.ThrowIfCancellationRequested();
            var capture = new SpirvScanCapture();
            var scan = new SpirvValSingleModuleScan(
                module, environment, options, _expectedVersion, capture);
            AuditResult result;
            try
            {
                result = await scan.RunAsync(sandbox, workingDirectory, context, ct)
                    .ConfigureAwait(false);
            }
            catch (AuditUnavailableException ex)
            {
                throw new AuditUnavailableException(
                    $"could-not-verify: audit tool '{ToolBinary}' module "
                    + $"'{SpirvValHarness.TruncateMessage(module)}': {SpirvValHarness.SingleLineMessage(ex.Message)}",
                    ex)
                { IsDeterministic = ex.IsDeterministic };
            }

            passed = passed && result.Passed;
            AppendModuleSection(raw, combinedCap, ref rawTruncated, module, capture, result);

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

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        _expectedVersion = () => scoped[ToolVersionPin.ExpectedVersionKey];
        _spirvTargets = () => ExternalToolAuditorOptions.SplitCommaSeparatedList(scoped[SpirvTargetsKey]);
        _targetEnvironment = () => scoped[TargetEnvironmentKey];
        _maxModuleBytes = () => ReadMaxModuleBytes(scoped[MaxModuleBytesKey]);
        context.Logger.LogInformation(
            "SpirvValAuditor initialized: pluginId={PluginId}", context.PluginId);
        return Task.CompletedTask;
    }

    private static void AppendModuleSection(
        StringBuilder raw,
        int combinedCap,
        ref bool rawTruncated,
        string module,
        SpirvScanCapture capture,
        AuditResult result)
    {
        var exit = capture.ExitCode?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        AppendCapped(raw, combinedCap, ref rawTruncated, $"=== {module} ===");
        AppendCapped(
            raw, combinedCap, ref rawTruncated,
            $"spirv-val exit={exit} findings={result.Findings.Count}");
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

    private async Task CheckConfiguredModulesAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> modules,
        ExternalToolAuditorOptions options,
        CancellationToken ct)
    {
        // Liveness: every configured module must exist in the audited tree.
        // A missing module is a broken configuration (or a tree that
        // drifted under the config) — unavailable, never a pass.
        var operands = modules.Distinct(StringComparer.Ordinal).ToList();
        var present = await SpirvValHarness.ProbeFilesPresentAsync(
            sandbox, workingDirectory, ToolBinary, operands, options, ct).ConfigureAwait(false);
        var missing = operands
            .Except(present, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (missing.Count > 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' input verification failed — "
                + $"{missing.Count} configured file(s) not found in the audited worktree: "
                + $"'{SpirvValHarness.TruncateMessage(string.Join("', '", missing))}'. The check did not run, "
                + "so this is infrastructure, not a verdict on the change.")
            { IsDeterministic = true };

        // Shape: every module must be a readable regular file under the
        // size cap carrying the SPIR-V magic number. An empty or
        // non-module file validates vacuously or not at all, so a silent
        // tool success afterward would prove nothing — fail closed naming
        // the cause.
        var maxBytes = _maxModuleBytes();
        var script = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ModuleCheckScriptTemplate,
            maxBytes,
            SpirvValidationSupport.SpirvMagicProbeWords);
        var check = await SpirvValHarness.ExecBoundedAsync(
            sandbox,
            ToolBinary,
            "input verification",
            new SandboxExec
            {
                Argv = ["sh", "-c", script, "sh", .. operands],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = SpirvValHarness.ProbeOutputCap,
                MaxStderrBytes = SpirvValHarness.ProbeOutputCap,
                KillOnOutputLimit = true,
            },
            SpirvValHarness.ProbeTimeoutFor(options),
            ct).ConfigureAwait(false);
        if (check.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(check.ExitCode);
        if (check.ExitCode != 0)
            throw new AuditUnavailableException(
                $"could-not-verify: audit tool '{ToolBinary}' input verification failed — "
                + DescribeModuleCheckFailure(check.ExitCode, maxBytes)
                + " The check did not run, so this is infrastructure, not a verdict on the change.",
                check.ExitCode,
                check.Stdout + "\n" + check.Stderr);
    }

    private static string DescribeModuleCheckFailure(int exitCode, long maxBytes)
        => exitCode switch
        {
            11 => "a configured module is not a readable regular file (a directory, or it vanished "
                + "after the presence check).",
            12 => "a configured module file is empty. Empty modules validate vacuously, so a silent "
                + "tool success would prove nothing.",
            13 => "a configured module could not be read for size verification.",
            14 => $"a configured module is larger than {MaxModuleBytesKey} ({maxBytes} bytes). Oversize "
                + "modules fail closed instead of being skipped or scanned unbounded.",
            15 => "a configured module is not a SPIR-V binary (missing magic number 0x07230203). "
                + "Only assembled .spv modules validate here.",
            _ => $"the module check helper failed (exit {exitCode}).",
        };

    private static long ReadMaxModuleBytes(string? value)
    {
        if (long.TryParse(
                value?.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            && parsed > 0)
            return Math.Clamp(parsed, MinConfiguredModuleBytes, MaxConfiguredModuleBytes);
        return DefaultMaxModuleBytes;
    }
}
