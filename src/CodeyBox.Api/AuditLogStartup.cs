namespace CodeyBox.Api;

internal static class AuditLogStartup
{
    private const long MinConsoleLogMaxFileSize = 1L * 1024 * 1024;

    public static IReadOnlyList<string> Validate(AuditLogOptions options)
    {
        var failures = new List<string>();
        if (options.RetainedDays < 1)
            failures.Add("CodeyBox:AuditLog:RetainedDays must be >= 1");
        if (string.IsNullOrWhiteSpace(options.Path))
            failures.Add("CodeyBox:AuditLog:Path must be non-empty");
        if (string.IsNullOrWhiteSpace(options.AuditPath))
            failures.Add("CodeyBox:AuditLog:AuditPath must be non-empty");

        var consoleLog = options.ConsoleLog;
        if (consoleLog is null)
        {
            failures.Add("CodeyBox:AuditLog:ConsoleLog must not be null");
        }
        else if (consoleLog.Enabled)
        {
            if (string.IsNullOrWhiteSpace(consoleLog.Path))
                failures.Add("CodeyBox:AuditLog:ConsoleLog:Path must be non-empty");
            if (consoleLog.RetainedFileCountLimit < 1)
                failures.Add("CodeyBox:AuditLog:ConsoleLog:RetainedFileCountLimit must be >= 1");
            if (consoleLog.MaxFileSizeBytes < MinConsoleLogMaxFileSize)
                failures.Add(
                    "CodeyBox:AuditLog:ConsoleLog:MaxFileSizeBytes must be >= 1048576 (1 MiB)");
        }

        return failures;
    }

    public static void ValidateAndPrepare(AuditLogOptions options)
    {
        var failures = Validate(options);
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        // Ensure log directories exist and are writable before handing control
        // to Serilog, so misconfigured paths surface at startup.
        var pathsToPrepare = new List<string> { options.Path, options.AuditPath };
        if (options.ConsoleLog.Enabled)
            pathsToPrepare.Add(options.ConsoleLog.Path);
        foreach (var logPath in pathsToPrepare)
            PrepareDirectory(logPath);
    }

    private static void PrepareDirectory(string logPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Audit log directory '{dir}' (from path '{logPath}') is not writable: {ex.Message}", ex);
        }
    }
}
