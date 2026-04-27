using System.Diagnostics;
using CodeyBox.Git;

namespace CodeyBox.Tests;

public sealed class GitCredentialHelperTests
{
    [Fact]
    public void CreateAskPassFor_ProducesScriptThatEmitsToken()
    {
        if (OperatingSystem.IsWindows()) return; // helper is unix-only
        const string token = "ghp_TESTTOKEN_abc123";
        using var scope = GitCredentialHelper.CreateAskPassFor(token);

        Assert.True(scope.Environment.ContainsKey("GIT_ASKPASS"));
        var scriptPath = scope.Environment["GIT_ASKPASS"];
        Assert.True(File.Exists(scriptPath));

        // Run the script with a "Password" prompt; it should print the token.
        var psi = new ProcessStartInfo
        {
            FileName = scriptPath,
            ArgumentList = { "Password for 'https://x':" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in scope.Environment) psi.EnvironmentVariables[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(token, stdout);
    }

    [Fact]
    public void Dispose_RemovesScriptDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        string scriptPath;
        using (var scope = GitCredentialHelper.CreateAskPassFor("x"))
        {
            scriptPath = scope.Environment["GIT_ASKPASS"];
            Assert.True(File.Exists(scriptPath));
        }
        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void EnvironmentDoesNotIncludeRawTokenInGitVarName()
    {
        // Defence-in-depth: env var names should not be the token itself.
        if (OperatingSystem.IsWindows()) return;
        const string token = "secret-token";
        using var scope = GitCredentialHelper.CreateAskPassFor(token);
        foreach (var key in scope.Environment.Keys)
            Assert.DoesNotContain(token, key);
    }
}
