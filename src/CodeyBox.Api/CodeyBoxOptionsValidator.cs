using CodeyBox.Core;
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

        if (!Enum.IsDefined(options.Shutdown.SandboxResumeMode))
        {
            failures.Add("CodeyBox:Shutdown:SandboxResumeMode must be Background or Blocking");
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

        failures.AddRange(AuditLogStartup.Validate(options.AuditLog));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
