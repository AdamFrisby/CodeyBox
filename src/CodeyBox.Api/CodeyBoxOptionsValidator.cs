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

        failures.AddRange(AuditLogStartup.Validate(options.AuditLog));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
