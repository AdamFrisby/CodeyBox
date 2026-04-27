namespace CodeyBox.Core;

/// <summary>
/// Stable, human-readable identifier for a project (e.g. "my-app",
/// "internal-tools"). Treated as opaque so config files, CLIs, and the API
/// can use the same string. Validation enforces a conservative shape so the
/// id is safe to use in paths, URLs, and log lines.
/// </summary>
public readonly record struct ProjectId
{
    public string Value { get; }

    public ProjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("ProjectId must not be empty", nameof(value));
        if (value.Length > 64)
            throw new ArgumentException("ProjectId must be <= 64 chars", nameof(value));
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) throw new ArgumentException($"ProjectId may only contain ASCII alnum / '-' / '_': {value}", nameof(value));
        }
        Value = value;
    }

    public override string ToString() => Value;
}
