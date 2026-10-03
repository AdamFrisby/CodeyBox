using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Options validator preserving <see cref="ColocatedExecutorOptions.Validate"/>'s
/// per-rule failure text — registered via <c>IValidateOptions</c> so the
/// operator sees the actual fault, and honoured by <c>ValidateOnStart</c> so
/// a bad staging root or capacity fails the host at startup, not at the
/// first local dispatch.
/// </summary>
public sealed class ColocatedExecutorOptionsValidator : IValidateOptions<ColocatedExecutorOptions>
{
    public ValidateOptionsResult Validate(string? name, ColocatedExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
