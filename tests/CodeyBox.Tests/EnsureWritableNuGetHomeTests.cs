using System.Diagnostics;
using System.Runtime.Versioning;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the repo-root NuGet-home self-heal used by
/// <c>Directory.Build.targets</c> / <c>scripts/ensure-writable-nuget-home.sh</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class EnsureWritableNuGetHomeTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-nuget-home-").FullName;

    public void Dispose()
    {
        try
        {
            // Repair may leave mode-555 directories behind; restore write bits first.
            foreach (var path in Directory.EnumerateDirectories(_workspace, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetUnixFileMode(
                        path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch { /* best-effort cleanup */ }
            }
            Directory.Delete(_workspace, recursive: true);
        }
        catch { /* ignore */ }
    }

    private static string ScriptPath
    {
        get
        {
            var repoRoot = FindRepoRoot();
            return Path.Combine(repoRoot, "scripts", "ensure-writable-nuget-home.sh");
        }
    }

    [Fact]
    public async Task Script_RelocatesNonWritableNuGetHome_AndPreservesPackagesSymlink()
    {
        var home = Path.Combine(_workspace, "home");
        var nugetHome = Path.Combine(home, ".nuget");
        var packages = Path.Combine(nugetHome, "packages", "newtonsoft.json");
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, "marker.txt"), "cached");
        // Simulate the baked image: parent not writable by the build user.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var (exit, output) = await RunScriptAsync(home);
        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(Path.Combine(home, ".nuget", "NuGet")), output);
        Assert.True(Directory.Exists(Path.Combine(home, ".nuget")), output);
        Assert.True(
            new DirectoryInfo(Path.Combine(home, ".nuget")).UnixFileMode.HasFlag(UnixFileMode.UserWrite),
            "recreated .nuget must be writable");
        var link = Path.Combine(home, ".nuget", "packages");
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "packages must be a symlink");
        Assert.Equal("cached", File.ReadAllText(Path.Combine(link, "newtonsoft.json", "marker.txt")));
    }

    [Fact]
    public async Task Script_IsIdempotent_WhenNuGetHomeAlreadyWritable()
    {
        var home = Path.Combine(_workspace, "home-ok");
        Directory.CreateDirectory(Path.Combine(home, ".nuget", "packages"));

        Assert.Equal(0, (await RunScriptAsync(home)).ExitCode);
        Assert.Equal(0, (await RunScriptAsync(home)).ExitCode);
        Assert.True(Directory.Exists(Path.Combine(home, ".nuget", "NuGet")));
        Assert.False(Directory.Exists(Path.Combine(home, ".nuget.rootbaked")));
    }

    [Fact]
    public async Task DirectoryBuildTargets_LetsRawDotnetRestore_WhenNuGetHomeIsNotWritable()
    {
        // End-to-end: the same fault the auditors hit (root-owned $HOME/.nuget)
        // must not fail `dotnet restore` once Directory.Build.targets runs the
        // repair script via InitialTargets.
        var home = Path.Combine(_workspace, "e2e-home");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(Path.Combine(nugetHome, "packages"));
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var projDir = Path.Combine(_workspace, "proj");
        Directory.CreateDirectory(projDir);
        await File.WriteAllTextAsync(Path.Combine(projDir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        // Point MSBuild at this repo's Directory.Build.* so InitialTargets fire.
        var repoRoot = FindRepoRoot();
        await File.WriteAllTextAsync(
            Path.Combine(projDir, "Directory.Build.props"),
            $"<Project><Import Project=\"{repoRoot}/Directory.Build.props\" /></Project>\n");
        await File.WriteAllTextAsync(
            Path.Combine(projDir, "Directory.Build.targets"),
            $"<Project><Import Project=\"{repoRoot}/Directory.Build.targets\" /></Project>\n");

        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "restore", "-v", "q" },
            WorkingDirectory = projDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.Environment["HOME"] = home;
        psi.Environment.Remove("DOTNET_CLI_HOME");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // Keep restore offline against the (empty) packages folder — we only
        // care that NuGet can create its settings directory. A project with no
        // PackageReference restores with zero downloads.
        psi.Environment["NUGET_PACKAGES"] = Path.Combine(home, ".nuget", "packages");

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = stdout + stderr;

        Assert.Equal(0, proc.ExitCode);
        Assert.DoesNotContain("Failed to read NuGet.Config", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(home, ".nuget", "NuGet")), output);
        Assert.True(
            new DirectoryInfo(Path.Combine(home, ".nuget")).UnixFileMode.HasFlag(UnixFileMode.UserWrite),
            "Directory.Build.targets must leave a writable NuGet home");
    }

    private async Task<(int ExitCode, string Output)> RunScriptAsync(string home)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { ScriptPath },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.Environment["HOME"] = home;
        psi.Environment["TMPDIR"] = _workspace;

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout + stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeyBox.slnx"))
                && File.Exists(Path.Combine(dir.FullName, "Directory.Build.targets")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
