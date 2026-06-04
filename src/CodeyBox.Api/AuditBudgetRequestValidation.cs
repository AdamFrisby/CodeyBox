using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class AuditBudgetRequestValidation
{
    public static (string? Normalised, string? Error) NormaliseAuditComplexity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);

        var trimmed = value.Trim();
        if (trimmed.Length > 64)
            return (null, "auditComplexity must be <= 64 chars");

        try { Validation.ValidateNoOptionLikeOrControl(trimmed, "auditComplexity"); }
        catch (ArgumentException ex) { return (null, ex.Message); }

        return (trimmed, null);
    }

    public static string? ValidateAuditMaxIterations(int value)
    {
        if (value <= 0)
            return "auditMaxIterations must be greater than 0";
        if (value > ProjectAudit.MaxIterationBudget)
            return $"auditMaxIterations must be <= {ProjectAudit.MaxIterationBudget}";
        return null;
    }
}
