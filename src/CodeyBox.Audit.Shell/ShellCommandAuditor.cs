using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// Audits the working tree by running an arbitrary command inside the
/// sandbox. Most commands follow the shell-style "exit 0 = good" contract:
/// exit code 0 passes, and non-zero fails with stdout/stderr captured as a
/// single Error finding. If the top-level tool is confirmed missing before
/// the command runs, the auditor usually emits a non-blocking Info finding;
/// callers can raise that to Warning for coverage-sensitive tools, and
/// BuildTestGate auditors emit Error because missing deterministic build/test
/// evidence must block dependent auditors.
/// A command-specific result classifier can refine non-zero exits without
/// making this generic shell runner aware of language- or tool-specific
/// output formats.
///
/// By default does not need agent credentials, so ordinary shell auditors run
/// in the credential-free audit sandbox. Trusted preset/config authors can
/// opt into additional sandbox capabilities for tool-specific needs such as
/// package-registry network access; do not request agent credentials for
/// repository-controlled commands unless that exposure is intentional.
///
/// <para>Every invocation receives <c>CODEYBOX_AUDIT_TARGET</c> and
/// <c>CODEYBOX_WORK_ITEM_ID</c>. Plan-target invocations additionally receive
/// <c>CODEYBOX_PLAN_ARTIFACT_PATH</c>, which names a read-only-by-contract JSON
/// snapshot whose path is unique per work item, review iteration, and auditor
/// name — so concurrent plan-target auditors never share a path. The snapshot
/// exists only for the command's duration and is removed in a <c>finally</c>
/// before <see cref="RunAsync"/> returns, on every exit path (success, a
/// failed/partial materialisation, a classified failure, an exception, or
/// cancellation of the auditor command). Code-target invocations do not receive
/// the artifact-path variable.</para>
/// </summary>
public sealed class ShellCommandAuditor : IAuditor, IShellAuditorArgvProvider
{
    private readonly ShellCommandAuditorOptions _opts;

    public ShellCommandAuditor(ShellCommandAuditorOptions opts)
    {
        if (opts.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(opts));
        _opts = opts;
    }

    public string Name => _opts.Name;
    public string Kind => "shell";
    public AuditCapabilities Required => _opts.Required;
    public IReadOnlySet<AuditTarget> Targets => _opts.Targets;
    public bool CanShortCircuitOnBlockingFinding => _opts.CanShortCircuitOnBlockingFinding;

    public string? SelfReviewGuidance
    {
        get
        {
            if (Name.Contains("build", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains("format", StringComparison.OrdinalIgnoreCase))
            {
                return "run build (warnings-as-errors) + formatter before committing";
            }
            return null;
        }
    }

    public AuditorRole Role => _opts.Role;
    public BuildTestGateEvidence BuildTestGateEvidence => _opts.Role == AuditorRole.BuildTestGate
        ? _opts.BuildTestGateEvidence
        : BuildTestGateEvidence.None;

    /// <summary>
    /// The argv this auditor invokes. Exposed so the work-phase prompt builder
    /// can advise the agent to run these checks itself before committing,
    /// pre-empting iter-1 mechanical findings (format, lint, build-WaE).
    /// </summary>
    public IReadOnlyList<string> Argv => _opts.Argv;

    /// <summary>Marks this auditor's executable as mandatory infrastructure.</summary>
    public ShellCommandAuditor WithRequiredToolAvailability()
        => new(_opts with { MissingToolBehavior = MissingToolBehavior.Unavailable });

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        var toolName = _opts.ToolName ?? _opts.Argv[0];
        if (await IsDirectToolMissingAsync(sandbox, workingDirectory, toolName, ct))
            return MissingToolResult(toolName, string.Empty);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEYBOX_AUDIT_TARGET"] = context.EffectiveTarget.Value,
            ["CODEYBOX_WORK_ITEM_ID"] = context.WorkItemId.ToString(),
        };
        DotnetCliHomeConventions.ApplyIfDotnetInvocation(_opts.Argv, workingDirectory, environment);

        // Dispatch on the explicit review strategy; an unhandled future target is
        // rejected in Classify rather than silently run as a code audit.
        return AuditTargetSemantics.Classify(context.EffectiveTarget) == AuditReviewStrategy.PlanReview
            ? await RunPlanTargetAsync(sandbox, workingDirectory, context, environment, toolName, ct)
            : await ExecAndClassifyAsync(sandbox, workingDirectory, context, environment, toolName, ct);
    }

    private async Task<AuditResult> RunPlanTargetAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        Dictionary<string, string> environment,
        string toolName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.PlanArtifact))
        {
            return new AuditResult(false, [new AuditFinding(
                Name,
                AuditSeverity.Error,
                "no plan artifact to review",
                "The plan-review context carried no PLAN artifact.")]);
        }

        var planArtifactPath = BuildPlanArtifactPath(context);
        // Set before the write starts so the finally still removes a partially
        // written snapshot if the write exec throws mid-stream.
        var mustRemove = true;
        try
        {
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "umask 077; rm -f -- \"$1\"; cat > \"$1\"; chmod 400 \"$1\"", "sh", planArtifactPath],
                WorkingDirectory = workingDirectory,
                Stdin = context.PlanArtifact,
            }, ct);

            if (!write.Success)
            {
                return new AuditResult(false, [new AuditFinding(
                    Name,
                    AuditSeverity.Error,
                    "failed to materialise plan artifact",
                    DescriptionOutput(write).TrimEnd())],
                    RawOutput: CombinedOutput(write));
            }

            environment["CODEYBOX_PLAN_ARTIFACT_PATH"] = planArtifactPath;
            var result = await ExecAndClassifyAsync(sandbox, workingDirectory, context, environment, toolName, ct);

            // Explicit in-band cleanup so a removal failure on the happy path is
            // surfaced as a blocking finding (the snapshot must not outlive the run).
            var cleanup = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["rm", "-f", "--", planArtifactPath],
                WorkingDirectory = workingDirectory,
            }, ct);
            mustRemove = false;
            if (!cleanup.Success)
            {
                return new AuditResult(false, [new AuditFinding(
                    Name,
                    AuditSeverity.Error,
                    "failed to clean up plan artifact",
                    DescriptionOutput(cleanup).TrimEnd())],
                    RawOutput: CombinedOutput(cleanup));
            }

            return result;
        }
        finally
        {
            // Guarantee removal on every abnormal exit (failed/partial write,
            // exception, or cancellation). rm -f is idempotent, and mustRemove is
            // cleared once the happy-path removal succeeds so it is not repeated.
            if (mustRemove)
                await TryRemovePlanArtifactAsync(sandbox, workingDirectory, planArtifactPath);
        }
    }

    private async Task<AuditResult> ExecAndClassifyAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        IReadOnlyDictionary<string, string> environment,
        string toolName,
        CancellationToken ct)
    {
        // The exact vector the sandbox receives. Diagnostics (findings and the
        // result classifier) report THIS vector — never the pre-wrap configured
        // argv — so the logged command string is byte-identical to what actually
        // executed. A diagnostic that omits a wrapper or an appended argument
        // makes invocation refusals (e.g. VSTest rejecting a test source) much
        // harder to identify.
        var execArgv = BuildExecArgv();
        var maxAttempts = Math.Max(1, _opts.TransportRetryMaxAttempts);
        var timeProvider = _opts.TimeProvider ?? TimeProvider.System;
        for (var attempt = 1; ; attempt++)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = execArgv,
                WorkingDirectory = workingDirectory,
                ExtraEnvironment = environment,
            }, ct);

            var combinedOutput = CombinedOutput(result);

            if (result.ExitCode == 0 && !result.ExecutionUnavailable)
                return new AuditResult(true, [], RawOutput: combinedOutput);

            // A dropped exec channel produced no verdict: the command did not
            // run to completion, so there is nothing attributable to the code
            // under review. Retry with backoff rather than recording a finding
            // against the diff — a transport-shaped finding would burn a rework
            // iteration on an unfixable outcome and park the item as though the
            // diff were at fault. Only when the bounded retries are exhausted
            // does this surface, as infrastructure (AuditUnavailableException),
            // with no finding recorded from any attempt.
            if (ExecTransportFailure.IsTransportFailure(result))
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(ComputeTransportRetryDelay(attempt), timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                throw TransportFailureExhausted(result, combinedOutput, maxAttempts);
            }

            return await ClassifyFailedCommandAsync(
                sandbox, workingDirectory, context, toolName, execArgv, result, combinedOutput, ct);
        }
    }

    private async Task<AuditResult> ClassifyFailedCommandAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        string toolName,
        IReadOnlyList<string> execArgv,
        SandboxExecResult result,
        string combinedOutput,
        CancellationToken ct)
    {
        var finding = BuildCommandFinding(result, toolName, execArgv);
        if (_opts.ResultClassifier is not null)
        {
            var classified = _opts.ResultClassifier.ClassifyFailedCommand(new AuditResultClassificationContext(
                Name,
                _opts.Argv,
                result,
                combinedOutput,
                finding,
                execArgv));
            if (classified is not null)
            {
                if (_opts.ResultClassifier is DotnetTestCommandResultClassifier
                    && _opts.TestFailureAttributionOptions is not null)
                {
                    var parsed = DotnetTestOutputParser.Parse(Name, combinedOutput);
                    var attributions = await DotnetTestFailureAttributionRunner.AttributeAsync(
                        sandbox,
                        workingDirectory,
                        context,
                        Name,
                        _opts.Argv,
                        parsed.FailedTestNames,
                        parsed.HitFailureParseCap,
                        _opts.TestFailureAttributionOptions,
                        ct);
                    return classified with { TestFailureAttributions = attributions };
                }

                return classified;
            }
        }

        return new AuditResult(false, [finding], RawOutput: combinedOutput);
    }

    /// <summary>
    /// Exponential backoff between exec-transport retries: the base delay
    /// doubling per attempt, capped at the configured maximum. Overflow-safe:
    /// doubling stops before it could exceed <see cref="long.MaxValue"/>.
    /// </summary>
    private TimeSpan ComputeTransportRetryDelay(int attempt)
    {
        var baseDelay = _opts.TransportRetryBaseDelay;
        if (baseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;
        var maxDelay = _opts.TransportRetryMaxDelay > TimeSpan.Zero
            ? _opts.TransportRetryMaxDelay
            : baseDelay;
        var delayTicks = baseDelay.Ticks;
        for (var i = 1; i < attempt && delayTicks <= long.MaxValue / 2; i++)
            delayTicks *= 2;
        return new TimeSpan(Math.Min(delayTicks, maxDelay.Ticks));
    }

    /// <summary>
    /// Builds the infrastructure failure for exhausted exec-transport retries.
    /// Deliberately non-deterministic (a plain retry may succeed), so the
    /// pipeline routes it to the infrastructure failure path instead of
    /// spending deterministic-configuration handling on it — and, crucially,
    /// records no finding, so the audit record distinguishes "this auditor
    /// could not run" from "this auditor ran and found a problem".
    /// </summary>
    private AuditUnavailableException TransportFailureExhausted(
        SandboxExecResult result,
        string combinedOutput,
        int maxAttempts)
    {
        var diagnostic = ExecTransportFailure.FirstDiagnosticLine(combinedOutput);
        var evidence = diagnostic.Length == 0
            ? "exec-transport diagnostic"
            : $"'{diagnostic}'";
        return new AuditUnavailableException(
            $"could-not-verify: auditor '{Name}' could not run: the sandbox exec transport dropped " +
            $"(exit {ExecTransportFailure.TransportFailureExitCode} with {evidence}) on all {maxAttempts} attempt(s). " +
            "No verdict was produced, so no finding was recorded.",
            result.ExitCode,
            combinedOutput);
    }

    /// <summary>
    /// The argv actually dispatched to the sandbox. Without
    /// <see cref="ShellCommandAuditorOptions.SelfHealNuGetHome"/> this is the
    /// configured argv verbatim. When it is set (a dotnet-specific opt-in), the
    /// argv is wrapped by <see cref="NuGetHomeSelfHeal.WrapDotnetInvocation"/> so
    /// restore survives a root-owned <c>~/.nuget</c> -- a single <c>sh -c</c> that
    /// runs the self-heal preamble then <c>exec "$@"</c>s the real command with
    /// its arguments intact. Findings and the result classifier report this
    /// executed vector (not the pre-wrap configured argv), so the logged command
    /// string is byte-identical to what the sandbox received.
    /// </summary>
    private IReadOnlyList<string> BuildExecArgv()
        => _opts.SelfHealNuGetHome
            ? NuGetHomeSelfHeal.WrapDotnetInvocation(_opts.Argv)
            : _opts.Argv;

    private string BuildPlanArtifactPath(AuditContext context)
    {
        // Unique per work item, review iteration, AND auditor name so concurrent
        // plan-target auditors sharing an item/iteration never collide on one path.
        var safeName = SanitizePathToken(Name);
        return $"/tmp/codeybox-plan-artifact-{context.WorkItemId}-{context.Iteration}-{safeName}.json";
    }

    private static string SanitizePathToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "auditor" : sb.ToString();
    }

    private static async Task TryRemovePlanArtifactAsync(ISandbox sandbox, string workingDirectory, string path)
    {
        try
        {
            // Detached from the request token so a cancelled auditor still removes
            // its snapshot. Best-effort: this runs on the exceptional unwind where
            // there is no AuditResult to attach a cleanup-failure finding to, so a
            // removal failure must not mask the original outcome.
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["rm", "-f", "--", path],
                WorkingDirectory = workingDirectory,
            }, CancellationToken.None);
        }
        catch
        {
            // Intentionally swallowed on the abnormal-exit cleanup path (see above).
        }
    }

    private AuditFinding BuildCommandFinding(SandboxExecResult result, string toolName, IReadOnlyList<string> execArgv)
    {
        var description = DescriptionOutput(result);

        // Exit 127 is only non-blocking when it is confirmed to be the
        // auditor's tool missing from the sandbox. Some tools, notably npm,
        // propagate exit 127 from repository-controlled scripts; those remain
        // blocking command failures.
        var missingTool = IsConfirmedMissingTopLevelTool(result);
        if (missingTool && _opts.MissingToolBehavior == MissingToolBehavior.Unavailable)
        {
            throw new AuditUnavailableException(
                $"Required audit tool '{toolName}' is not installed in the sandbox.",
                result.ExitCode,
                CombinedOutput(result));
        }
        var severity = missingTool
            ? MissingToolSeverity()
            : AuditSeverity.Error;
        // The title is a bounded summary, never the command: a wrapped
        // invocation (e.g. the NuGet-home self-heal preamble) would otherwise
        // dump a multi-line script into every audit-progress record. The exact
        // executed command belongs in the description, where the transcript
        // already lives.
        var title = missingTool
            ? $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)"
            : $"command exited {result.ExitCode}";

        if (missingTool)
        {
            return new AuditFinding(
                AuditorName: Name,
                Severity: severity,
                Title: title,
                Description: description.TrimEnd());
        }

        var commandLine = string.Join(' ', execArgv);
        var output = description.TrimEnd();
        var fullDescription = output.Length == 0
            ? $"Command: {commandLine}"
            : $"Command: {commandLine}\n\n{output}";
        return new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: fullDescription);
    }

    private static string CombinedOutput(SandboxExecResult result)
    {
        var stdout = result.Stdout;
        var stderr = result.Stderr;
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;
        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;
        return stdout + "\n" + stderr;
    }

    private static string DescriptionOutput(SandboxExecResult result)
        => string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;

    private async Task<bool> IsDirectToolMissingAsync(
        ISandbox sandbox,
        string workingDirectory,
        string toolName,
        CancellationToken ct)
    {
        if (_opts.TreatExit127AsMissingTool is not null || string.IsNullOrWhiteSpace(toolName))
            return false;

        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "command -v \"$1\" >/dev/null 2>&1", "sh", toolName],
            WorkingDirectory = workingDirectory,
        }, ct);

        return probe.ExitCode != 0;
    }

    private bool IsConfirmedMissingTopLevelTool(SandboxExecResult result)
    {
        return result.ExitCode == 127 && _opts.TreatExit127AsMissingTool == true;
    }

    private AuditResult MissingToolResult(string toolName, string rawOutput)
    {
        if (_opts.MissingToolBehavior == MissingToolBehavior.Unavailable)
        {
            throw new AuditUnavailableException(
                $"Required audit tool '{toolName}' is not installed in the sandbox.");
        }

        var finding = new AuditFinding(
            AuditorName: Name,
            Severity: MissingToolSeverity(),
            Title: $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)",
            Description: $"The auditor command was not run because '{toolName}' is not available in the audit sandbox.");
        return new AuditResult(false, [finding], RawOutput: rawOutput);
    }

    private AuditSeverity MissingToolSeverity()
        => _opts.Role == AuditorRole.BuildTestGate
            ? AuditSeverity.Error
            : _opts.MissingToolSeverity ?? AuditSeverity.Info;
}

public enum MissingToolBehavior
{
    Finding,
    Unavailable,
}

public sealed record ShellCommandAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }

    public string? ToolName { get; init; }
    public bool? TreatExit127AsMissingTool { get; init; }
    public IAuditResultClassifier? ResultClassifier { get; init; }
    public AuditCapabilities Required { get; init; } = AuditCapabilities.None;
    public AuditSeverity? MissingToolSeverity { get; init; }
    public MissingToolBehavior MissingToolBehavior { get; init; } = MissingToolBehavior.Finding;
    /// <summary>
    /// Review targets for this command. Empty configuration is materialised as
    /// Code-only by composers. Plan commands read their artifact through
    /// <c>CODEYBOX_PLAN_ARTIFACT_PATH</c>; the path is unique per work item,
    /// review iteration, and auditor name, is removed in a <c>finally</c> after
    /// the command on every exit path, and is absent for Code runs.
    /// </summary>
    public IReadOnlySet<AuditTarget> Targets { get; init; } = AuditTargets.CodeOnly;
    public TestFailureAttributionOptionsSnapshot? TestFailureAttributionOptions { get; init; }
    public bool CanShortCircuitOnBlockingFinding { get; init; }
    public AuditorRole Role { get; init; } = AuditorRole.None;
    public BuildTestGateEvidence BuildTestGateEvidence { get; init; } = BuildTestGateEvidence.None;

    /// <summary>
    /// When true and the command is a <c>dotnet</c> invocation, wrap it in the
    /// shared <see cref="NuGetHomeSelfHeal"/> preamble so restore survives a
    /// root-owned <c>~/.nuget</c> on unprivileged build hosts. Off by default; a
    /// no-op on a healthy home and for non-dotnet commands.
    /// </summary>
    public bool SelfHealNuGetHome { get; init; }

    /// <summary>
    /// Total exec attempts (initial try plus retries) when the sandbox exec
    /// transport drops mid-command (exit 255 with an abnormal-closure
    /// diagnostic). A dropped channel produced no verdict, so a plain retry
    /// usually recovers; only when every attempt drops does the run surface as
    /// infrastructure with no finding recorded. Must be at least 1; smaller
    /// values behave as 1.
    /// </summary>
    public int TransportRetryMaxAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay between exec-transport retries. The actual wait doubles per
    /// attempt (base, 2x base, ...) up to <see cref="TransportRetryMaxDelay"/>.
    /// </summary>
    public TimeSpan TransportRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cap for the exponential exec-transport retry backoff.
    /// </summary>
    public TimeSpan TransportRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clock used for exec-transport retry backoff delays. Null (the default)
    /// uses <see cref="TimeProvider.System"/>; tests inject a fake to keep
    /// retry tests deterministic.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }
}
