using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Options validator preserving <see cref="MajordomoSandboxOptions.Validate"/>'s
/// per-rule failure text — registered via <c>IValidateOptions</c> so the
/// operator sees the actual fault, and honoured by <c>ValidateOnStart</c> so a
/// bad profile/timeout/backend fails the host at startup, not at the first
/// majordomo turn.
/// </summary>
public sealed class MajordomoSandboxOptionsValidator : IValidateOptions<MajordomoSandboxOptions>
{
    public ValidateOptionsResult Validate(string? name, MajordomoSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failure = MajordomoSandboxOptions.Validate(options);
        return failure is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failure);
    }
}
