namespace CodeyBox.Api;

/// <summary>
/// Maps an operator-supplied attachment filename to a safe display name.
/// The on-disk filename used by <see cref="CodeyBox.Orchestrator.HostWorkItemAttachmentBlobStore"/>
/// is the blob's SHA-256 — never the operator-supplied value — so the
/// sanitiser's job is purely to keep the metadata column / Content-Disposition
/// header from carrying a path-traversal payload.
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>
    /// Returns a sanitised filename, or null if the input is unrecoverable.
    /// Strips directory components, control characters, and characters that
    /// have special meaning on the major filesystems / shells. Returns null
    /// when the result would be empty or a reserved name (<c>.</c> /
    /// <c>..</c>).
    /// </summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Strip directory components from full local paths. Normalize
        // backslashes first because Path.GetFileName only treats the current
        // platform's separator as path syntax.
        var name = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name is "." or "..") return null;

        // Drop control characters and characters that produce parser ambiguity
        // in HTTP headers / shells. The replacement '_' keeps the visible
        // glyph approximately stable so the operator can recognise the file.
        var buf = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7f) { buf.Append('_'); continue; }
            switch (c)
            {
                case '/':
                case '\\':
                case ':':
                case '*':
                case '?':
                case '"':
                case '<':
                case '>':
                case '|':
                case '\0':
                    buf.Append('_');
                    break;
                default:
                    buf.Append(c);
                    break;
            }
        }
        var sanitized = buf.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized)) return null;
        if (sanitized is "." or "..") return null;
        // Defend against argv-flag confusion in downstream tooling (tar/grep/
        // git add/Claude's file-read): a name whose first character is '-' can
        // be parsed as a flag rather than a positional. Prefix with '_' so the
        // visible glyph stays stable while the leading dash is neutralised.
        if (sanitized[0] == '-')
            sanitized = "_" + sanitized;
        return sanitized;
    }
}
