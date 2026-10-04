using CodeyBox.Sandbox.ArtifactProvenance;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Options validator preserving <see cref="ArtifactTrustOptions.Validate"/>'s
/// per-rule failure text — registered via <c>IValidateOptions</c> so the
/// operator sees the actual fault, and honoured by <c>ValidateOnStart</c> so
/// a misconfigured trust policy fails the host at startup, not at the first
/// plugin load or baseline bake. Cryptographic verification itself happens
/// per admission through <see cref="ArtifactAdmissionService"/>.
/// </summary>
public sealed class ArtifactTrustOptionsValidator : IValidateOptions<ArtifactTrustOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ArtifactTrustOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            return ValidateOptionsResult.Fail(
                $"Invalid {ArtifactTrustOptions.SectionName} configuration: {string.Join(" ", errors.Take(5))}");
        }
        return ValidateOptionsResult.Success;
    }
}
