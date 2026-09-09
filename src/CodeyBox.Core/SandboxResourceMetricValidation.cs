using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// Validates numeric sandbox metrics before provider output reaches persistence
/// or telemetry. Guest and CLI output is untrusted, so non-finite and
/// out-of-domain values are represented as unavailable rather than recorded.
/// </summary>
public static class SandboxResourceMetricValidation
{
    public static double? ParseFiniteDouble(
        string? value,
        double minimumInclusive,
        double? maximumInclusive = null)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return null;
        }

        return NormalizeFiniteDouble(parsed, minimumInclusive, maximumInclusive);
    }

    public static double? NormalizeFiniteDouble(
        double? value,
        double minimumInclusive,
        double? maximumInclusive = null)
    {
        if (!double.IsFinite(minimumInclusive))
            throw new ArgumentOutOfRangeException(nameof(minimumInclusive));
        if (maximumInclusive is { } maximum
            && (!double.IsFinite(maximum) || maximum < minimumInclusive))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInclusive));
        }

        if (value is not { } candidate
            || !double.IsFinite(candidate)
            || candidate < minimumInclusive
            || (maximumInclusive is { } upperBound && candidate > upperBound))
        {
            return null;
        }

        return candidate;
    }
}
