using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

public sealed class CodeyBoxOptionsValidator : IValidateOptions<CodeyBoxOptions>
{
    private readonly E2eRemotePoolConfigValidation _e2eRemotePoolConfigValidation;

    public CodeyBoxOptionsValidator()
        : this(E2eRemotePoolConfigValidation.Default)
    {
    }

    internal CodeyBoxOptionsValidator(E2eRemotePoolConfigValidation e2eRemotePoolConfigValidation)
    {
        _e2eRemotePoolConfigValidation = e2eRemotePoolConfigValidation;
    }

    public ValidateOptionsResult Validate(string? name, CodeyBoxOptions options)
    {
        var failures = new List<string>();

        if (double.IsNaN(options.PhaseAbsoluteTimeoutMultiplier)
            || double.IsInfinity(options.PhaseAbsoluteTimeoutMultiplier)
            || options.PhaseAbsoluteTimeoutMultiplier < 1.0)
        {
            failures.Add("CodeyBox:PhaseAbsoluteTimeoutMultiplier must be finite and >= 1");
        }

        if (options.MaxTemplateChecks is < 1 or > CodeyBoxOptions.MaximumMaxTemplateChecks)
        {
            failures.Add(
                $"CodeyBox:MaxTemplateChecks must be between 1 and {CodeyBoxOptions.MaximumMaxTemplateChecks}");
        }

        if (options.MaxBulkItems is < 1 or > CodeyBoxOptions.MaximumMaxBulkItems)
        {
            failures.Add(
                $"CodeyBox:MaxBulkItems must be between 1 and {CodeyBoxOptions.MaximumMaxBulkItems}");
        }

        var e2e = options.E2eExecution;
        if (e2e is not null)
        {
            if (e2e.MaxConcurrent is < E2eExecutionOptions.MinimumMaxConcurrent
                or > E2eExecutionOptions.MaximumMaxConcurrent)
            {
                failures.Add(
                    $"CodeyBox:E2eExecution:MaxConcurrent must be between {E2eExecutionOptions.MinimumMaxConcurrent} and {E2eExecutionOptions.MaximumMaxConcurrent}");
            }
            if (e2e.PollInterval < TimeSpan.Zero)
            {
                failures.Add("CodeyBox:E2eExecution:PollInterval must be non-negative");
            }
            if (e2e.PerRunTimeout <= TimeSpan.Zero)
            {
                failures.Add("CodeyBox:E2eExecution:PerRunTimeout must be positive");
            }
            if (!string.Equals(e2e.PoolKind, "local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e2e.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("CodeyBox:E2eExecution:PoolKind must be 'local' or 'remote-ssh'");
            }
            var remoteE2e = string.Equals(e2e.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase);
            if (e2e.Enabled && remoteE2e)
            {
                failures.AddRange(_e2eRemotePoolConfigValidation.ValidateEnabledRemoteE2eConfig(e2e, options));
            }
            else if (remoteE2e)
            {
                failures.AddRange(_e2eRemotePoolConfigValidation.ValidateConfiguredRemoteLifecycleIsolation(options));
            }
            foreach (var (host, index) in GetE2eRemoteHostConfigs(options).Select((host, index) => (host, index)))
            {
                if (host.MaxConcurrent is < E2eExecutionOptions.MinimumMaxConcurrent or > E2eExecutionOptions.MaximumMaxConcurrent)
                {
                    failures.Add(
                        $"CodeyBox:E2eMultipassRemoteSandboxes:{index}:MaxConcurrent must be between {E2eExecutionOptions.MinimumMaxConcurrent} and {E2eExecutionOptions.MaximumMaxConcurrent}");
                }
            }
            if (e2e.AllowedReadinessOrigins is null || e2e.AllowedReadinessOrigins.Count == 0)
            {
                failures.Add("CodeyBox:E2eExecution:AllowedReadinessOrigins must contain at least one origin");
            }
            foreach (var origin in e2e.AllowedReadinessOrigins ?? [])
            {
                if (string.IsNullOrWhiteSpace(origin)
                    || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
                    || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    failures.Add("CodeyBox:E2eExecution:AllowedReadinessOrigins entries must be http(s) origins without path, query, fragment, or userinfo");
                    break;
                }
            }
        }

        foreach (var (agent, tolerance) in options.AgentNetworkTolerance)
        {
            if (string.IsNullOrWhiteSpace(agent))
            {
                failures.Add("CodeyBox:AgentNetworkTolerance keys must not be empty");
                continue;
            }
            if (tolerance is null)
            {
                failures.Add($"CodeyBox:AgentNetworkTolerance:{agent} must not be null");
                continue;
            }

            if (string.Equals(agent, AgentNetworkToleranceOptions.CodexAgentKind, StringComparison.OrdinalIgnoreCase))
            {
                if (tolerance.RequestMaxRetries is < 0 or > AgentNetworkToleranceOptions.CodexMaximumRetries)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:RequestMaxRetries must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumRetries}");
                }
                if (tolerance.StreamMaxRetries is < 0 or > AgentNetworkToleranceOptions.CodexMaximumRetries)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:StreamMaxRetries must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumRetries}");
                }
                if (tolerance.StreamIdleTimeoutMs is < 0 or > AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs)
                {
                    failures.Add(
                        $"CodeyBox:AgentNetworkTolerance:{agent}:StreamIdleTimeoutMs must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs}");
                }
                if (tolerance.Provider is not null)
                {
                    if (string.IsNullOrWhiteSpace(tolerance.Provider))
                    {
                        failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:Provider must not be empty");
                    }
                    else if (!AgentNetworkToleranceOptions.IsValidCodexProviderId(tolerance.Provider))
                    {
                        failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:Provider must match [A-Za-z0-9_-]+");
                    }
                }
            }
            else if (string.Equals(agent, AgentNetworkToleranceOptions.ClaudeAgentKind, StringComparison.OrdinalIgnoreCase))
            {
                if (tolerance.ApiTimeoutMs is < 0 or > AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs)
                {
                    failures.Add(
                        $"CodeyBox:AgentNetworkTolerance:{agent}:ApiTimeoutMs must be between 0 and {AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs}");
                }
            }
        }

        foreach (var (agent, pause) in options.AgentPauses)
        {
            if (string.IsNullOrWhiteSpace(agent))
            {
                failures.Add("CodeyBox:AgentPauses keys must not be empty");
                continue;
            }

            if (pause.Paused
                && AgentPauseValidation.ValidateRequiredReason(pause.Reason, $"CodeyBox:AgentPauses:{agent}:Reason") is { } reasonError)
                failures.Add(reasonError);
            else if (!pause.Paused
                && AgentPauseValidation.ValidateOptionalReason(pause.Reason, $"CodeyBox:AgentPauses:{agent}:Reason") is { } optionalReasonError)
                failures.Add(optionalReasonError);

            if (pause.DurationSeconds is { } seconds && seconds <= 0)
                failures.Add($"CodeyBox:AgentPauses:{agent}:DurationSeconds must be positive");
            if (pause.DurationSeconds is not null && pause.ExpiresAt is not null)
                failures.Add($"CodeyBox:AgentPauses:{agent} must provide either DurationSeconds or ExpiresAt, not both");
        }

        if (!Enum.IsDefined(options.Shutdown.SandboxResumeMode))
        {
            failures.Add("CodeyBox:Shutdown:SandboxResumeMode must be Background or Blocking");
        }

        if (!Enum.IsDefined(options.Shutdown.SandboxTeardownMode))
        {
            failures.Add("CodeyBox:Shutdown:SandboxTeardownMode must be Suspend, Stop, or Dispose");
        }

        if (options.Shutdown.SandboxResumeTimeout <= TimeSpan.Zero
            || options.Shutdown.SandboxResumeTimeout > SandboxStartupResumePolicy.MaximumResumeTimeout)
        {
            failures.Add(
                $"CodeyBox:Shutdown:SandboxResumeTimeout must be a positive TimeSpan <= {SandboxStartupResumePolicy.MaximumResumeTimeout}");
        }

        if (options.Shutdown.SandboxAdoptionDeadlineSeconds <= 0
            || options.Shutdown.SandboxAdoptionDeadlineSeconds > (int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds)
        {
            failures.Add(
                $"CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds must be > 0 and <= {(int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds}");
        }

        try
        {
            options.WorkerPoolHealthWatchdog.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (options.PipelineTuning.MaxSandboxReuses < 1)
        {
            failures.Add("CodeyBox:PipelineTuning:MaxSandboxReuses must be >= 1");
        }
        if (!PlanReviewIterationLimit.TryCreate(options.PipelineTuning.MaxPlanReviewIterations, out _))
        {
            failures.Add(
                $"CodeyBox:PipelineTuning:MaxPlanReviewIterations must be >= {PlanReviewIterationLimit.MinimumValue}");
        }
        if (options.PipelineTuning.MaxSandboxLifetime <= TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:MaxSandboxLifetime must be a positive TimeSpan");
        }
        if (double.IsNaN(options.PipelineTuning.SandboxPressureThreshold)
            || double.IsInfinity(options.PipelineTuning.SandboxPressureThreshold)
            || options.PipelineTuning.SandboxPressureThreshold < 0.0
            || options.PipelineTuning.SandboxPressureThreshold > 1.0)
        {
            failures.Add("CodeyBox:PipelineTuning:SandboxPressureThreshold must be between 0.0 and 1.0 inclusive");
        }
        if (options.PipelineTuning.AuditorIdleTimeout < TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:AuditorIdleTimeout must be non-negative");
        }
        if (options.PipelineTuning.EmptyReworkEscalationRetries < 0)
        {
            failures.Add("CodeyBox:PipelineTuning:EmptyReworkEscalationRetries must be non-negative");
        }

        if (options.PipelineTuning.CSharpTestPassAuditorIdleTimeout is { } cSharpTestIdle && cSharpTestIdle < TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:CSharpTestPassAuditorIdleTimeout must be non-negative when set");
        }

        if (options.PipelineTuning.CSharpTestPassBlameHangTimeout is { } cSharpTestBlameHang && cSharpTestBlameHang <= TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:CSharpTestPassBlameHangTimeout must be positive when set");
        }

        try
        {
            options.AgentSupervision.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        try
        {
            options.Attachments.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (options.EnableSharedUpstreamMirror && string.IsNullOrWhiteSpace(options.SharedUpstreamMirrorDirectory))
        {
            failures.Add("CodeyBox:SharedUpstreamMirrorDirectory must not be empty if EnableSharedUpstreamMirror is true");
        }

        failures.AddRange(AuditLogStartup.Validate(options.AuditLog));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static IReadOnlyList<E2eMultipassRemoteHostConfig> GetE2eRemoteHostConfigs(CodeyBoxOptions options)
    {
        if (options.E2eMultipassRemoteSandboxes is { Count: > 0 } hosts)
            return hosts;
        return options.E2eMultipassRemoteSandbox is null
            ? []
            : [options.E2eMultipassRemoteSandbox];
    }
}
