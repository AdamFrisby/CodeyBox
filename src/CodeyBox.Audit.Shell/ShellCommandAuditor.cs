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
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = BuildExecArgv(),
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = environment,
        }, ct);

        var combinedOutput = CombinedOutput(result);

        if (result.ExitCode == 0 && !result.ExecutionUnavailable)
            return new AuditResult(true, [], RawOutput: combinedOutput);

        var finding = BuildCommandFinding(result, toolName);
        if (_opts.ResultClassifier is not null)
        {
            var classified = _opts.ResultClassifier.ClassifyFailedCommand(new AuditResultClassificationContext(
                Name,
                _opts.Argv,
                result,
                combinedOutput,
                finding));
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
    /// The argv actually dispatched to the sandbox. Without
    /// <see cref="ShellCommandAuditorOptions.SelfHealNuGetHome"/> this is the
    /// configured argv verbatim. When it is set (a dotnet-specific opt-in), the
    /// argv is wrapped by <see cref="NuGetHomeSelfHeal.WrapDotnetInvocation"/> so
    /// restore survives a root-owned <c>~/.nuget</c> -- a single <c>sh -c</c> that
    /// runs the self-heal preamble then <c>exec "$@"</c>s the real command with
    /// its arguments intact. The configured argv (not the wrapped form) is what
    /// findings and the result classifier report, so wrapping is invisible to
    /// callers.
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

    private AuditFinding BuildCommandFinding(SandboxExecResult result, string toolName)
    {
        var description = DescriptionOutput(result);

        // Exit 127 is only non-blocking when it is confirmed to be the
        // auditor's tool missing from the sandbox. Some tools, notably npm,
        // propagate exit 127 from repository-controlled scripts; those remain
        // blocking command failures.
        var missingTool = IsConfirmedMissingTopLevelTool(result);
        var severity = missingTool
            ? MissingToolSeverity()
            : AuditSeverity.Error;
        var title = missingTool
            ? $"tool not installed in sandbox: {toolName} (auditor skipped — install the tool in MultipassExtraRuncmd)"
            : $"command exited {result.ExitCode}: {string.Join(' ', _opts.Argv)}";

        return new AuditFinding(
            AuditorName: Name,
            Severity: severity,
            Title: title,
            Description: description.TrimEnd());
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

public sealed record ShellCommandAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }

    public string? ToolName { get; init; }
    public bool? TreatExit127AsMissingTool { get; init; }
    public IAuditResultClassifier? ResultClassifier { get; init; }
    public AuditCapabilities Required { get; init; } = AuditCapabilities.None;
    public AuditSeverity? MissingToolSeverity { get; init; }
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
}
