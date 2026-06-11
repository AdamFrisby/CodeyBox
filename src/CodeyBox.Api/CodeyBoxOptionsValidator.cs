using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

public sealed class CodeyBoxOptionsValidator : IValidateOptions<CodeyBoxOptions>
{
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
                if (tolerance.RequestMaxRetries is < 0 or > 100)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:RequestMaxRetries must be between 0 and 100");
                }
                if (tolerance.StreamMaxRetries is < 0 or > 100)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:StreamMaxRetries must be between 0 and 100");
                }
                if (tolerance.StreamIdleTimeoutMs is < 0)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:StreamIdleTimeoutMs must be non-negative");
                }
                if (tolerance.Provider is not null && string.IsNullOrWhiteSpace(tolerance.Provider))
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:Provider must not be empty");
                }
            }
            else if (string.Equals(agent, AgentNetworkToleranceOptions.ClaudeAgentKind, StringComparison.OrdinalIgnoreCase))
            {
                if (tolerance.ApiTimeoutMs is < 0)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:ApiTimeoutMs must be non-negative");
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

        failures.AddRange(AuditLogStartup.Validate(options.AuditLog));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
