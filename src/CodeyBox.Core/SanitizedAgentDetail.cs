namespace CodeyBox.Core;

/// <summary>
/// Agent-controlled text that has been redacted and truncated for persistence.
/// The only way to obtain a value is <see cref="FromRaw"/>, which redacts
/// secret-shaped tokens and caps the length, so quota/auth/transient error
/// construction paths cannot accept raw agent text by construction.
/// </summary>
public readonly record struct SanitizedAgentDetail
{
    /// <summary>Maximum UTF-8 bytes retained after redaction.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The redacted, truncated detail text.</summary>
    public string Value { get; }

    private SanitizedAgentDetail(string value) => Value = value;

    /// <summary>
    /// Redacts secret-shaped tokens and truncates to <see cref="MaxBytes"/>
    /// UTF-8 bytes. Null is treated as empty. Idempotent: applying it to an
    /// already-sanitized value returns an equal value.
    /// </summary>
    public static SanitizedAgentDetail FromRaw(string? raw)
        => new(RawOutputRedactor.TruncateToBytes(RawOutputRedactor.Redact(raw ?? string.Empty), MaxBytes));

    public override string ToString() => Value;
}
