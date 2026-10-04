using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Validates <see cref="AuditCheckPublicationOptions"/> at host startup so a
/// misconfigured check-publication policy fails fast with the actual rule that
/// broke, rather than silently publishing with clamped budgets or retrying
/// forever. Registration is inert while <c>Enabled=false</c> (the default).
/// </summary>
public sealed class AuditCheckPublicationOptionsValidator : IValidateOptions<AuditCheckPublicationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditCheckPublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ValidateOptionsResult.Fail(
                $"Invalid {AuditCheckPublicationOptions.SectionName} configuration: {ex.Message}");
        }
    }
}
