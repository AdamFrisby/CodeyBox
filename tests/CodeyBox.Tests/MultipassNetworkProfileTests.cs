using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="MultipassSandboxProvider.BuildLaunchArgv"/>:
/// the host-enforced-network-profile selection logic. These don't touch
/// multipass at all — they verify that a profile name in the spec turns
/// into the correct <c>--network &lt;bridge&gt;</c> argv.
///
/// This is the single place where the orchestrator's "which bridge"
/// decision becomes a concrete CLI flag, so a regression in the mapping
/// would silently put the VM on the wrong network.
/// </summary>
public sealed class MultipassNetworkProfileTests
{
    private static MultipassSandboxProvider NewProvider(IReadOnlyDictionary<string, string>? profiles = null) =>
        new(new MultipassSandboxOptions
        {
            NetworkProfiles = profiles ?? new Dictionary<string, string>(),
        }, NullLogger<MultipassSandboxProvider>.Instance);

    private static SandboxSpec SpecWithProfile(string? profile) => new()
    {
        ImageReference = "ignored",
        Network = new SandboxNetworkPolicy { ProfileName = profile },
    };

    [Fact]
    public void NoProfile_OmitsNetworkArg()
    {
        var p = NewProvider();
        var argv = p.BuildLaunchArgv("vm-x", SpecWithProfile(null), "/tmp/cloud.yaml");
        Assert.DoesNotContain("--network", argv);
    }

    [Fact]
    public void ProfileWithMappedBridge_AddsNetworkArg()
    {
        var p = NewProvider(new Dictionary<string, string>
        {
            ["claude"] = "codeybox-net-claude",
            ["isolated"] = "codeybox-net-isolated",
        });
        var argv = p.BuildLaunchArgv("vm-x", SpecWithProfile("claude"), "/tmp/cloud.yaml");
        var idx = argv.ToList().IndexOf("--network");
        Assert.True(idx > 0, $"--network not found in argv: [{string.Join(' ', argv)}]");
        Assert.Equal("name=codeybox-net-claude,mode=auto", argv[idx + 1]);
    }

    [Fact]
    public void UnknownProfile_ThrowsWithListOfAvailable()
    {
        var p = NewProvider(new Dictionary<string, string>
        {
            ["claude"] = "codeybox-net-claude",
        });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            p.BuildLaunchArgv("vm-x", SpecWithProfile("does-not-exist"), "/tmp/cloud.yaml"));
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void EmptyProfileMap_AndRequestedProfile_ThrowsClearly()
    {
        // No profiles configured but the spec asks for one — should fail
        // loudly, NOT silently fall back to "no enforcement".
        var p = NewProvider(profiles: null);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            p.BuildLaunchArgv("vm-x", SpecWithProfile("anything"), "/tmp/cloud.yaml"));
        Assert.Contains("setup-host-networks.sh", ex.Message);
    }

    [Fact]
    public void NetworkArgComesBeforeImageReference()
    {
        // Multipass parses positional args after named flags. Image must
        // be last; --network is a flag.
        var p = NewProvider(new Dictionary<string, string>
        {
            ["claude"] = "codeybox-net-claude",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
        };
        var argv = p.BuildLaunchArgv("vm-x", spec, "/tmp/cloud.yaml");
        var list = argv.ToList();
        var networkIdx = list.IndexOf("--network");
        var imageIdx = list.IndexOf("24.04");
        Assert.True(networkIdx > 0);
        Assert.True(imageIdx > networkIdx, $"image {imageIdx} must come after --network {networkIdx}");
    }

    [Fact]
    public async Task EnvironmentFileContent_ShellSourcesMultilineJsonLiterally()
    {
        var value = """
            {
              "tokens": {
                "access_token": "tok$`'\value"
              }
            }
            """;
        var content = MultipassSandboxProvider.BuildEnvironmentFileContent(
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = value });
        var path = Path.Combine(Path.GetTempPath(), "codeybox-env-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, content);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(". \"$1\"; printf %s \"$CODEX_AUTH_JSON\"");
            psi.ArgumentList.Add("codeybox-env-test");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", stderr);
            Assert.Equal(value, stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
