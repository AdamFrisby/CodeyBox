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
        var packageDir = Path.Combine(nugetHome, "packages", "newtonsoft.json");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "marker.txt"), "cached");
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
        Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
    }

    [Fact]
    public async Task Script_DefersToRedirect_WhenHomeUnwritable()
    {
        // An unwritable $HOME makes every in-place repair impossible — both the
        // fast-path mkdir and the quarantine mv need a writable parent. The
        // script must not turn that into a build failure (MSB3073): the
        // DOTNET_CLI_HOME redirect is the designed fallback, so it exits 0 and
        // leaves the broken tree untouched.
        if (Environment.IsPrivilegedProcess)
            return; // root can write anywhere; the unwritable branch is unobservable

        var home = Path.Combine(_workspace, "home-unwritable");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(nugetHome);
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (exit, output) = await RunScriptAsync(home);

            Assert.Equal(0, exit);
            Assert.Contains("relying on the DOTNET_CLI_HOME redirect", output, StringComparison.Ordinal);
            Assert.Empty(Directory.GetDirectories(home, ".nuget.codeybox-unwritable.*"));
        }
        finally
        {
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Script_DefersToRedirect_WhenNuGetHomeCannotBeCreated()
    {
        // $HOME unwritable with no .nuget at all: the mkdir fast path cannot
        // succeed, so the script defers to the redirect instead of aborting a
        // `set -e` caller.
        if (Environment.IsPrivilegedProcess)
            return;

        var home = Path.Combine(_workspace, "home-no-create");
        Directory.CreateDirectory(home);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (exit, output) = await RunScriptAsync(home);

            Assert.Equal(0, exit);
            Assert.Contains("relying on the DOTNET_CLI_HOME redirect", output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(home, ".nuget")));
        }
        finally
        {
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The CODEYBOX_NUGET_HOME_UNDER_TEST override below exercises the same
    // hook the retired scripts/reclaim-nuget-home.sh carried — the two
    // wrappers converged into this one, so its coverage moved here.
    [Fact]
    public async Task Script_AlreadyUsableHome_IsNoOp()
    {
        var home = Path.Combine(_workspace, "arg-home-ok");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(Path.Combine(nugetHome, "NuGet"));

        var (exit, _) = await RunScriptAsync(home, nugetHome);

        Assert.Equal(0, exit);
        // A usable home is left exactly as-is: no reclaim, no aside copy.
        Assert.True(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
        Assert.Empty(AsideDirs(nugetHome));
    }

    [Fact]
    public async Task Script_UnwritableHome_IsReclaimedNonDestructively()
    {
        if (Environment.IsPrivilegedProcess)
            return;

        var home = Path.Combine(_workspace, "arg-home-unwritable");
        var nugetHome = Path.Combine(home, ".nuget");
        Directory.CreateDirectory(nugetHome);
        var marker = Path.Combine(nugetHome, "packages-marker");
        await File.WriteAllTextAsync(marker, "base-image-cache");
        // Deny writes on the home itself so "mkdir $home/NuGet" fails exactly as a
        // root-owned ~/.nuget denies the unprivileged build user; the parent
        // stays writable so the reclaim can move it aside.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (exit, output) = await RunScriptAsync(home, nugetHome);

            Assert.Equal(0, exit);
            // A fresh, writable home is in place so restore can create its config.
            var nugetDir = Path.Combine(nugetHome, "NuGet");
            Assert.True(Directory.Exists(nugetDir), output);
            var probe = Path.Combine(nugetDir, "probe");
            await File.WriteAllTextAsync(probe, "writable");
            Assert.True(File.Exists(probe));
            // Non-destructive: the original home (with its cache) is preserved aside.
            var aside = Assert.Single(AsideDirs(nugetHome));
            Assert.Equal("base-image-cache", await File.ReadAllTextAsync(Path.Combine(aside, "packages-marker")));
        }
        finally
        {
            if (Directory.Exists(nugetHome))
                File.SetUnixFileMode(
                    nugetHome,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Script_UnreadableConfigInWritableDir_IsReclaimed()
    {
        if (Environment.IsPrivilegedProcess)
            return;

        var home = Path.Combine(_workspace, "arg-home-poisoned");
        var nugetHome = Path.Combine(home, ".nuget");
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Directory.CreateDirectory(nugetDir);
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>");
        // Writable directory but a root-owned, unreadable config inside — NuGet
        // aborts reading it just like the missing-directory case, so the reclaim
        // must relocate it rather than treat the home as usable.
        File.SetUnixFileMode(config, UnixFileMode.None);
        try
        {
            var (exit, _) = await RunScriptAsync(home, nugetHome);

            Assert.Equal(0, exit);
            // The poisoned config was moved aside and a fresh home put in place.
            Assert.Single(AsideDirs(nugetHome));
            var freshConfig = Path.Combine(nugetHome, "NuGet", "NuGet.Config");
            Assert.True(
                File.Exists(freshConfig),
                "the heal seeds a readable user config so the fatal read succeeds");
            Assert.Contains(
                "<configuration",
                await File.ReadAllTextAsync(freshConfig),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var aside in AsideDirs(nugetHome))
            {
                var quarantined = Path.Combine(aside, "NuGet", "NuGet.Config");
                if (File.Exists(quarantined))
                    File.SetUnixFileMode(quarantined, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public async Task Script_ParentNotWritable_IsLeftForRedirect()
    {
        if (Environment.IsPrivilegedProcess)
            return;

        var parent = Path.Combine(_workspace, "arg-readonly-home");
        var nugetHome = Path.Combine(parent, ".nuget");
        Directory.CreateDirectory(nugetHome);
        // Both the home AND its parent are unwritable: no in-place repair is
        // possible, so the script must leave everything untouched, defer to
        // the DOTNET_CLI_HOME redirect, and exit cleanly.
        File.SetUnixFileMode(nugetHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (exit, output) = await RunScriptAsync(parent, nugetHome);

            Assert.Equal(0, exit);
            Assert.Contains("relying on the DOTNET_CLI_HOME redirect", output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
            Assert.Empty(AsideDirs(nugetHome));
        }
        finally
        {
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                nugetHome,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Script_UidOwnedGroupWritableTree_IsLeftInPlaceWithPackagesIntact()
    {
        var home = Path.Combine(_workspace, "arg-home-group-writable");
        var nugetHome = Path.Combine(home, ".nuget");
        var packageDir = Path.Combine(nugetHome, "packages", "pkg");
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(nugetDir);
        var marker = Path.Combine(packageDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "healthy-cache");
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>");

        // Simulate Linux umask 002: group-writable directory (0775) and files (0664)
        // owned by the current UID.
        File.SetUnixFileMode(nugetHome, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(Path.Combine(nugetHome, "packages"), UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(packageDir, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(nugetDir, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(marker, UnixUmask002Permissions.File);
        File.SetUnixFileMode(config, UnixUmask002Permissions.File);

        var (exit, output) = await RunScriptAsync(home, nugetHome);

        Assert.Equal(0, exit);
        Assert.Empty(AsideDirs(nugetHome));
        Assert.True(File.Exists(marker), output);
        Assert.Equal("healthy-cache", await File.ReadAllTextAsync(marker));
        Assert.True(File.Exists(config));
        Assert.False(
            File.GetAttributes(Path.Combine(nugetHome, "packages")).HasFlag(FileAttributes.ReparsePoint),
            "packages directory must not have been replaced by a symlink");
    }

    [Fact]
    public async Task Script_UnusableTree_IsQuarantined()
    {
        // 1. .nuget symlinked at a foreign root-owned system directory (/etc) —
        //    rejected as a link before ownership is even inspected.
        var homeLink = Path.Combine(_workspace, "arg-home-foreign-symlink");
        Directory.CreateDirectory(homeLink);
        var nugetHomeLink = Path.Combine(homeLink, ".nuget");
        File.CreateSymbolicLink(nugetHomeLink, "/etc");

        var (exitLink, outputLink) = await RunScriptAsync(homeLink, nugetHomeLink);
        Assert.Equal(0, exitLink);
        Assert.Single(AsideDirs(nugetHomeLink));
        Assert.Contains("quarantined unusable", outputLink, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(nugetHomeLink, "NuGet")));
        Assert.False(
            File.GetAttributes(nugetHomeLink).HasFlag(FileAttributes.ReparsePoint),
            "recreated .nuget must be a real directory, not a symlink");

        // 2. Unusable tree simulated via mode 0555 (uid-owned but unwritable —
        //    foreign-OWNERSHIP is covered separately by
        //    Script_ForeignOwnedTree_IsQuarantined).
        if (!Environment.IsPrivilegedProcess)
        {
            var homeUnwritable = Path.Combine(_workspace, "arg-home-foreign-unwritable");
            var nugetHomeUnwritable = Path.Combine(homeUnwritable, ".nuget");
            Directory.CreateDirectory(nugetHomeUnwritable);
            File.SetUnixFileMode(
                nugetHomeUnwritable,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            try
            {
                var (exitUnwritable, outputUnwritable) = await RunScriptAsync(homeUnwritable, nugetHomeUnwritable);
                Assert.Equal(0, exitUnwritable);
                Assert.Single(AsideDirs(nugetHomeUnwritable));
                Assert.Contains("quarantined unusable", outputUnwritable, StringComparison.Ordinal);
                Assert.True(Directory.Exists(Path.Combine(nugetHomeUnwritable, "NuGet")));
            }
            finally
            {
                if (Directory.Exists(nugetHomeUnwritable))
                {
                    File.SetUnixFileMode(
                        nugetHomeUnwritable,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
    }

    [Fact]
    public async Task Script_NoQuarantineFlag_SkipsDestructiveRepair()
    {
        // CODEYBOX_NUGET_HEAL_NO_QUARANTINE is the contract the per-project
        // MSBuild InitialTargets rely on: the shared recovery must leave an
        // unusable .nuget untouched (no mv, no recreation) and take only the
        // non-destructive redirect fallback.
        var home = Path.Combine(_workspace, "arg-home-no-quarantine");
        Directory.CreateDirectory(home);
        var nugetHome = Path.Combine(home, ".nuget");
        File.CreateSymbolicLink(nugetHome, "/etc");

        var (exit, output) = await RunScriptAsync(
            home, nugetHome,
            new Dictionary<string, string> { ["CODEYBOX_NUGET_HEAL_NO_QUARANTINE"] = "1" });

        Assert.Equal(0, exit);
        Assert.Empty(AsideDirs(nugetHome));
        Assert.True(
            File.GetAttributes(nugetHome).HasFlag(FileAttributes.ReparsePoint),
            "non-destructive mode must leave the unusable .nuget exactly as found");
        Assert.Contains("relying on the DOTNET_CLI_HOME redirect", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ForeignOwnedTree_IsQuarantined()
    {
        // A genuinely foreign-owned .nuget (uid != ours) is the co-tenant risk
        // the ownership admission exists for: it must be quarantined aside and
        // replaced, never trusted because it happens to be writable.
        //
        // Creating a foreign-owned fixture needs a real chown, which requires
        // privilege. An unprivileged user-namespace fixture cannot substitute:
        // without newuidmap (absent here, and no_new_privs blocks the setuid
        // helpers anyway) a user namespace can only map the caller's own uid,
        // so every file it creates still carries the same kuid the heal sees
        // as its own — never a foreign one.
        if (!Environment.IsPrivilegedProcess)
            return;

        var home = Path.Combine(_workspace, "arg-home-foreign-owned");
        var nugetHome = Path.Combine(home, ".nuget");
        var nugetDir = Path.Combine(nugetHome, "NuGet");
        Directory.CreateDirectory(nugetDir);
        await File.WriteAllTextAsync(Path.Combine(nugetDir, "NuGet.Config"), "<configuration/>");
        var marker = Path.Combine(nugetHome, "packages-marker");
        await File.WriteAllTextAsync(marker, "foreign-cache");

        // nobody (uid/gid 65534) — a uid that is neither ours nor root.
        await ChownAsync("65534:65534", nugetHome);

        var (exit, output) = await RunScriptAsync(home, nugetHome);

        Assert.Equal(0, exit);
        Assert.Single(AsideDirs(nugetHome));
        Assert.Contains("quarantined unusable", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(nugetHome, "NuGet")));
        Assert.False(
            File.GetAttributes(nugetHome).HasFlag(FileAttributes.ReparsePoint),
            "recreated .nuget must be a real directory, not a symlink");
    }

    private static async Task ChownAsync(string owner, string path)
    {
        var psi = new ProcessStartInfo("chown")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(owner);
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"chown {owner} {path} failed: {stderr}");
    }

    [Fact]
    public async Task Script_ConcurrentInvocations_ProduceAtMostOneQuarantine_AndNeverRemoveHealthyCache()
    {
        // Case 1: Unusable tree requiring quarantine. Multiple parallel invocations must coordinate
        // via lock/marker so at most one quarantine directory is produced.
        var homeBroken = Path.Combine(_workspace, "concurrent-broken");
        Directory.CreateDirectory(homeBroken);
        var nugetBroken = Path.Combine(homeBroken, ".nuget");
        File.CreateSymbolicLink(nugetBroken, "/etc");

        var tasksBroken = Enumerable.Range(0, 8)
            .Select(_ => RunScriptAsync(homeBroken, nugetBroken))
            .ToArray();
        var resultsBroken = await Task.WhenAll(tasksBroken);

        foreach (var (exit, _) in resultsBroken)
            Assert.Equal(0, exit);

        Assert.Single(AsideDirs(nugetBroken));
        Assert.True(Directory.Exists(Path.Combine(nugetBroken, "NuGet")));
        Assert.False(File.GetAttributes(nugetBroken).HasFlag(FileAttributes.ReparsePoint));

        // Case 2: Healthy tree with umask 002 (group-writable). Multiple parallel invocations
        // must never quarantine and never remove or replace the healthy cache.
        var homeHealthy = Path.Combine(_workspace, "concurrent-healthy");
        var nugetHealthy = Path.Combine(homeHealthy, ".nuget");
        var packageDir = Path.Combine(nugetHealthy, "packages", "pkg");
        var nugetDir = Path.Combine(nugetHealthy, "NuGet");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(nugetDir);
        var marker = Path.Combine(packageDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "healthy-concurrent-cache");
        var config = Path.Combine(nugetDir, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration/>");

        File.SetUnixFileMode(nugetHealthy, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(Path.Combine(nugetHealthy, "packages"), UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(packageDir, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(nugetDir, UnixUmask002Permissions.Directory);
        File.SetUnixFileMode(marker, UnixUmask002Permissions.File);
        File.SetUnixFileMode(config, UnixUmask002Permissions.File);

        var tasksHealthy = Enumerable.Range(0, 8)
            .Select(_ => RunScriptAsync(homeHealthy, nugetHealthy))
            .ToArray();
        var resultsHealthy = await Task.WhenAll(tasksHealthy);

        foreach (var (exit, _) in resultsHealthy)
            Assert.Equal(0, exit);

        Assert.Empty(AsideDirs(nugetHealthy));
        Assert.True(File.Exists(marker));
        Assert.Equal("healthy-concurrent-cache", await File.ReadAllTextAsync(marker));
        Assert.False(File.GetAttributes(Path.Combine(nugetHealthy, "packages")).HasFlag(FileAttributes.ReparsePoint));
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
        // This is a NON-solution restore, so the per-project hook runs
        // non-destructively (CODEYBOX_NUGET_HEAL_NO_QUARANTINE): it must NOT
        // rename the unwritable .nuget aside — destructive repair is reserved
        // for the once-per-solution hook. The build still succeeds because the
        // in-process NuGetHomeGuard.targets redirect supplies a writable
        // DOTNET_CLI_HOME for NuGet's user-config read.
        Assert.Empty(AsideDirs(nugetHome));
        Assert.False(
            new DirectoryInfo(nugetHome).UnixFileMode.HasFlag(UnixFileMode.UserWrite),
            "per-project hook must leave the unwritable .nuget untouched");
    }

    private static IReadOnlyList<string> AsideDirs(string nugetHome)
    {
        var parent = Path.GetDirectoryName(nugetHome)!;
        var name = Path.GetFileName(nugetHome);
        return Directory.Exists(parent)
            ? Directory.GetDirectories(parent, name + ".codeybox-unwritable.*")
            : [];
    }

    private async Task<(int ExitCode, string Output)> RunScriptAsync(
        string home, string? nugetArg = null, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.Environment["HOME"] = home;
        // The .nuget-override test hook: it repairs the cli home above that
        // directory instead of the ambient $HOME. Cleared first so a value
        // inherited from the test host cannot leak into the fixture.
        psi.Environment.Remove("CODEYBOX_NUGET_HOME_UNDER_TEST");
        if (nugetArg is not null)
            psi.Environment["CODEYBOX_NUGET_HOME_UNDER_TEST"] = nugetArg;
        // Same for the non-destructive-mode flag: cleared so a value inherited
        // from the test host cannot silently change what the fixture exercises.
        psi.Environment.Remove("CODEYBOX_NUGET_HEAL_NO_QUARANTINE");
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }
        psi.Environment["TMPDIR"] = _workspace;
        // The fixture home must be authoritative: a stamped DOTNET_CLI_HOME or
        // runtime dir inherited from the test host would make the heal repair
        // that home instead of the fixture's.
        psi.Environment.Remove("DOTNET_CLI_HOME");
        psi.Environment.Remove("XDG_RUNTIME_DIR");
        psi.Environment.Remove("XDG_DATA_HOME");
        psi.Environment.Remove("NUGET_PACKAGES");

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

/// <summary>
/// Shared umask-002 fixture permissions (directories 0775, files 0664) used to
/// simulate the standard Linux user-private-group layout across the NuGet-home
/// heal tests, so the simulated modes cannot drift between fixtures.
/// </summary>
internal static class UnixUmask002Permissions
{
    internal const string DirectoryMode = "0775";
    internal const string FileMode = "0664";

    internal static readonly UnixFileMode Directory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    internal static readonly UnixFileMode File =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead;
}
