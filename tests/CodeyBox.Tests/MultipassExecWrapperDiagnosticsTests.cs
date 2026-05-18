using System.Diagnostics;
using System.Text;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

/// <summary>
/// Round-trip tests for <see cref="MultipassSandboxProvider.BuildEnvironmentFileContent"/>
/// (each value survives a /bin/sh dot-source) plus a smoke test for the
/// exec wrapper's exit-126 diagnostic: a malformed env file must surface
/// the underlying shell error to wrapper stderr, not vanish into a bare
/// "exit 126".
/// </summary>
public sealed class MultipassExecWrapperDiagnosticsTests
{
    [Fact]
    public void BuildEnvironmentFileContent_RejectsNulInValue()
    {
        var env = new Dictionary<string, string> { ["X"] = "ok\0bad" };
        var ex = Assert.Throws<ArgumentException>(
            () => MultipassSandboxProvider.BuildEnvironmentFileContent(env));
        Assert.Contains("NUL", ex.Message);
        Assert.Contains("X", ex.Message);
    }

    [Fact]
    public void BuildEnvironmentFileContent_RejectsNulInKey()
    {
        var env = new Dictionary<string, string> { ["B\0AD"] = "value" };
        Assert.Throws<ArgumentException>(
            () => MultipassSandboxProvider.BuildEnvironmentFileContent(env));
    }

    [Theory]
    [InlineData("EMBED_NEWLINE", "line1\nline2\nline3")]
    [InlineData("EMBED_SQUOTE", "it's a 'quote' party")]
    [InlineData("EMBED_BACKSLASH", "a\\b\\c\\")]
    [InlineData("EMBED_BACKTICK", "value with `command` chars")]
    [InlineData("EMBED_DOLLAR", "$HOME $(date) ${PATH}")]
    [InlineData("EMBED_MIXED", "mix '\"$`\\\n end")]
    public async Task BuildEnvironmentFileContent_RoundTripsThroughShellDotSource(string key, string value)
    {
        if (OperatingSystem.IsWindows()) return;

        var content = MultipassSandboxProvider.BuildEnvironmentFileContent(
            new Dictionary<string, string> { [key] = value });

        var path = Path.Combine(Path.GetTempPath(), $"codeybox-env-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, content);
        try
        {
            var (exit, stdout, stderr) = await RunShellAsync(
                $". \"$1\"; printf %s \"${key}\"", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal(value, stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecWrapper_FailedEnvFileSource_EmitsUnderlyingShellError()
    {
        if (OperatingSystem.IsWindows()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var badEnvPath = Path.Combine(Path.GetTempPath(), $"codeybox-bad-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        // Simulate the production failure mode: dot-sourcing returns
        // non-zero with diagnostic output on stderr. (We avoid syntax
        // errors here because dash treats those as fatal to the parent
        // shell, which short-circuits the wrapper before exit 126.)
        const string sentinel = "synthetic-underlying-error-detail";
        await File.WriteAllTextAsync(badEnvPath, $"echo '{sentinel}' >&2\nfalse\n");
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, _, stderr) = await RunProcessAsync(
                "/bin/sh", [wrapperPath, workDir, "--env-file", badEnvPath, "true"]);

            Assert.Equal(126, exit);
            Assert.Contains("failed to source env file", stderr, StringComparison.Ordinal);
            Assert.Contains(badEnvPath, stderr, StringComparison.Ordinal);
            Assert.Contains(sentinel, stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(wrapperPath);
            File.Delete(badEnvPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecWrapper_FailedCd_EmitsUnderlyingShellError()
    {
        if (OperatingSystem.IsWindows()) return;

        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var missingDir = Path.Combine(Path.GetTempPath(), $"codeybox-nope-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, _, stderr) = await RunProcessAsync(
                "/bin/sh", [wrapperPath, missingDir, "true"]);

            Assert.Equal(127, exit);
            Assert.Contains("failed to cd to", stderr, StringComparison.Ordinal);
            Assert.Contains(missingDir, stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(wrapperPath);
        }
    }

    [Fact]
    public async Task ExecWrapper_ValidEnvFile_AppliesValuesAndExecsCommand()
    {
        if (OperatingSystem.IsWindows()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-work-{Guid.NewGuid():N}");
        var wrapperPath = Path.Combine(Path.GetTempPath(), $"codeybox-wrap-{Guid.NewGuid():N}.sh");
        var envPath = Path.Combine(Path.GetTempPath(), $"codeybox-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var envContent = MultipassSandboxProvider.BuildEnvironmentFileContent(
            new Dictionary<string, string> { ["CODEYBOX_TEST_VALUE"] = "hello\nworld" });
        await File.WriteAllTextAsync(envPath, envContent);
        await File.WriteAllTextAsync(wrapperPath, MultipassSandboxProvider.ExecWrapperScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(wrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var (exit, stdout, stderr) = await RunProcessAsync(
                "/bin/sh",
                [wrapperPath, workDir, "--env-file", envPath, "sh", "-c", "printf %s \"$CODEYBOX_TEST_VALUE\""]);

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal("hello\nworld", stdout);
        }
        finally
        {
            File.Delete(wrapperPath);
            File.Delete(envPath);
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunShellAsync(string script, string arg)
        => RunProcessAsync("/bin/sh", ["-c", script, "codeybox-test", arg]);

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadAllAsync(process.StandardOutput, stdout);
        var stderrTask = ReadAllAsync(process.StandardError, stderr);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadAllAsync(System.IO.StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory())) > 0)
            sink.Append(buffer, 0, n);
    }
}
