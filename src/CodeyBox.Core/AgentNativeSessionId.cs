using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// Validated native conversation/session identifier emitted by an agent CLI.
/// The value is safe to carry through durable checkpoint JSON and as one argv
/// element; concrete runners may impose a narrower provider-specific format at
/// their process-launch sink.
/// </summary>
public sealed record AgentNativeSessionId
{
    /// <summary>
    /// Maximum accepted identifier length. Native Claude and Codex identifiers
    /// are substantially shorter; the cap bounds persisted and process-launch
    /// data even if a future CLI emits opaque identifiers.
    /// </summary>
    public const int MaximumLength = 200;

    /// <summary>
    /// Creates a validated native session identifier. JSON deserialization uses
    /// this constructor so persisted data cannot bypass validation.
    /// </summary>
    [JsonConstructor]
    public AgentNativeSessionId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Native session ids must be non-empty, at most {MaximumLength} characters, must not start with '-', and must not contain whitespace or control characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    /// <summary>Returns a validated identifier, or null when the input is invalid.</summary>
    public static AgentNativeSessionId? TryCreate(string? value)
    {
        if (value is null || !IsValid(value))
            return null;

        return new AgentNativeSessionId(value);
    }

    private static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumLength
            || value[0] == '-')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                return false;
        }

        return true;
    }

    // Session ids are durable routing metadata, not useful log content. Keep
    // accidental structured/interpolated logging from writing the raw value.
    public override string ToString() => "[native-session-id]";
}
