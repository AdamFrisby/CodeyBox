namespace CodeyBox.Api;

internal static class AuditLogStartup
{
    public static IReadOnlyList<string> Validate(AuditLogOptions options)
    {
        var failures = new List<string>();
        if (options.RetainedDays < 1)
            failures.Add("CodeyBox:AuditLog:RetainedDays must be >= 1");
        if (string.IsNullOrWhiteSpace(options.Path))
            failures.Add("CodeyBox:AuditLog:Path must be non-empty");
        if (string.IsNullOrWhiteSpace(options.AuditPath))
            failures.Add("CodeyBox:AuditLog:AuditPath must be non-empty");
        return failures;
    }

    public static void ValidateAndPrepare(AuditLogOptions options)
    {
        var failures = Validate(options);
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        // Ensure log directories exist and are writable before handing control
        // to Serilog, so misconfigured paths surface at startup.
        foreach (var logPath in new[] { options.Path, options.AuditPath })
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
