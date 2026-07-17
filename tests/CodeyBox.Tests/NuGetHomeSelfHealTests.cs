using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class NuGetHomeSelfHealTests
{
    [Fact]
    public void WrapDotnetInvocation_WrapsDotnetCommand_PreservingArgs()
    {
        IReadOnlyList<string> argv = ["dotnet", "build", "--no-incremental", "/warnaserror"];

        var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(argv);

        // sh -c <script> sh dotnet build --no-incremental /warnaserror
        Assert.Equal("sh", wrapped[0]);
        Assert.Equal("-c", wrapped[1]);
        Assert.Contains("exec \"$@\"", wrapped[2]);
        Assert.Equal("sh", wrapped[3]);
        Assert.Equal(argv, wrapped.Skip(4).ToArray());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("npm")]
    [InlineData("git")]
    public void WrapDotnetInvocation_LeavesNonDotnetCommandUnchanged(string tool)
    {
        IReadOnlyList<string> argv = [tool, "build"];

        var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(argv);

        Assert.Same(argv, wrapped);
    }

    [Fact]
    public void WrapDotnetInvocation_LeavesEmptyArgvUnchanged()
    {
        IReadOnlyList<string> argv = [];

        Assert.Same(argv, NuGetHomeSelfHeal.WrapDotnetInvocation(argv));
    }

    // Executes the real wrapped argv through /bin/sh against a broken (unreadable)
    // ~/.nuget with a fake `dotnet` on PATH that echoes DOTNET_CLI_HOME and its own
    // args. Proves the wrap actually redirects the NuGet home AND execs dotnet with
    // the original arguments intact.
    [Fact]
    public async Task WrapDotnetInvocation_HealsBrokenHomeAndForwardsArgs()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-selfheal-").FullName;
        try
        {
            var home = Path.Combine(root, "home");
            var settingsDir = Path.Combine(home, ".nuget", "NuGet");
            Directory.CreateDirectory(settingsDir);
            var config = Path.Combine(settingsDir, "NuGet.Config");
            File.WriteAllText(config, "<configuration/>");
            // The exact failure mode: a user-config the current user cannot read.
            File.SetUnixFileMode(config, UnixFileMode.None);

            var tmp = Path.Combine(root, "tmp");
            Directory.CreateDirectory(tmp);

            // Fake dotnet: prints the (possibly redirected) DOTNET_CLI_HOME, then a
            // marker line with every arg it received, so we can assert both.
            var binDir = Path.Combine(root, "bin");
            Directory.CreateDirectory(binDir);
            var fakeDotnet = Path.Combine(binDir, "dotnet");
            File.WriteAllText(
                fakeDotnet,
                "#!/bin/sh\nprintf 'HOME=%s\\n' \"${DOTNET_CLI_HOME:-}\"\nprintf 'ARGS=%s\\n' \"$*\"\n");
            File.SetUnixFileMode(
                fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var wrapped = NuGetHomeSelfHeal.WrapDotnetInvocation(
                ["dotnet", "build", "--no-incremental", "/warnaserror"]);

            var psi = new ProcessStartInfo(wrapped[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in wrapped.Skip(1))
                psi.ArgumentList.Add(arg);
            psi.Environment["HOME"] = home;
            psi.Environment["TMPDIR"] = tmp;
            psi.Environment["PATH"] = binDir + ":" + Environment.GetEnvironmentVariable("PATH");
            psi.Environment.Remove("DOTNET_CLI_HOME");
            psi.Environment.Remove("NUGET_PACKAGES");

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            // Home was redirected to the writable fallback (not the broken one).
            Assert.Contains(
                "HOME=" + Path.Combine(tmp, NuGetHomeSelfHeal.WritableHomeLeaf),
                stdout);
            // Original arguments reached dotnet unchanged.
            Assert.Contains("ARGS=build --no-incremental /warnaserror", stdout);
        }
        finally
        {
            // Restore readability so the temp tree can be deleted.
            var config = Path.Combine(root, "home", ".nuget", "NuGet", "NuGet.Config");
            if (File.Exists(config))
                File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
