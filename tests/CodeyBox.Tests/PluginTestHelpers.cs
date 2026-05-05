namespace CodeyBox.Tests;

/// <summary>
/// Shared helpers for plugin tests that load the sample plugin assembly.
/// </summary>
internal static class PluginTestHelpers
{
    /// <summary>
    /// Returns the path to the sample plugin assembly
    /// (<c>CodeyBox.PluginSdk.SampleTests.dll</c>). Walks up from the test
    /// binary to find the solution root, then locates the sample project's
    /// build output using the same configuration as the running test binary.
    /// </summary>
    public static string GetSamplePluginAssemblyPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionRoot = FindAncestorContaining(baseDir, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                $"Cannot locate solution root from '{baseDir}'. " +
                "Ensure CodeyBox.slnx exists in an ancestor directory.");

        var config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var samplePath = Path.Combine(
            solutionRoot,
            "tests",
            "CodeyBox.PluginSdk.SampleTests",
            "bin", config, "net10.0",
            "CodeyBox.PluginSdk.SampleTests.dll");

        if (!File.Exists(samplePath))
            throw new FileNotFoundException(
                $"Sample plugin assembly not found at '{samplePath}'. " +
                "Build the solution (including CodeyBox.PluginSdk.SampleTests) before running plugin tests.",
                samplePath);

        return samplePath;
    }

    private static string? FindAncestorContaining(string start, string fileName)
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, fileName)))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
