namespace CodeyBox.Core;

public static class AgentPauseValidation
{
    public const int MaxReasonLength = 500;

    public static string? ValidateRequiredReason(string? reason, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return $"{fieldName} is required";

        return ValidateOptionalReason(reason, fieldName);
    }

    public static string? ValidateOptionalReason(string? reason, string fieldName)
    {
        if (reason is null)
            return null;

        if (reason.Any(char.IsControl))
            return $"{fieldName} must not contain control characters";

        if (reason.Length > MaxReasonLength)
            return $"{fieldName} must be <= {MaxReasonLength} chars";

        return null;
    }
}
