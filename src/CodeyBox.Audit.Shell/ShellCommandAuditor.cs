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
/// <para>Every invocation receives <c>CODEYBOX_AUDIT_TARGET</c>. Plan-target
/// invocations additionally receive <c>CODEYBOX_PLAN_ARTIFACT_PATH</c>, which
/// names an invocation-specific read-only-by-contract JSON snapshot, and
/// <c>CODEYBOX_WORK_ITEM_ID</c>. The snapshot exists only for the command's
/// duration and is removed before <see cref="RunAsync"/> returns. Code-target
/// invocations do not receive the artifact-path variable.</para>
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

        var extraEnvironment = await PrepareAuditEnvironmentAsync(sandbox, workingDirectory, context, ct);
        if (extraEnvironment.Failure is not null)
            return extraEnvironment.Failure;

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = _opts.Argv,
            WorkingDirectory = workingDirectory,
            ExtraEnvironment = extraEnvironment.Environment,
        }, ct);

        if (extraEnvironment.Environment.TryGetValue("CODEYBOX_PLAN_ARTIFACT_PATH", out var planArtifactPath))
        {
            var cleanup = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["rm", "-f", "--", planArtifactPath],
                WorkingDirectory = workingDirectory,
            }, ct);
            if (!cleanup.Success)
            {
                return new AuditResult(false, [new AuditFinding(
                    Name,
                    AuditSeverity.Error,
                    "failed to clean up plan artifact",
                    DescriptionOutput(cleanup).TrimEnd())],
                    RawOutput: CombinedOutput(cleanup));
            }
        }

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
                return classified;
        }

        return new AuditResult(false, [finding], RawOutput: combinedOutput);
    }

    private async Task<(IReadOnlyDictionary<string, string> Environment, AuditResult? Failure)> PrepareAuditEnvironmentAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEYBOX_AUDIT_TARGET"] = context.EffectiveTarget.Value,
            ["CODEYBOX_WORK_ITEM_ID"] = context.WorkItemId.ToString(),
        };

        if (context.EffectiveTarget != AuditTarget.Plan)
            return (environment, null);

        if (string.IsNullOrWhiteSpace(context.PlanArtifact))
        {
            return (environment, new AuditResult(false, [new AuditFinding(
                Name,
                AuditSeverity.Error,
                "no plan artifact to review",
                "The plan-review context carried no PLAN artifact.")]));
        }

        var planArtifactPath = $"/tmp/codeybox-plan-artifact-{context.WorkItemId}-{context.Iteration}.json";
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "umask 077; rm -f -- \"$1\"; cat > \"$1\"; chmod 400 \"$1\"", "sh", planArtifactPath],
            WorkingDirectory = workingDirectory,
            Stdin = context.PlanArtifact,
        }, ct);

        if (!write.Success)
        {
            return (environment, new AuditResult(false, [new AuditFinding(
                Name,
                AuditSeverity.Error,
                "failed to materialise plan artifact",
                DescriptionOutput(write).TrimEnd())],
                RawOutput: CombinedOutput(write)));
        }

        environment["CODEYBOX_PLAN_ARTIFACT_PATH"] = planArtifactPath;
        return (environment, null);
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
    /// <c>CODEYBOX_PLAN_ARTIFACT_PATH</c>; the path is unique per work item and
    /// review iteration, is removed after the command, and is absent for Code
    /// runs.
    /// </summary>
    public IReadOnlySet<AuditTarget> Targets { get; init; } = AuditTargets.CodeOnly;
    public bool CanShortCircuitOnBlockingFinding { get; init; }
    public AuditorRole Role { get; init; } = AuditorRole.None;
    public BuildTestGateEvidence BuildTestGateEvidence { get; init; } = BuildTestGateEvidence.None;
}
