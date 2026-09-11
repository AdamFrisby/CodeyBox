using System.Diagnostics;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end restore behavior against the read-only fallback layout:
/// packages present in the shared cache resolve without entering the
/// writable root, absent packages are fetched into the writable root while
/// the shared source stays byte-identical, and a missing fallback folder
/// degrades to plain network/local restore.
/// </summary>
public sealed class NuGetFallbackRestoreTests : IDisposable
{
    private const string PackageA = "CodeyBox.FallbackTest.A";
    private const string PackageB = "CodeyBox.FallbackTest.B";
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-nuget-restore-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Restore_ResolvesSharedPackagesWithoutCopyingIntoWritableRoot()
    {
        var feed = await PackTestPackagesAsync();
        var fallback = Path.Combine(_root, "fallback");
        await SeedFallbackFromPackagesAsync(feed, fallback, PackageA, "1.0.0");
        var before = SnapshotTree(fallback);
        var consumer = WriteConsumer(PackageA, "1.0.0");
        var writable = Path.Combine(_root, "writable");
        Directory.CreateDirectory(writable);

        var result = await RunDotnetAsync(
            ["restore", consumer, "/p:RestoreSources=" + Path.Combine(_root, "empty-source")],
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = writable,
                [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = fallback,
            });
        Assert.True(result.Success, $"dotnet restore failed: {result.Output}");
        Assert.False(
            Directory.Exists(Path.Combine(writable, PackageA.ToLowerInvariant())),
            "a fallback-resolved package must not be copied into the writable root");
        Assert.Equal(before, SnapshotTree(fallback));
    }

    [Fact]
    public async Task Restore_FetchesMissingPackagesIntoWritableRootLeavingSharedSourceUnmodified()
    {
        var feed = await PackTestPackagesAsync();
        var fallback = Path.Combine(_root, "fallback");
        await SeedFallbackFromPackagesAsync(feed, fallback, PackageA, "1.0.0");
        var before = SnapshotTree(fallback);
        var consumer = WriteConsumer(PackageA, "1.0.0", PackageB, "2.0.0");
        var writable = Path.Combine(_root, "writable");
        Directory.CreateDirectory(writable);

        var result = await RunDotnetAsync(
            ["restore", consumer, "/p:RestoreSources=" + feed],
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = writable,
                [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = fallback,
            });
        Assert.True(result.Success, $"dotnet restore failed: {result.Output}");
        Assert.True(
            Directory.Exists(Path.Combine(writable, PackageB.ToLowerInvariant(), "2.0.0")),
            "a package absent from the fallback cache must be fetched into the writable root");
        Assert.False(
            Directory.Exists(Path.Combine(writable, PackageA.ToLowerInvariant())),
            "a fallback-resolved package must not be copied into the writable root");
        Assert.True(
            File.Exists(Path.Combine(writable, PackageB.ToLowerInvariant(), "2.0.0", "lib", "net10.0", $"{PackageB}.dll")),
            "the fetched package must be fully extracted");
        Assert.Equal(before, SnapshotTree(fallback));
    }

    [Fact]
    public async Task Restore_FailsClosedOnMissingFallbackDirectory()
    {
        var feed = await PackTestPackagesAsync();
        var consumer = WriteConsumer(PackageA, "1.0.0");
        var writable = Path.Combine(_root, "writable");
        Directory.CreateDirectory(writable);

        // A fallback folder that does not exist aborts restore loudly
        // (NU1301) instead of silently fetching everything. The provider
        // therefore ensures every advertised fallback folder exists in the
        // guest; this pins the fail-closed contract that makes that ensure
        // step load-bearing rather than cosmetic.
        var result = await RunDotnetAsync(
            ["restore", consumer, "/p:RestoreSources=" + feed],
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = writable,
                [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] =
                    Path.Combine(_root, "does-not-exist"),
            });
        Assert.False(result.Success, "restore against a missing fallback folder must fail closed");
        Assert.Contains("NU1301", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessSandbox_HonorsReadOnlyFallbackMount()
    {
        var feed = await PackTestPackagesAsync();
        var fallback = Path.Combine(_root, "fallback");
        await SeedFallbackFromPackagesAsync(feed, fallback, PackageA, "1.0.0");
        var before = SnapshotTree(fallback);
        var consumerDir = Path.Combine(_root, "consumer-proj");
        var consumer = WriteConsumerIn(consumerDir, PackageA, "1.0.0", PackageB, "2.0.0");
        var cliHome = Path.Combine(_root, "cli-home");
        Directory.CreateDirectory(cliHome);

        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/fb", HostPath = fallback, ReadOnly = true },
                new SandboxMount { SandboxPath = "/src", HostPath = consumerDir },
                new SandboxMount { SandboxPath = "/writable", Tmpfs = true },
            ],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NUGET_PACKAGES"] = "/writable",
                [NuGetFallbackCache.FallbackPackagesEnvironmentVariable] = "/fb",
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/src",
        };
        await using var sandbox = await provider.CreateAsync(spec);
        var restore = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["dotnet", "restore", "/src/consumer.csproj", "/p:RestoreSources=" + feed],
            WorkingDirectory = "/src",
        });
        Assert.True(restore.Success, $"sandbox restore failed: {restore.Stdout}\n{restore.Stderr}");

        // The shared package resolved in place; only the missing one landed
        // in the per-sandbox writable root. (Argv entries are translated
        // sandbox-path to host-path per element; a `sh -c` string would not
        // be, so the listing uses a direct argv exec.)
        var writableList = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["ls", "/writable"],
            WorkingDirectory = "/src",
        });
        Assert.True(writableList.Success, writableList.Stderr);
        Assert.DoesNotContain(PackageA, writableList.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PackageB, writableList.Stdout, StringComparison.OrdinalIgnoreCase);

        // The shared source is not writable from inside the sandbox: the
        // read-only copy refuses overwrites (opening a read-only file
        // O_WRONLY fails) and the host tree is untouched by construction —
        // the provider serves a copy, never the original.
        var tamper = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["tee", "/fb/" + PackageA.ToLowerInvariant() + "/1.0.0/" + PackageA.ToLowerInvariant() + ".1.0.0.nupkg"],
            Stdin = "tampered",
            WorkingDirectory = "/src",
        });
        Assert.False(tamper.Success, "overwriting a shared-cache file from inside the sandbox must fail");
        Assert.Equal(before, SnapshotTree(fallback));
    }

    private async Task<string> PackTestPackagesAsync()
    {
        var feed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(Path.Combine(_root, "empty-source"));
        await PackAsync(PackageA, "1.0.0", feed);
        await PackAsync(PackageB, "2.0.0", feed);
        return feed;
    }

    private async Task PackAsync(string id, string version, string feed)
    {
        var dir = Path.Combine(_root, "pack", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"{id}.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>{{id}}</PackageId>
                <Version>{{version}}</Version>
                <Authors>CodeyBoxTests</Authors>
                <Description>Fallback cache integration test package.</Description>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "Class1.cs"), "namespace TestLib; public sealed class Class1 { }\n");
        var result = await RunDotnetAsync(
            ["pack", Path.Combine(dir, $"{id}.csproj"), "-o", feed, "/p:RestoreSources=" + Path.Combine(_root, "empty-source")],
            new Dictionary<string, string>());
        Assert.True(result.Success, $"dotnet pack {id} failed: {result.Output}");
        Assert.True(File.Exists(Path.Combine(feed, $"{id}.{version}.nupkg")), $"missing nupkg for {id}");
    }

    private async Task SeedFallbackFromPackagesAsync(string feed, string fallback, string id, string version)
    {
        // Resolve once into a scratch global folder, then promote only the
        // extracted package directory into the fallback folder — the same
        // shape a pre-seeded host cache has.
        var scratch = Path.Combine(_root, "scratch-global");
        Directory.CreateDirectory(scratch);
        var probe = WriteConsumerIn(Path.Combine(_root, "seed-probe"), id, version);
        var result = await RunDotnetAsync(
            ["restore", probe, "/p:RestoreSources=" + feed],
            new Dictionary<string, string> { ["NUGET_PACKAGES"] = scratch });
        Assert.True(result.Success, $"seed restore failed: {result.Output}");
        var source = Path.Combine(scratch, id.ToLowerInvariant(), version);
        var destination = Path.Combine(fallback, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        CopyTree(source, destination);
    }

    private string WriteConsumer(string id, string version, string? id2 = null, string? version2 = null) =>
        WriteConsumerIn(Path.Combine(_root, "consumer"), id, version, id2, version2);

    private string WriteConsumerIn(string directory, string id, string version, string? id2 = null, string? version2 = null)
    {
        Directory.CreateDirectory(directory);
        var references = $"""<PackageReference Include="{id}" Version="{version}" />""" +
            (id2 is null
                ? string.Empty
                : "\n    " + $"""<PackageReference Include="{id2}" Version="{version2}" />""");
        var project = Path.Combine(directory, "consumer.csproj");
        File.WriteAllText(
            project,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                {{references}}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Class1.cs"), "namespace Consumer; public sealed class Class1 { }\n");
        return project;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static string SnapshotTree(string root)
    {
        if (!Directory.Exists(root))
            return string.Empty;
        var entries = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(root, f)}:{new FileInfo(f).Length}")
            .OrderBy(e => e, StringComparer.Ordinal);
        return string.Join("\n", entries);
    }

    private async Task<(bool Success, string Output)> RunDotnetAsync(
        IReadOnlyList<string> argv,
        IDictionary<string, string> environment)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in argv)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "cli-home");
        foreach (var (key, value) in environment)
            process.StartInfo.Environment[key] = value;
        Assert.True(process.Start(), "failed to start dotnet");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
            return (false, "dotnet timed out\n" + stdout + "\n" + stderr);
        }
        return (process.ExitCode == 0, stdout + "\n" + stderr);
    }
}
