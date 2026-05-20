using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

public sealed class CodeyBoxOptionsValidator : IValidateOptions<CodeyBoxOptions>
{
    public ValidateOptionsResult Validate(string? name, CodeyBoxOptions options)
    {
        if (double.IsNaN(options.PhaseAbsoluteTimeoutMultiplier)
            || double.IsInfinity(options.PhaseAbsoluteTimeoutMultiplier)
            || options.PhaseAbsoluteTimeoutMultiplier < 1.0)
        {
            return ValidateOptionsResult.Fail(
                "CodeyBox:PhaseAbsoluteTimeoutMultiplier must be finite and >= 1");
        }

        return ValidateOptionsResult.Success;
    }
}
