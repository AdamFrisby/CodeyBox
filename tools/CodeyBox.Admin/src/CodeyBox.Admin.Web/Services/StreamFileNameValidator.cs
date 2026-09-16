namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Guards the agent-stream download path. Mirrors the orchestrator store's
/// file guard (<c>IsSafeFileName</c>) locally — the admin holds no project
/// reference to the orchestrator, so the rule is restated here rather than
/// reused. Rejects anything that is not a plain <c>.jsonl</c> file name:
/// no directories, no parent traversal, bounded length. The orchestrator
/// re-validates server-side; this keeps malformed input from ever leaving
/// the admin process.
/// </summary>
public static class StreamFileNameValidator
{
    public const int MaxLength = 256;

    public static bool IsValid(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > MaxLength)
            return false;
        if (!fileName.EndsWith(".jsonl", StringComparison.Ordinal))
            return false;
        if (fileName.Contains("..", StringComparison.Ordinal))
            return false;
        if (fileName.Any(c => c is '/' or '\\' or '\0'))
            return false;
        if (fileName.StartsWith('.')) return false;
        return fileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}
