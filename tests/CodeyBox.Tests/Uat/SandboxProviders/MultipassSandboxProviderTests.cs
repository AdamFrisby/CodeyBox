using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Multipass sandbox provider - Runs agents in isolated Ubuntu VMs with host-enforced network profiles</c>.
/// Plan anchor: docs/uat/00-plan.md#multipass-sandbox-provider---runs-agents-in-isolated-ubuntu-vms-with-host-enforced-network-profiles
/// </summary>
public sealed class MultipassSandboxProviderTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAAB9Wl9WAAAAC0lEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-multipass-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public void CloudInit_InstallsExecWrapperAndRouteServiceWithoutGuestFirewallRules()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["apt-get update", "npm install -g @anthropic-ai/claude-code"],
            "packages:\n  - git\n");

        Assert.StartsWith("#cloud-config", cloudInit, StringComparison.Ordinal);
        Assert.Contains("path: /usr/local/bin/codeybox-exec", cloudInit);
        Assert.Contains("path: /usr/local/sbin/codeybox-route", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-route.service", cloudInit);
        Assert.Contains("mkdir -p /work", cloudInit);
        Assert.Contains("systemctl enable --now codeybox-route.service", cloudInit);
        Assert.Contains("apt-get update", cloudInit);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", cloudInit);
        Assert.Contains("packages:\n  - git", cloudInit);
        Assert.DoesNotContain("iptables", cloudInit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ufw", cloudInit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloudInit_BaselineManifestRendersHashEntryIntoUserData()
    {
        var installer = "curl -fsSL https://antigravity.google/cli/install.sh | bash -s -- --dir /usr/local/bin";
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: null,
            includeGraphicalInstall: false,
            baselineInstallCommands: [installer]);

        Assert.Contains("path: /var/lib/codeybox/baseline-install-commands.sh", cloudInit);

        // Structural check: the cloud-init must be valid YAML AND the manifest
        // entry must be nested inside the write_files content block, not just
        // present as a substring. The original regression this test guards is
        // an entry that *looked* present but rendered outside its intended
        // block due to bad indentation — cloud-init silently drops it. The
        // post-redactor design persists only step ordering and a SHA-256 of
        // each configured command (the command text would otherwise leak
        // operator secrets into the LLM-controlled clone disk), so we assert
        // on the hash entry, not the installer string.
        var manifest = ExtractWriteFilesEntryContent(cloudInit, "/var/lib/codeybox/baseline-install-commands.sh");
        Assert.StartsWith("#!/bin/bash", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(installer, manifest);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var expected = Convert.ToHexString(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(installer))).ToLowerInvariant();
        Assert.Contains($"sha256={expected}", manifest);
    }

    [Fact]
    public void CloudInit_WriteFilePermissionsUseSchemaSafeOctalStrings()
    {
        var variants = new[]
        {
            MultipassSandboxProvider.BuildCloudInit(
                extraRuncmd: null,
                extraCloudInit: null,
                includeGraphicalInstall: false,
                baselineInstallCommands: ["npm install -g @openai/codex"]),
            MultipassSandboxProvider.BuildCloudInit(
                extraRuncmd: [],
                extraCloudInit: null,
                flavor: SandboxProfileFlavor.Graphical),
        };

        foreach (var cloudInit in variants)
        {
            Assert.DoesNotContain("permissions: '0644'", cloudInit, StringComparison.Ordinal);
            Assert.DoesNotContain("permissions: '0755'", cloudInit, StringComparison.Ordinal);
            Assert.DoesNotContain("permissions: '0700'", cloudInit, StringComparison.Ordinal);

            foreach (var entry in ExtractWriteFilesEntries(cloudInit))
            {
                var path = ExtractRequiredScalar(entry, "path");
                var permissions = ExtractRequiredScalar(entry, "permissions");

                Assert.True(
                    IsSchemaSafeOctalPermission(permissions),
                    $"write_files entry for path '{path}' must use 0o### permissions, got '{permissions}'");
            }
        }
    }

    /// <summary>
    /// Parses the rendered cloud-init as YAML, walks to <c>write_files</c>,
    /// and returns the <c>content</c> field of the entry whose <c>path</c>
    /// matches <paramref name="path"/>. Asserts that the document is a valid
    /// cloud-config mapping and that the target entry exists. Used by the
    /// baseline-install-manifest test to confirm the manifest is rendered as
    /// a proper YAML block scalar inside <c>write_files</c>, not just as a
    /// substring somewhere in the document.
    /// </summary>
    private static string ExtractWriteFilesEntryContent(string cloudInit, string path)
    {
        foreach (var entry in ExtractWriteFilesEntries(cloudInit))
        {
            var entryPath = ExtractRequiredScalar(entry, "path");
            if (!string.Equals(entryPath, path, StringComparison.Ordinal))
                continue;

            return ExtractRequiredScalar(entry, "content");
        }

        Assert.Fail($"no write_files entry found for path '{path}'");
        return string.Empty;
    }

    private static IReadOnlyList<YamlDotNet.RepresentationModel.YamlMappingNode> ExtractWriteFilesEntries(string cloudInit)
    {
        // Strip the leading "#cloud-config" header — it's a cloud-init marker
        // comment, not a YAML directive, and YamlDotNet treats it like any
        // other comment so the parse still works. We pass the full text in
        // for fidelity (an indentation regression would still surface as a
        // parse failure here).
        using var reader = new StringReader(cloudInit);
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(reader);
        Assert.Single(stream.Documents);
        var root = Assert.IsType<YamlDotNet.RepresentationModel.YamlMappingNode>(stream.Documents[0].RootNode);

        var writeFilesKey = new YamlDotNet.RepresentationModel.YamlScalarNode("write_files");
        Assert.True(
            root.Children.TryGetValue(writeFilesKey, out var writeFilesNode),
            "cloud-init must contain a top-level write_files block");
        var entries = Assert.IsType<YamlDotNet.RepresentationModel.YamlSequenceNode>(writeFilesNode);
        return entries.Children
            .Select(entry => Assert.IsType<YamlDotNet.RepresentationModel.YamlMappingNode>(entry))
            .ToList();
    }

    private static string ExtractRequiredScalar(
        YamlDotNet.RepresentationModel.YamlMappingNode entry,
        string key)
    {
        var scalarKey = new YamlDotNet.RepresentationModel.YamlScalarNode(key);
        Assert.True(
            entry.Children.TryGetValue(scalarKey, out var value),
            $"write_files entry is missing a {key} field");
        var scalar = Assert.IsType<YamlDotNet.RepresentationModel.YamlScalarNode>(value);
        return scalar.Value ?? string.Empty;
    }

    private static bool IsSchemaSafeOctalPermission(string permissions)
    {
        if (permissions.Length != 5 || !permissions.StartsWith("0o", StringComparison.Ordinal))
            return false;
        return permissions[2..].All(c => c is >= '0' and <= '7');
    }

    [Fact]
    public void CloudInit_BaselineManifestPersistsHashesNotCommandText()
    {
        // The manifest is persisted to /var/lib/codeybox/baseline-install-commands.sh
        // inside the baked baseline, then inherited by every (LLM-controlled)
        // clone. Raw install commands routinely carry registry tokens, basic-
        // auth URLs, or env-assigned API keys — including QUOTED forms
        // (`GITHUB_TOKEN="ghp_..."`, `--token 'npm_...'`) that a regex
        // redactor cannot reliably scrub. We persist ONLY a SHA-256 of each
        // configured command plus its step index; the command text itself is
        // never written into the image.
        var cmds = new[]
        {
            "npm install --registry https://my-registry.example/ --token=npm_abc123XYZdefSecretToken --save",
            // Quoted env-var form — previously bypassed the redactor.
            "GITHUB_TOKEN=\"ghp_aaaaaaaabbbbbbbbccccccccddddddddeeee\" npm i -g something",
            // Single-quoted CLI flag value — also bypassed the redactor.
            "npm publish --token 'npm_QuotedSecretZ987YYY'",
            "curl -fsSL https://user:p@ssword1@private.example/install.sh | bash",
            "curl -H \"Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig\" https://api.example/install",
        };
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: null,
            includeGraphicalInstall: false,
            baselineInstallCommands: cmds);

        var manifest = ExtractWriteFilesEntryContent(cloudInit, "/var/lib/codeybox/baseline-install-commands.sh");

        // No secret material from any input — quoted or not — is in the manifest.
        Assert.DoesNotContain("npm_abc123XYZdefSecretToken", manifest);
        Assert.DoesNotContain("ghp_aaaaaaaabbbbbbbbccccccccddddddddeeee", manifest);
        Assert.DoesNotContain("npm_QuotedSecretZ987YYY", manifest);
        Assert.DoesNotContain("p@ssword1", manifest);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.payload.sig", manifest);
        // Command text itself is not persisted — not even the non-secret prefix.
        Assert.DoesNotContain("npm install --registry", manifest);
        Assert.DoesNotContain("npm publish", manifest);
        Assert.DoesNotContain("Authorization: Bearer", manifest);

        // Each configured command appears as a step entry with its SHA-256.
        for (var i = 0; i < cmds.Length; i++)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var expected = Convert.ToHexString(
                sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(cmds[i]))).ToLowerInvariant();
            Assert.Contains($"step {i + 1} (configured index {i + 1}) sha256={expected}", manifest);
        }
    }

    [Theory]
    // Bare keys must be rejected so a runcmd: / write_files: in the operator fragment
    // doesn't duplicate (and silently clobber) CodeyBox's own generated block.
    [InlineData("runcmd", "runcmd")]
    [InlineData("write_files", "write_files")]
    // YAML allows the same mapping key in quoted form; PyYAML parses both spellings
    // to the same string, so the validator must too — otherwise an entry like
    // "runcmd": slips past the text check and produces two top-level runcmd blocks,
    // and the later-appended caller fragment wins. (This is the exact silent-drop
    // shape that surfaced as the agy-installer regression on 2026-06-10.)
    [InlineData("\"runcmd\"", "runcmd")]
    [InlineData("'runcmd'", "runcmd")]
    [InlineData("\"write_files\"", "write_files")]
    [InlineData("'write_files'", "write_files")]
    public void CloudInit_RejectsExtraCloudInitThatClobbersGeneratedTopLevelBlocks(string keySource, string expectedKey)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultipassSandboxProvider.BuildCloudInit(
                extraRuncmd: [],
                extraCloudInit: $"{keySource}:\n  - echo bad\n"));

        Assert.Contains($"top-level '{expectedKey}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MultipassExtraRuncmd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudInit_InstallsTcpKeepaliveSysctlForSuspendResumeRecovery()
    {
        // R8-core: after a multipass suspend/start cycle the in-VM agent's
        // long-lived TCP connections may be hanging in the kernel waiting for
        // a peer that doesn't know the suspend happened. The keepalive
        // sysctl makes that detection fast (~45s worst-case) instead of the
        // OS default of ~2h.
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(extraRuncmd: [], extraCloudInit: null);

        Assert.Contains("path: /etc/sysctl.d/99-codeybox-keepalive.conf", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_time = 30", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_intvl = 5", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_probes = 3", cloudInit);
        // Applied immediately on first boot so the first agent run benefits.
        Assert.Contains("sysctl --system", cloudInit);
    }

    [Fact]
    public void CloudInit_GraphicalFlavor_InstallsDesktopVncAndInputTools()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["echo project setup"],
            extraCloudInit: null,
            SandboxProfileFlavor.Graphical);

        Assert.Contains("path: /etc/systemd/system/codeybox-xvfb.service", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-xfce.service", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-vnc.service", cloudInit);
        Assert.Contains("xvfb x11vnc xfce4", cloudInit);
        Assert.Contains("xdotool scrot ffmpeg", cloudInit);
        Assert.Contains("x11-utils socat", cloudInit);
        Assert.Contains($"-rfbport {SandboxConventions.GraphicalVncPort}", cloudInit);
        Assert.Contains("-listen \"$listen_addr\"", cloudInit);
        Assert.Contains("-allow \"$host_addr\"", cloudInit);
        Assert.Contains("-rfbauth \"$password_file\"", cloudInit);
        Assert.DoesNotContain("-listen 127.0.0.1", cloudInit);
        Assert.DoesNotContain("-allow 127.0.0.1", cloudInit);
        Assert.DoesNotContain("-nopw", cloudInit);
        Assert.Contains("echo project setup", cloudInit);
        Assert.True(
            cloudInit.IndexOf("systemctl enable --now codeybox-route.service", StringComparison.Ordinal)
            < cloudInit.IndexOf("apt-get update", StringComparison.Ordinal),
            "graphical package install must run after route swap");

        var headlessCloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["echo project setup"],
            extraCloudInit: null,
            SandboxProfileFlavor.Headless);
        Assert.DoesNotContain("codeybox-xvfb.service", headlessCloudInit);
        Assert.DoesNotContain("codeybox-vnc.service", headlessCloudInit);
        Assert.DoesNotContain("xvfb x11vnc xfce4", headlessCloudInit);
        Assert.DoesNotContain("xdotool scrot ffmpeg", headlessCloudInit);
        Assert.DoesNotContain("x11-utils socat", headlessCloudInit);
    }

    [Fact]
    public void CloudInit_GraphicalFlavor_InstallsDesktopToolsWithEmptyRuncmd()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            [],
            extraCloudInit: null,
            SandboxProfileFlavor.Graphical);

        Assert.Contains("xvfb x11vnc xfce4", cloudInit);
        Assert.Contains("xdotool scrot ffmpeg", cloudInit);
        Assert.True(
            cloudInit.IndexOf("systemctl enable --now codeybox-route.service", StringComparison.Ordinal)
            < cloudInit.IndexOf("apt-get update", StringComparison.Ordinal),
            "graphical package install must run after route swap");
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxInjectsDisplayByDefault()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxPreservesCallerDisplayOverride()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "DISPLAY"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["DISPLAY"] = ":7",
                ["OTHER"] = "value",
            },
        });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains("DISPLAY=:7", argv);
        Assert.Contains("OTHER=value", argv);
        Assert.DoesNotContain($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxMergesDisplayIntoCallerEnvironment()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["OTHER"] = "value",
            },
        });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains("OTHER=value", argv);
        Assert.Contains($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_HeadlessSandboxDoesNotInjectDisplay()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.DoesNotContain($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task GraphicalCapabilities_RejectHeadlessSandbox()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.GetScreenshotAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Click }]));
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenScrotFails()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(2, "", "scrot failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("graphical screenshot failed", ex.Message);
        Assert.Contains("scrot failed", ex.Message);
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenCommandReturnsInvalidBase64()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "not base64", "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("invalid base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_ReturnsDecodedPngBytes()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String(TinyPng), "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var screenshot = await sandbox.GetScreenshotAsync();

        Assert.Equal(TinyPng, screenshot);
        AssertPngSignature(screenshot);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsDecodedNonPngBytes()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]), "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("non-PNG", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsOutputPastCaptureLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                137,
                new string('A', 1024),
                "",
                StdoutLimitExceeded: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("maximum capture size", ex.Message, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(MultipassSandbox.MaxScreenshotBase64StdoutBytes, call.MaxStdoutBytes);
        Assert.Equal(MultipassSandbox.MaxScreenshotStderrBytes, call.MaxStderrBytes);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsStderrPastCaptureLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                137,
                "",
                new string('e', 1024),
                StderrLimitExceeded: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("stderr", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum capture size", ex.Message, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(MultipassSandbox.MaxScreenshotBase64StdoutBytes, call.MaxStdoutBytes);
        Assert.Equal(MultipassSandbox.MaxScreenshotStderrBytes, call.MaxStderrBytes);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsDecodedPngPastLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String(new byte[5]), "")));
        var sandbox = NewMultipassSandbox(
            SandboxProfileFlavor.Graphical,
            runner,
            maxScreenshotPngBytes: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("PNG exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultProcessRunner_StopsReadingWhenStdoutLimitIsExceeded()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new DefaultProcessRunner();
        // The sh busy-loop competes for CPU with the .NET reader. On a fast
        // host the kill fires in milliseconds, but in a CPU-constrained
        // sandbox (e.g. an audit Multipass VM) the reader can be starved
        // long enough for a 5s cap to fire WaitForExitAsync via OCE before
        // the reader observes the limit. The ct here is only a backstop
        // against hangs — it must NOT be tight enough to race the actual
        // limit-detection path.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            ["sh", "-c", "while :; do printf 1234567890; done"],
            stdin: null,
            ct: timeout.Token,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);

        Assert.True(result.StdoutLimitExceeded);
        Assert.True(result.Stdout.Length <= 1024, $"captured {result.Stdout.Length} bytes");
    }

    [Fact]
    public async Task ExecAsync_ForwardsOutputCapsAndMapsLimitFlags()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                0,
                "partial output",
                "",
                StdoutLimitExceeded: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "yes output"],
            MaxStdoutBytes = 256,
            MaxStderrBytes = 128,
        });

        Assert.False(result.Success);
        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.StderrLimitExceeded);
        Assert.Equal("partial output", result.Stdout);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(256, call.MaxStdoutBytes);
        Assert.Equal(128, call.MaxStderrBytes);
    }

    [Fact]
    public async Task ExecAsync_PreferHttpIngestRoutesOutputThroughPerRunEndpoint()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        string? transferredEnvContent = null;
        string? observedToken = null;
        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", "/home/ubuntu/.codeybox-exec-env"])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, _])
            {
                transferredEnvContent = await File.ReadAllTextAsync(hostPath, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", "0600", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");
            if (IsCodeyboxExecArgv(argv))
            {
                Assert.NotNull(transferredEnvContent);
                var url = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable);
                var token = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable);
                var runId = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable);
                observedToken = token;
                using var client = new HttpClient();
                await PostAgentOutputAsync(client, url, runId, "ready", 0, token, "", ct);
                await PostAgentOutputAsync(client, url, runId, "stdout", 0, token, "hello over http\n", ct);
                await PostAgentOutputAsync(client, url, runId, "stderr", 0, token, "warn over http\n", ct);
                return new ProcessRunResult(0, "wrapper stdout\n", "wrapper stderr\n");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo ignored-by-fake-runner"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                [MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable] = "stale-caller-exit-token",
            },
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.True(result.Success);
        Assert.Equal("hello over http\nwrapper stdout\n", result.Stdout);
        Assert.Equal("warn over http\nwrapper stderr\n", result.Stderr);
        Assert.Equal(["hello over http\n"], stdoutChunks);
        Assert.Equal(["warn over http\n"], stderrChunks);
        Assert.NotNull(observedToken);
        Assert.NotNull(transferredEnvContent);
        Assert.DoesNotContain("stale-caller-exit-token", transferredEnvContent, StringComparison.Ordinal);
        Assert.DoesNotContain(
            MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable,
            transferredEnvContent,
            StringComparison.Ordinal);
        foreach (var call in runner.Calls)
            Assert.DoesNotContain(observedToken!, string.Join("\0", call.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public void MultipassProvider_AdvertisesConservativeBatchCapabilities()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProvider(_workspace, runner: runner);

        Assert.Equal(SandboxAgentOutputTransportKind.ExecPipe, provider.AgentOutputTransportKind);
        Assert.Equal(SandboxBatchLaunchMode.Attached, provider.BatchLaunchMode);
    }

    [Fact]
    public void MultipassSandbox_HttpIngestBindAddressAdvertisesDetachedBatchLaunch()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        Assert.Equal(SandboxAgentOutputTransportKind.HttpIngest, sandbox.AgentOutputTransportKind);
        Assert.Equal(SandboxBatchLaunchMode.Detached, sandbox.BatchLaunchMode);
    }

    [Fact]
    public void MultipassSandbox_WithoutHttpIngestBindAddressAdvertisesAttachedBatchLaunch()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        Assert.Equal(SandboxAgentOutputTransportKind.ExecPipe, sandbox.AgentOutputTransportKind);
        Assert.Equal(SandboxBatchLaunchMode.Attached, sandbox.BatchLaunchMode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExecAsync_DetachedBatchLaunchesWithoutAttachedCodeyboxExec()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var localVm = new LocalDetachedVm(_workspace);
        string? transferredEnvContent = null;
        string? transferredExitTokenContent = null;
        string? transferredStdinContent = null;
        string? transferredCommandScript = null;
        string? transferredLaunchScript = null;
        var forgedHttpExitRejected = false;
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, var destination])
            {
                var content = await File.ReadAllTextAsync(hostPath, ct);
                if (destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal))
                {
                    if (content.Contains(MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable, StringComparison.Ordinal))
                        transferredEnvContent = content;
                    else
                        transferredExitTokenContent = content;
                }
                else if (destination.Contains(".codeybox-exec-stdin/", StringComparison.Ordinal))
                    transferredStdinContent = content;
                else if (destination.Contains(".codeybox-exec/", StringComparison.Ordinal)
                         && content.Contains("codeybox_lock_dir=", StringComparison.Ordinal))
                    transferredLaunchScript = content;
                else if (destination.Contains(".codeybox-exec/", StringComparison.Ordinal))
                    transferredCommandScript = content;
                await localVm.TransferAsync(hostPath, destination, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin, "prompt over stdin\n");
                var launchResult = await localVm.RunLaunchScriptAsync(launchScript, stdin, ct);
                Assert.NotNull(transferredEnvContent);
                var url = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable);
                var token = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable);
                var runId = ExtractShellEnvValue(transferredEnvContent!, MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable);
                using var client = new HttpClient();
                using var forged = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{url}/{Uri.EscapeDataString(runId)}/exit/0");
                forged.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                forged.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("0\n"));
                using var forgedResponse = await client.SendAsync(forged, ct);
                forgedHttpExitRejected = forgedResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                return launchResult;
            }
            if (IsDetachedOutputSidecarRead(argv))
                return await localVm.ReadOutputSidecarAsync(argv[^1], ct);
            if (IsDetachedProcessGroupPoll(argv))
            {
                return await localVm.PollProcessGroupAsync(argv[^1], ct);
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
            {
                localVm.RemoveVmPaths(argv.Skip(5));
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "/bin/bash",
                "-c",
                """
                IFS= read -r prompt
                printf 'hello detached:%s\n' "$prompt"
                printf 'warn detached:%s\n' "$prompt" >&2
                exit 7
                """,
            ],
            Stdin = "prompt over stdin\n",
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
        Assert.True(forgedHttpExitRejected);
        Assert.NotNull(transferredEnvContent);
        Assert.DoesNotContain(
            MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable,
            transferredEnvContent);
        Assert.Null(transferredExitTokenContent);
        Assert.Equal("hello detached:prompt over stdin\n", result.Stdout);
        Assert.Equal("warn detached:prompt over stdin\n", result.Stderr);
        Assert.Equal(["hello detached:prompt over stdin\n"], stdoutChunks);
        Assert.Equal(["warn detached:prompt over stdin\n"], stderrChunks);
        Assert.Null(transferredStdinContent);
        Assert.NotNull(transferredCommandScript);
        Assert.Contains("--keep-stdin", transferredCommandScript);
        Assert.DoesNotContain("--stdin-file", transferredCommandScript);
        Assert.DoesNotContain(".codeybox-exec-stdin", transferredCommandScript, StringComparison.Ordinal);
        Assert.NotNull(transferredLaunchScript);
        Assert.Contains("codeybox_stdin_file='/run/codeybox-exec/detached-", transferredLaunchScript);
        Assert.Contains("codeybox-detached: failed to publish stdin sidecar", transferredLaunchScript);
        Assert.Contains("setsid /bin/bash \"$codeybox_child_script\" \"$codeybox_pgid_marker\" \"$codeybox_stdin_file\" \"$codeybox_env_file\" \"$codeybox_exit_token_file\" '/bin/sh'", transferredLaunchScript);
        Assert.DoesNotContain("codeybox_exit_marker", transferredLaunchScript);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);

        var launchCall = Assert.Single(
            runner.Calls,
            c => c.Argv is [_, "exec", _, "--", "/bin/bash", var script]
                 && script.Contains("/detached-", StringComparison.Ordinal));
        Assert.False(launchCall.HasStdoutChunkCallback);
        Assert.False(launchCall.HasStderrChunkCallback);
        AssertDetachedLaunchStdin(launchCall.Stdin, "prompt over stdin\n");
    }

    [Fact]
    public async Task ExecAsync_DetachedBatchShortStdinConsumerPreservesAgentExitWhenProducerGetsSigpipe()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var localVm = new LocalDetachedVm(_workspace);
        var prompt = new string('x', 1024 * 1024) + "\n";
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, var destination])
            {
                await localVm.TransferAsync(hostPath, destination, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin, prompt);
                return await localVm.RunLaunchScriptAsync(launchScript, stdin, ct);
            }
            if (IsDetachedOutputSidecarRead(argv))
                return await localVm.ReadOutputSidecarAsync(argv[^1], ct);
            if (IsDetachedProcessGroupPoll(argv))
                return await localVm.PollProcessGroupAsync(argv[^1], ct);
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
            {
                localVm.RemoveVmPaths(argv.Skip(5));
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "/bin/bash",
                "-c",
                """
                IFS= read -r -n 1 ch
                printf 'first:%s\n' "$ch"
                exit 0
                """,
            ],
            Stdin = prompt,
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        }, timeout.Token);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("first:x\n", result.Stdout);
        Assert.DoesNotContain("141", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);
    }

    [Fact]
    public async Task ExecAsync_DetachedBatchPreservesWrapperDiagnosticsBeforeHttpStreaming()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var localVm = new LocalDetachedVm(_workspace);
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, var destination])
            {
                await localVm.TransferAsync(hostPath, destination, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return await localVm.RunLaunchScriptAsync(launchScript, stdin, ct);
            }
            if (IsDetachedOutputSidecarRead(argv))
                return await localVm.ReadOutputSidecarAsync(argv[^1], ct);
            if (IsDetachedProcessGroupPoll(argv))
                return await localVm.PollProcessGroupAsync(argv[^1], ct);

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["/bin/sh", "-c", "printf should-not-run"],
            WorkingDirectory = "/work/missing-detached-workdir",
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(127, result.ExitCode);
        Assert.Contains("codeybox-exec: failed to cd to", result.Stderr);
        Assert.Contains("missing-detached-workdir", result.Stderr);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);
    }

    [Fact]
    public async Task ExecAsync_DetachedBatchWithoutBindAddressFailsBeforeAttachedFallback()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
            Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(MultipassSandbox.AgentOutputHttpSetupFailedExitCode, result.ExitCode);
        Assert.Contains(MultipassSandbox.AgentOutputHttpSetupFailureMarker, result.Stderr);
        Assert.Contains("no bind address", result.Stderr);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExecAsync_DetachedBatchWhenListenerCannotStartFailsBeforeAttachedFallback()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
            Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv))));
        var sandbox = NewLoopbackHttpIngestSandbox(
            runner,
            static (_, _, _, _, _, _) => Task.FromResult<MultipassAgentOutputHttpIngestSession?>(null));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(MultipassSandbox.AgentOutputHttpSetupFailedExitCode, result.ExitCode);
        Assert.Contains(MultipassSandbox.AgentOutputHttpSetupFailureMarker, result.Stderr);
        Assert.Contains("listener could not start", result.Stderr);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExecAsync_DetachedMarkerMissingAtPollTimeFailsWithCleanupDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
                return Task.FromResult(new ProcessRunResult(0, "missing\n", ""));
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("detached exec process group marker", result.Stderr);
        Assert.Contains("was not written", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedProcessGroupStaysAliveBeyondReapWindowIsTerminated()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        // Once the authenticated exit arrives, EnsureDetachedProcessGroupReapedAsync
        // verifies the process group has actually wound down. If it does not,
        // the supervisor escalates to TERM and surfaces the failure if the group
        // still appears alive after that escalation.
        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                    await exitPoster.PostExitAsync(0);
                // Use the production shape for an exit marker with a still-live
                // process group: "exited <pgid> <exit> alive".
                return new ProcessRunResult(0, "exited 12345 0 alive\n", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, killCalls);
        Assert.Contains("detached exec process group 12345 remained alive after termination", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedTerminationTransportFailureWithLiveGroupFailsWithCleanupDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        // Same shape as the sibling test above, but the TERM escalation itself
        // fails — the diagnostic must still surface the live process group.
        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(42, "", "kill transport failed");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                    await exitPoster.PostExitAsync(0);
                return new ProcessRunResult(0, "exited 12345 0 alive\n", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, killCalls);
        Assert.Contains("detached exec process group 12345 remained alive after termination", result.Stderr);
    }

    [Fact]
    public void BuildDetachedLaunchScript_ScrubsWrapperEnvironmentAndPublishesProcessGroupMarker()
    {
        var script = MultipassSandbox.BuildDetachedLaunchScript(
            "/home/ubuntu/.codeybox-exec-env/env file",
            "/home/ubuntu/.codeybox-exec/detached marker.pgid",
            null,
            ["/bin/sh", "/home/ubuntu/.codeybox-exec/command script.sh"]);

        Assert.Contains(
            "unset CODEYBOX_AGENT_OUTPUT_URL CODEYBOX_AGENT_OUTPUT_TOKEN CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN CODEYBOX_AGENT_OUTPUT_RUN_ID",
            script);
        Assert.DoesNotContain("unset CODEYBOX_AGENT_RUN_ID", script);
        Assert.Contains("codeybox_lock_dir=\"$codeybox_pgid_marker.lock\"", script);
        Assert.Contains("codeybox_exit_token_file=''", script);
        Assert.Contains("codeybox-detached: failed to publish exit token sidecar", script);
        Assert.Contains("codeybox_output_exit_token=$(codeybox_root_sh 'cat -- \"$1\"", script);
        Assert.Contains("codeybox_root_sh 'rm -f -- \"$1\"' \"$codeybox_exit_token_file\"", script);
        Assert.Contains("sudo -n sh -c", script);
        Assert.Contains("while ! codeybox_root_sh 'mkdir \"$1\" 2>/dev/null' \"$codeybox_lock_dir\"", script);
        Assert.Contains("codeybox_drain_stdin", script);
        Assert.Contains("if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then codeybox_drain_stdin; exit 0; fi", script);
        Assert.Contains("setsid /bin/bash \"$codeybox_child_script\" \"$codeybox_pgid_marker\" \"$codeybox_stdin_file\" \"$codeybox_env_file\" \"$codeybox_exit_token_file\" '/bin/sh' '/home/ubuntu/.codeybox-exec/command script.sh'", script);
        Assert.Contains("codeybox_detached_pid=$!", script);
        Assert.Contains("codeybox_pgid=$$", script);
        Assert.Contains("printf \"%s\\n\" \"$pgid\" > \"$tmp\" && mv -f \"$tmp\" \"$marker\"", script);
        Assert.Contains("mv -f \"$exit_tmp\" \"${marker}.exit\"", script);
        Assert.Contains("mv -f \"$stdout_tmp\" \"${marker}.stdout\"", script);
        Assert.Contains("mv -f \"$stderr_tmp\" \"${marker}.stderr\"", script);
        Assert.Contains("kill -TERM \"-$codeybox_detached_pid\"", script);
        Assert.Contains("while kill -0 \"-$codeybox_detached_pid\"", script);
        Assert.Contains("kill -KILL \"-$codeybox_detached_pid\"", script);
        Assert.Contains("wait \"$codeybox_detached_pid\" 2>/dev/null", script);
        var termIndex = script.IndexOf("kill -TERM \"-$codeybox_detached_pid\"", StringComparison.Ordinal);
        var sleepIndex = script.IndexOf("sleep 0.05", termIndex, StringComparison.Ordinal);
        var killIndex = script.IndexOf("kill -KILL \"-$codeybox_detached_pid\"", termIndex, StringComparison.Ordinal);
        var waitIndex = script.IndexOf("wait \"$codeybox_detached_pid\" 2>/dev/null", termIndex, StringComparison.Ordinal);
        var timeoutExitIndex = script.IndexOf(
            "exit 88",
            waitIndex,
            StringComparison.Ordinal);
        Assert.True(termIndex >= 0, "TERM not emitted");
        Assert.True(sleepIndex > termIndex, "sleep must follow TERM");
        Assert.True(killIndex > sleepIndex, "KILL must follow the post-TERM sleep");
        Assert.True(waitIndex > killIndex, "wait must follow KILL");
        Assert.True(timeoutExitIndex > waitIndex, "timeout path must report supervisor-setup exit code");
        Assert.Contains("if ! kill -0 \"$codeybox_detached_pid\" 2>/dev/null; then", script);
        Assert.Contains("exit \"$codeybox_child_rc\"", script);
        Assert.DoesNotContain("codeybox_output_exit_token=\"${CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN:-}\"", script);
        Assert.DoesNotContain("CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN=\"$codeybox_output_exit_token\"", script);
        Assert.DoesNotContain("codeybox_exit_marker", script);
        Assert.Contains("exit 88", script);
    }

    [Fact]
    public void BuildDetachedLaunchScript_DefaultMarkerWaitSecondsIsThirty()
    {
        var script = MultipassSandbox.BuildDetachedLaunchScript(
            "/home/ubuntu/.codeybox-exec-env/env",
            "/home/ubuntu/.codeybox-exec/detached.pgid",
            null,
            ["/bin/sh", "-c", "printf should-run"]);

        Assert.Contains("codeybox_marker_wait_seconds=30\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDetachedLaunchScript_RejectsNegativeLaunchLockAttempts()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => MultipassSandbox.BuildDetachedLaunchScript(
            "/home/ubuntu/.codeybox-exec-env/env",
            "/home/ubuntu/.codeybox-exec/detached.pgid",
            null,
            ["/bin/sh", "-c", "printf should-not-run"],
            launchLockAttempts: -1));

        Assert.Equal("launchLockAttempts", ex.ParamName);
    }

    [Fact]
    public void BuildDetachedLaunchScript_RejectsNonPositiveMarkerWaitSeconds()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => MultipassSandbox.BuildDetachedLaunchScript(
            "/home/ubuntu/.codeybox-exec-env/env",
            "/home/ubuntu/.codeybox-exec/detached.pgid",
            null,
            ["/bin/sh", "-c", "printf should-not-run"],
            markerWaitSeconds: 0));

        Assert.Equal("markerWaitSeconds", ex.ParamName);
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ScrubsOutputEnvironmentFromDetachedChild()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "scrub-detached-launch-script",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-env-scrub");
        var commandScript = Path.Combine(_workspace, "detached-command-scrub.sh");
        var launchScript = Path.Combine(_workspace, "detached-launch-scrub.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-scrub.pgid");
        var visibleEnvironmentFile = Path.Combine(_workspace, "detached-visible-env");
        var visibleRunIdFile = Path.Combine(_workspace, "detached-visible-run-id");
        var doneFile = Path.Combine(_workspace, "detached-scrub.done");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(
            commandScript,
            """
            {
                env | grep -E '^(CODEYBOX_AGENT_OUTPUT_URL|CODEYBOX_AGENT_OUTPUT_TOKEN|CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN|CODEYBOX_AGENT_OUTPUT_RUN_ID)=' || true
            } > "$1"
            printf '%s' "${CODEYBOX_AGENT_RUN_ID:-}" > "$2"
            printf done > "$3"
            """);
        File.SetUnixFileMode(commandScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                ["/bin/sh", commandScript, visibleEnvironmentFile, visibleRunIdFile, doneFile]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var poisonedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = "poison-url",
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "poison-token",
            ["CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN"] = "poison-exit-token",
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "poison-run-id",
            ["CODEYBOX_AGENT_RUN_ID"] = "poison-agent-run-id",
        };

        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: MergeEnvironment(poisonedEnvironment, FakeSudoPathEnvironment()));
        await WaitForFileAsync(doneFile, TimeSpan.FromSeconds(3));

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.Equal("", await File.ReadAllTextAsync(visibleEnvironmentFile));
        Assert.Equal("poison-agent-run-id", await File.ReadAllTextAsync(visibleRunIdFile));
        await WaitForProcessGroupGoneAsync(processGroupMarker, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_RunsCommandInBackgroundAndReturnsBeforeCommandCompletes()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "run-detached-launch-script",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-env");
        var commandScript = Path.Combine(_workspace, "detached-command.sh");
        var launchScript = Path.Combine(_workspace, "detached-launch.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached.pgid");
        var doneFile = Path.Combine(_workspace, "detached.done");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(commandScript, "sleep 3\nprintf done > \"$1\"\n");
        File.SetUnixFileMode(commandScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(envFile, processGroupMarker, null, ["/bin/sh", commandScript, doneFile]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sw = Stopwatch.StartNew();
        var (exit, _, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment());
        sw.Stop();

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"detached launch stayed attached for {sw.Elapsed}");
        Assert.True(File.Exists(processGroupMarker));
        await WaitForFileAsync(doneFile, TimeSpan.FromSeconds(6));
        await WaitForProcessGroupGoneAsync(processGroupMarker, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_PreflightRejectsListenerWithoutReadyProtocol()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var acceptCts = new CancellationTokenSource();
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                await Task.Delay(TimeSpan.FromSeconds(10), acceptCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var envFile = Path.Combine(_workspace, "detached-slow-ready.env");
        var commandScript = Path.Combine(_workspace, "detached-slow-ready-command.sh");
        var launchScript = Path.Combine(_workspace, "detached-slow-ready-launch.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-slow-ready.pgid");
        var doneFile = Path.Combine(_workspace, "detached-slow-ready.done");
        await File.WriteAllTextAsync(
            envFile,
            MultipassSandboxProvider.BuildEnvironmentFileContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] =
                    $"http://127.0.0.1:{port}/codeybox-agent-output",
                [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "stream-token",
                [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "slow-ready",
            }));
        await File.WriteAllTextAsync(commandScript, "printf done > \"$1\"\n");
        File.SetUnixFileMode(commandScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(envFile, processGroupMarker, null, ["/bin/sh", commandScript, doneFile]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();
            var (exit, _, stderr) = await RunLocalProcessAsync(
                "/bin/bash",
                [launchScript],
                timeout.Token,
                environmentOverrides: FakeSudoPathEnvironment());
            sw.Stop();

            Assert.Equal(86, exit);
            Assert.Contains("agent output HTTP ingest unavailable before launch", stderr, StringComparison.Ordinal);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"detached launch waited too long for HTTP readiness for {sw.Elapsed}");
            Assert.False(File.Exists(doneFile));
            Assert.False(File.Exists(processGroupMarker));
        }
        finally
        {
            acceptCts.Cancel();
            listener.Stop();
            await acceptTask;
        }
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_PreflightRejectsMalformedHttpIngestUrlBeforeLaunch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var envFile = Path.Combine(_workspace, "detached-malformed-ready.env");
        var commandScript = Path.Combine(_workspace, "detached-malformed-ready-command.sh");
        var launchScript = Path.Combine(_workspace, "detached-malformed-ready-launch.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-malformed-ready.pgid");
        var doneFile = Path.Combine(_workspace, "detached-malformed-ready.done");
        await File.WriteAllTextAsync(
            envFile,
            MultipassSandboxProvider.BuildEnvironmentFileContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = "http://",
                [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = "stream-token",
                [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = "malformed-ready",
            }));
        await File.WriteAllTextAsync(commandScript, "printf done > \"$1\"\n");
        File.SetUnixFileMode(commandScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(envFile, processGroupMarker, null, ["/bin/sh", commandScript, doneFile]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment());

        Assert.Equal(86, exit);
        Assert.Equal("", stdout);
        Assert.Contains("agent output HTTP ingest unavailable before launch", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(doneFile));
        Assert.False(File.Exists(processGroupMarker));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_DoesNotLaunchAgainWhenProcessGroupMarkerAlreadyExists()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "run-detached-launch-idempotent",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-env-idempotent");
        var commandScript = Path.Combine(_workspace, "detached-command-idempotent.sh");
        var launchScript = Path.Combine(_workspace, "detached-launch-idempotent.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-idempotent.pgid");
        var countFile = Path.Combine(_workspace, "detached.count");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(commandScript, "printf run >> \"$1\"\n");
        File.SetUnixFileMode(commandScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(envFile, processGroupMarker, null, ["/bin/sh", commandScript, countFile]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var first = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment());
        await WaitForFileAsync(countFile, TimeSpan.FromSeconds(3));
        var second = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment(),
            stdin: "retry-token-line-that-must-be-drained\n" + new string('x', 1024 * 1024));
        await Task.Delay(200);

        Assert.True(
            first.Exit == 0,
            $"first launch exited {first.Exit}; stderr=<{first.Stderr}>; stdout=<{first.Stdout}>");
        Assert.True(
            second.Exit == 0,
            $"second launch exited {second.Exit}; stderr=<{second.Stderr}>; stdout=<{second.Stdout}>");
        Assert.Equal("run", await File.ReadAllTextAsync(countFile));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ConsumesExitTokenLineWhenTokenSidecarAlreadyExists()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "detached-existing-exit-token",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-env-existing-exit-token");
        var launchScript = Path.Combine(_workspace, "detached-launch-existing-exit-token.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-existing-exit-token.pgid");
        var stdinFile = processGroupMarker + ".stdin";
        var exitTokenFile = processGroupMarker + ".exit-token";
        var capturedPromptFile = Path.Combine(_workspace, "detached-existing-exit-token.prompt");

        await File.WriteAllTextAsync(
            envFile,
            MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment(includeExitToken: false)));
        await File.WriteAllTextAsync(exitTokenFile, session.ExitToken);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                stdinFile,
                ["/bin/sh", "-c", "cat > \"$1\"", "codeybox-capture-stdin", capturedPromptFile],
                exitTokenFile: exitTokenFile));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment(),
            stdin: "retry-token-line-that-must-not-reach-agent\nagent prompt\n");
        await WaitForFileAsync(capturedPromptFile, TimeSpan.FromSeconds(3));
        await WaitForExitCodeAsync(session, 0, TimeSpan.FromSeconds(6));

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.Equal("agent prompt\n", await File.ReadAllTextAsync(capturedPromptFile));
        Assert.False(File.Exists(exitTokenFile));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_TimesOutWhenLaunchLockIsHeld()
    {
        if (OperatingSystem.IsWindows())
            return;

        var launchScript = Path.Combine(_workspace, "detached-launch-lock-timeout.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-lock-timeout.pgid");
        Directory.CreateDirectory(processGroupMarker + ".lock");
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                "/does/not/need/to/exist",
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run"],
                launchLockAttempts: 0));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: FakeSudoPathEnvironment());

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("codeybox-detached: timed out waiting for launch lock", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(processGroupMarker));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReturnsExit88WhenSupervisorDirectoryCannotBePrepared()
    {
        if (OperatingSystem.IsWindows())
            return;

        var launchScript = Path.Combine(_workspace, "detached-launch-supervisor-dir-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-supervisor-dir-fails.pgid");
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                "/does/not/need/to/exist",
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run"]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = CreateFakeSudoBin("#!/bin/sh\nexit 1\n") + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            });

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("codeybox-detached: failed to prepare supervisor directory", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(processGroupMarker));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReturnsExit88WhenStdinSidecarPublicationFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var launchScript = Path.Combine(_workspace, "detached-stdin-publication-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-stdin-publication-fails.pgid");
        var stdinFile = processGroupMarker + ".stdin";
        var sentinel = Path.Combine(_workspace, "detached-stdin-publication-fails.started");
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                "/does/not/need/to/exist",
                processGroupMarker,
                stdinFile,
                ["/bin/sh", "-c", "printf should-not-run > \"$1\"", "codeybox-stdin-fail", sentinel]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'stdin_file=$1'*) exit 1 ;; esac
            fi
            exec "$@"
            """);
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            },
            stdin: "prompt payload");

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("codeybox-detached: failed to publish stdin sidecar", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(stdinFile));
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReportsSetupFailureWhenStdinSidecarReadFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "detached-stdin-read-fails",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-stdin-read-fails.env");
        var launchScript = Path.Combine(_workspace, "detached-stdin-read-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-stdin-read-fails.pgid");
        var stdinFile = processGroupMarker + ".stdin";
        var exitTokenFile = processGroupMarker + ".exit-token";
        var sentinel = Path.Combine(_workspace, "detached-stdin-read-fails.started");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment(includeExitToken: false)));
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                stdinFile,
                ["/bin/sh", "-c", "cat >/dev/null; printf should-not-run > \"$1\"", "codeybox-stdin-read-fail", sentinel],
                exitTokenFile: exitTokenFile));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'cat -- "$1"'*)
                    case "$5" in *.stdin) exit 23 ;; esac
                    ;;
                esac
            fi
            exec "$@"
            """);
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            },
            stdin: session.ExitToken + "\nprompt payload");

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        await WaitForExitCodeAsync(session, 88, TimeSpan.FromSeconds(6));
        await WaitForProcessGroupGoneAsync(processGroupMarker, TimeSpan.FromSeconds(6));
        Assert.True(File.Exists(stdinFile));
        Assert.Contains(
            "codeybox-detached: failed to read stdin sidecar (exit 23)",
            await File.ReadAllTextAsync(processGroupMarker + ".stderr"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReturnsExit88WhenExitTokenSidecarPublicationFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var launchScript = Path.Combine(_workspace, "detached-exit-token-publication-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-exit-token-publication-fails.pgid");
        var exitTokenFile = processGroupMarker + ".exit-token";
        var sentinel = Path.Combine(_workspace, "detached-exit-token-publication-fails.started");
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                "/does/not/need/to/exist",
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run > \"$1\"", "codeybox-exit-token-fail", sentinel],
                exitTokenFile: exitTokenFile));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'token_file=$1'*) exit 1 ;; esac
            fi
            exec "$@"
            """);
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            },
            stdin: "exit-token-that-cannot-be-published\n" + new string('p', 1024 * 1024));

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("codeybox-detached: failed to publish exit token sidecar", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(exitTokenFile));
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReturnsExit88WhenProcessGroupMarkerPublicationFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "marker-publication-fails",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-marker-publication-fails.env");
        var launchScript = Path.Combine(_workspace, "detached-marker-publication-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-marker-publication-fails.pgid");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run"]));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'pgid=$2'*) exit 1 ;; esac
            fi
            exec "$@"
            """);
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            });

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("detached child exited before publishing process group marker", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(processGroupMarker));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_ReturnsExit88WhenProcessGroupMarkerPublicationTimesOut()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "marker-publication-times-out",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-marker-timeout.env");
        var launchScript = Path.Combine(_workspace, "detached-marker-timeout.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-marker-timeout.pgid");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run"],
                markerWaitSeconds: 5));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'pgid=$2'*) sleep 10; exit 1 ;; esac
            fi
            exec "$@"
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            timeout.Token,
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            });

        Assert.Equal(88, exit);
        Assert.Equal("", stdout);
        Assert.Contains("codeybox-detached: timed out waiting for process group marker", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(processGroupMarker));
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_MarkerTimeoutSigkillsChildThatIgnoresSigterm()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "marker-timeout-sigkill",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-marker-timeout-sigkill.env");
        var launchScript = Path.Combine(_workspace, "detached-marker-timeout-sigkill.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-marker-timeout-sigkill.pgid");
        var sudoProcessGroupFile = Path.Combine(_workspace, "detached-marker-timeout-sigkill.sudo-pgid");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment()));
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "printf should-not-run"],
                markerWaitSeconds: 5));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin($$"""
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'pgid=$2'*)
                    pgid=$(ps -o pgid= -p "$$" | tr -d ' ')
                    printf '%s\n' "$pgid" > {{MultipassSandboxProvider.ShellSingleQuote(sudoProcessGroupFile)}}
                    trap '' TERM
                    while :; do sleep 1; done
                    ;;
                esac
            fi
            exec "$@"
            """);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (exit, stdout, stderr) = await RunLocalProcessAsync(
                "/bin/bash",
                [launchScript],
                timeout.Token,
                environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
                });

            Assert.Equal(88, exit);
            Assert.Equal("", stdout);
            Assert.Contains("codeybox-detached: timed out waiting for process group marker", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(processGroupMarker));

            await WaitForFileAsync(sudoProcessGroupFile, TimeSpan.FromSeconds(1));
            var pgid = (await File.ReadAllTextAsync(sudoProcessGroupFile)).Trim();
            await WaitForProcessGroupIdGoneAsync(pgid, TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (File.Exists(sudoProcessGroupFile))
            {
                var pgid = (await File.ReadAllTextAsync(sudoProcessGroupFile)).Trim();
                await KillProcessGroupAsync(pgid);
            }
        }
    }

    [Fact]
    public async Task BuildDetachedLaunchScript_DetachedChildPostsAuthenticatedExitWhenSidecarPublicationFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "exit-sidecar-publication-fails",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var envFile = Path.Combine(_workspace, "detached-sidecar-publication-fails.env");
        var exitTokenFile = Path.Combine(_workspace, "detached-sidecar-publication-fails.token");
        var launchScript = Path.Combine(_workspace, "detached-sidecar-publication-fails.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-sidecar-publication-fails.pgid");
        await File.WriteAllTextAsync(envFile, MultipassSandboxProvider.BuildEnvironmentFileContent(session.BuildEnvironment(includeExitToken: false)));
        await File.WriteAllTextAsync(exitTokenFile, session.ExitToken);
        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                ["/bin/sh", "-c", "exit 3"],
                exitTokenFile: exitTokenFile));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var fakeSudo = CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            if [ "$1" = "sh" ] && [ "$2" = "-c" ]; then
                case "$3" in *'exit_code=$4'*) exit 1 ;; esac
            fi
            exec "$@"
            """);
        var (exit, stdout, stderr) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeSudo + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            });
        await WaitForProcessGroupGoneAsync(processGroupMarker, TimeSpan.FromSeconds(6));
        await WaitForExitCodeAsync(session, 3, TimeSpan.FromSeconds(6));

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.True(File.Exists(processGroupMarker));
        Assert.False(File.Exists(processGroupMarker + ".exit"));
        Assert.False(File.Exists(exitTokenFile));
        Assert.Contains("codeybox-detached: failed to publish output sidecars", session.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_DetachedTransientPollFailureIsToleratedAndAuthenticatedExitPreservesAgentExit()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                    return new ProcessRunResult(1, "", "instance \"codeybox-test\" does not exist");
                await exitPoster.PostExitAsync(0);
                return new ProcessRunResult(0, "exited 12345\n", "");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, killCalls);
        Assert.True(pollCalls >= 2);
        Assert.DoesNotContain("detached exec process group poll failed", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedAuthenticatedExitWhileProcessGroupAliveReturnsWithoutTimeout()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                {
                    await exitPoster.PostExitAsync(0);
                    return new ProcessRunResult(0, "alive 12345\n", "");
                }

                return new ProcessRunResult(0, "exited 12345 0 gone\n", "");
            }
            if (IsDetachedOutputSidecarRead(argv))
                return new ProcessRunResult(0, "", "");
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        }, timeout.Token);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.True(pollCalls >= 2);
        Assert.Equal(0, killCalls);
    }

    [Fact]
    public async Task ExecAsync_DetachedOutputSidecarReadFailureAppendsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var outputReadCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                    await exitPoster.PostExitAsync(0);
                return new ProcessRunResult(0, "exited 12345\n", "");
            }
            if (IsDetachedOutputSidecarRead(argv))
            {
                outputReadCalls++;
                return new ProcessRunResult(42, "", "sudo cat denied");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, outputReadCalls);
        Assert.Contains("failed to read detached exec output sidecar", result.Stderr);
        Assert.Contains("sudo cat denied", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedMarkerDisappearingDuringCleanupReturnsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                {
                    await exitPoster.PostExitAsync(0);
                    return new ProcessRunResult(0, "exited 12345\n", "");
                }

                return new ProcessRunResult(0, "missing\n", "");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, killCalls);
        Assert.Contains("disappeared before cleanup", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedPersistentPollFailureAfterAuthenticatedExitPreservesAgentExitAndReturnsCleanupDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                {
                    await exitPoster.PostExitAsync(23);
                    return new ProcessRunResult(0, "exited 12345 23 gone\n", "");
                }

                return new ProcessRunResult(42, "", "multipass control plane unavailable");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(23, result.ExitCode);
        Assert.True(pollCalls >= 2);
        Assert.Equal(1, killCalls);
        Assert.Contains("detached exec process group poll failed (exit 42): multipass control plane unavailable", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedTransientPollFailureBeforeAuthenticatedExitDoesNotTerminateProcessGroup()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var exitPoster = new CapturingDetachedExitPoster();
        var killCalls = 0;
        var pollCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return new ProcessRunResult(0, "", "");
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                if (pollCalls == 1)
                    return new ProcessRunResult(1, "", "multipass control plane unavailable");
                await exitPoster.PostExitAsync(0);
                return new ProcessRunResult(0, "exited 12345\n", "");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner, exitPoster.StartAsync);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, killCalls);
        Assert.True(pollCalls >= 2);
        Assert.DoesNotContain("detached exec process group poll failed", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedPersistentPollFailureBeforeAuthenticatedExitTerminatesGroupAndReturnsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var killCalls = 0;
        var pollCalls = 0;
        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                return Task.FromResult(new ProcessRunResult(42, "", "multipass control plane unavailable"));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(5, pollCalls);
        Assert.Equal(1, killCalls);
        Assert.Contains("detached exec process group poll failed (exit 42): multipass control plane unavailable", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedMalformedProcessGroupMarkerReturnsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        const string diagnostic = "detached exec process group marker /run/codeybox-exec/detached-test.pgid was malformed: not-a-pid\n";
        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                return Task.FromResult(new ProcessRunResult(73, "", diagnostic));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("detached exec process group marker", result.Stderr);
        Assert.Contains("was malformed: not-a-pid", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedMalformedProcessGroupPollStdoutReturnsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
                return Task.FromResult(new ProcessRunResult(0, "garbage marker state\n", ""));
            if (IsDetachedProcessGroupKill(argv))
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("detached exec process group poll returned malformed output: garbage marker state", result.Stderr);
    }

    [Theory]
    [InlineData("exited 12345\n")]
    [InlineData("exited 12345 0 gone\n")]
    public async Task ExecAsync_DetachedProcessGroupExitedWithoutAuthenticatedExitReturnsDiagnostic(string pollOutput)
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                // The PG is gone, but no host-authenticated completion arrived.
                // The supervisor must surface a diagnostic rather than guessing.
                return Task.FromResult(new ProcessRunResult(0, pollOutput, ""));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("detached exec process group 12345 exited without authenticated exit completion", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedProcessGroupMarkerMissingBeforeHttpExitReturnsDiagnostic()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                return Task.FromResult(new ProcessRunResult(0, "missing\n", ""));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("detached exec process group marker", result.Stderr);
        Assert.Contains("was not written", result.Stderr);
    }

    [Fact]
    public async Task ExecAsync_DetachedLauncherFailureReturnsLauncherResultWithoutFallback()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var pollCalls = 0;
        var killCalls = 0;
        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                return Task.FromResult(new ProcessRunResult(
                    88,
                    "",
                    "codeybox-detached: detached child exited before publishing process group marker (exit 88)\n"));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                pollCalls++;
                return Task.FromResult(new ProcessRunResult(99, "", "poll should not run after launch failure"));
            }
            if (IsDetachedProcessGroupKill(argv))
            {
                killCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (IsCodeyboxExecArgv(argv))
                return Task.FromResult(new ProcessRunResult(99, "", "attached exec fallback should not run"));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        });

        Assert.False(result.Success);
        Assert.Equal(88, result.ExitCode);
        Assert.Contains("before publishing process group marker", result.Stderr);
        Assert.Equal(0, pollCalls);
        Assert.Equal(1, killCalls);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);
    }

    [Theory]
    [InlineData("env-transfer")]
    [InlineData("env-chmod")]
    [InlineData("command")]
    [InlineData("launch")]
    public async Task ExecAsync_DetachedSetupFailureReturnsSetupFailureWithoutAttachedFallback(string failingSetup)
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var setupFailed = false;
        var launchCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, var destination])
            {
                var content = await File.ReadAllTextAsync(hostPath, ct);
                var isFailingTransfer =
                    (failingSetup == "env-transfer" && destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal))
                    || (failingSetup == "command"
                        && destination.Contains(".codeybox-exec/", StringComparison.Ordinal)
                        && !content.Contains("codeybox_lock_dir=", StringComparison.Ordinal))
                    || (failingSetup == "launch"
                        && destination.Contains(".codeybox-exec/", StringComparison.Ordinal)
                        && content.Contains("codeybox_lock_dir=", StringComparison.Ordinal));
                if (isFailingTransfer)
                {
                    setupFailed = true;
                    return new ProcessRunResult(1, "", "transfer denied");
                }
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", _, _])
            {
                if (failingSetup == "env-chmod" && argv[^1].Contains(".codeybox-exec-env/", StringComparison.Ordinal))
                {
                    setupFailed = true;
                    return new ProcessRunResult(1, "", "chmod denied");
                }
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                launchCalls++;
                return new ProcessRunResult(99, "", "detached launch should not run after setup failure");
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");
            if (IsCodeyboxExecArgv(argv))
                return new ProcessRunResult(99, "", "attached exec fallback should not run");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            Stdin = "prompt over stdin",
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.False(result.Success);
        Assert.True(setupFailed);
        Assert.Equal(0, launchCalls);
        Assert.Equal(MultipassSandbox.AgentOutputHttpSetupFailedExitCode, result.ExitCode);
        Assert.Contains(MultipassSandbox.AgentOutputHttpSetupFailureMarker, result.Stderr);
        Assert.Contains("denied", result.Stderr);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);
        Assert.Empty(stdoutChunks);
        Assert.Empty(stderrChunks);
    }

    [Fact]
    public async Task ExecAsync_DetachedHttpPreflightFailureReturnsLaunchFailureWithoutAttachedFallback()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var localVm = new LocalDetachedVm(_workspace);
        var detachedLaunchCalls = 0;
        var preflightFailedInGeneratedScript = false;
        var runner = new RecordingMultipassRunner(async (argv, stdin, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, var destination])
            {
                if (destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal))
                {
                    var content = await File.ReadAllTextAsync(hostPath, ct);
                    if (!content.Contains(MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable, StringComparison.Ordinal))
                    {
                        await localVm.TransferAsync(hostPath, destination, ct);
                        return new ProcessRunResult(0, "", "");
                    }

                    var broken = RewriteShellEnvValue(
                        content,
                        MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable,
                        "http://127.0.0.1:9/codeybox-agent-output");
                    var brokenHostPath = Path.Combine(_workspace, $"broken-env-{Guid.NewGuid():N}");
                    await File.WriteAllTextAsync(brokenHostPath, broken, ct);
                    await localVm.TransferAsync(brokenHostPath, destination, ct);
                    return new ProcessRunResult(0, "", "");
                }

                await localVm.TransferAsync(hostPath, destination, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                detachedLaunchCalls++;
                AssertDetachedLaunchStdin(stdin, "prompt over stdin");
                var launchResult = await localVm.RunLaunchScriptAsync(launchScript, stdin, ct);
                preflightFailedInGeneratedScript =
                    launchResult.ExitCode == MultipassSandbox.AgentOutputHttpSetupFailedExitCode
                    && launchResult.Stderr.Contains(MultipassSandbox.AgentOutputHttpSetupFailureMarker, StringComparison.Ordinal);
                return launchResult;
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
            {
                localVm.RemoveVmPaths(argv.Skip(5));
                return new ProcessRunResult(0, "", "");
            }
            if (IsCodeyboxExecArgv(argv))
                return new ProcessRunResult(99, "", "attached exec fallback should not run");

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            Stdin = "prompt over stdin",
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.False(result.Success);
        Assert.Equal(1, detachedLaunchCalls);
        Assert.True(preflightFailedInGeneratedScript);
        Assert.Equal(MultipassSandbox.AgentOutputHttpSetupFailedExitCode, result.ExitCode);
        Assert.Contains(MultipassSandbox.AgentOutputHttpSetupFailureMarker, result.Stderr);
        Assert.DoesNotContain(runner.Calls, IsCodeyboxExecCall);
        Assert.Empty(stdoutChunks);
        Assert.Empty(stderrChunks);
    }

    [Fact]
    public async Task ExecAsync_CancelledDetachedRunTerminatesRecordedProcessGroup()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        using var cts = new CancellationTokenSource();
        var detachedLaunchObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killAttempted = false;
        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                detachedLaunchObserved.TrySetResult();
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "exec", _, "--", "sudo", "-n", "sh", "-c", var command, "codeybox-detached-kill", var processGroupMarker]
                && command.Contains("kill -TERM", StringComparison.Ordinal))
            {
                killAttempted = true;
                Assert.Contains("/run/codeybox-exec/detached-", processGroupMarker, StringComparison.Ordinal);
                Assert.EndsWith(".pgid", processGroupMarker, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
            {
                return Task.FromResult(new ProcessRunResult(0, "alive 123\n", ""));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var execTask = sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        }, cts.Token);

        await detachedLaunchObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);

        Assert.True(killAttempted);
    }

    [Fact]
    public async Task ExecAsync_DetachedRunExceptionTerminatesRecordedProcessGroup()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var detachedLaunchObserved = false;
        var killAttempted = false;
        var runner = new RecordingMultipassRunner((argv, stdin, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "/bin/bash", var launchScript]
                && launchScript.Contains("/detached-", StringComparison.Ordinal))
            {
                AssertDetachedLaunchStdin(stdin);
                detachedLaunchObserved = true;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (IsDetachedProcessGroupPoll(argv))
                throw new InvalidOperationException("poll transport exploded after launch");
            if (IsDetachedProcessGroupKill(argv))
            {
                killAttempted = true;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            LaunchMode = SandboxExecLaunchMode.DetachedBatch,
        }, timeout.Token));

        Assert.Contains("poll transport exploded", ex.Message, StringComparison.Ordinal);
        Assert.True(detachedLaunchObserved);
        Assert.True(killAttempted);
    }

    [Fact]
    public async Task ExecAsync_HttpSetupFailureBeforeLaunchFallsBackToExecPipe()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        string? transferredEnvContent = null;
        var execCalls = 0;
        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", "/home/ubuntu/.codeybox-exec-env"])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "transfer", var hostPath, _])
            {
                transferredEnvContent = await File.ReadAllTextAsync(hostPath, ct);
                return new ProcessRunResult(0, "", "");
            }
            if (argv is [_, "exec", _, "--", "chmod", "0600", _])
                return new ProcessRunResult(0, "", "");
            if (argv is [_, "exec", _, "--", "rm", "-f", ..])
                return new ProcessRunResult(0, "", "");
            if (IsCodeyboxExecArgv(argv))
            {
                execCalls++;
                return execCalls == 1
                    ? new ProcessRunResult(
                        MultipassSandbox.AgentOutputHttpSetupFailedExitCode,
                        "",
                        MultipassSandbox.AgentOutputHttpSetupFailureMarker + "\n")
                    : new ProcessRunResult(0, "pipe stdout\n", "pipe stderr\n");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.True(result.Success);
        Assert.NotNull(transferredEnvContent);
        Assert.DoesNotContain(
            MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable,
            transferredEnvContent,
            StringComparison.Ordinal);
        Assert.Equal("pipe stdout\n", result.Stdout);
        Assert.Equal("pipe stderr\n", result.Stderr);
        Assert.Equal(2, execCalls);

        var wrapperCalls = runner.Calls.Where(IsCodeyboxExecCall).ToArray();
        Assert.Equal(2, wrapperCalls.Length);
        Assert.Contains("--env-file", wrapperCalls[0].Argv);
        Assert.False(wrapperCalls[0].HasStdoutChunkCallback);
        Assert.False(wrapperCalls[0].HasStderrChunkCallback);
        Assert.DoesNotContain("--env-file", wrapperCalls[1].Argv);
        Assert.True(wrapperCalls[1].HasStdoutChunkCallback);
        Assert.True(wrapperCalls[1].HasStderrChunkCallback);
        Assert.Empty(stdoutChunks);
        Assert.Empty(stderrChunks);
    }

    [Fact]
    public async Task ExecAsync_EnvFileTransferFailureFallsBackToExecPipeAndRestoresCallbacks()
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var transferCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "exec", _, "--", "mkdir", "-p", "/home/ubuntu/.codeybox-exec-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, _])
            {
                transferCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "transfer denied"));
            }
            if (IsCodeyboxExecArgv(argv))
                return Task.FromResult(new ProcessRunResult(0, "pipe stdout\n", "pipe stderr\n"));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = SandboxAgentOutputTransportPreference.PreferHttpIngest,
            StdoutChunkCallback = stdoutChunks.Add,
            StderrChunkCallback = stderrChunks.Add,
        });

        Assert.True(result.Success);
        Assert.Equal(1, transferCalls);
        Assert.Equal("pipe stdout\n", result.Stdout);
        Assert.Equal("pipe stderr\n", result.Stderr);

        var wrapperCall = Assert.Single(runner.Calls, IsCodeyboxExecCall);
        Assert.DoesNotContain("--env-file", wrapperCall.Argv);
        Assert.True(wrapperCall.HasStdoutChunkCallback);
        Assert.True(wrapperCall.HasStderrChunkCallback);
        Assert.Empty(stdoutChunks);
        Assert.Empty(stderrChunks);
    }

    [Theory]
    [InlineData(SandboxAgentOutputTransportPreference.ExecPipe, SandboxExecLaunchMode.Attached, null, null)]
    [InlineData(SandboxAgentOutputTransportPreference.PreferHttpIngest, SandboxExecLaunchMode.Attached, 256, null)]
    [InlineData(SandboxAgentOutputTransportPreference.PreferHttpIngest, SandboxExecLaunchMode.Attached, null, 128)]
    [InlineData(SandboxAgentOutputTransportPreference.PreferHttpIngest, SandboxExecLaunchMode.DetachedBatch, 256, null)]
    [InlineData(SandboxAgentOutputTransportPreference.PreferHttpIngest, SandboxExecLaunchMode.DetachedBatch, null, 128)]
    public async Task ExecAsync_UsesExecPipeUnlessHttpIngestIsPreferredAndOutputIsUnbounded(
        SandboxAgentOutputTransportPreference transport,
        SandboxExecLaunchMode launchMode,
        int? maxStdoutBytes,
        int? maxStderrBytes)
    {
        if (MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress("lo") is null)
            return;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (IsCodeyboxExecArgv(argv))
                return Task.FromResult(new ProcessRunResult(0, "pipe stdout\n", "pipe stderr\n"));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewLoopbackHttpIngestSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["agent-cli", "--json"],
            AgentOutputTransport = transport,
            LaunchMode = launchMode,
            MaxStdoutBytes = maxStdoutBytes,
            MaxStderrBytes = maxStderrBytes,
            StdoutChunkCallback = _ => { },
            StderrChunkCallback = _ => { },
        });

        Assert.True(result.Success);
        Assert.Equal("pipe stdout\n", result.Stdout);
        Assert.Equal("pipe stderr\n", result.Stderr);
        Assert.DoesNotContain(runner.Calls, call => call.Argv.Count > 1 && call.Argv[1] == "transfer");

        var wrapperCall = Assert.Single(runner.Calls, IsCodeyboxExecCall);
        Assert.DoesNotContain("--env-file", wrapperCall.Argv);
        Assert.True(wrapperCall.HasStdoutChunkCallback);
        Assert.True(wrapperCall.HasStderrChunkCallback);
        Assert.Equal(maxStdoutBytes, wrapperCall.MaxStdoutBytes);
        Assert.Equal(maxStderrBytes, wrapperCall.MaxStderrBytes);
    }

    [Fact]
    public async Task ExecAsync_MapsProcessExecutionUnavailable()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                1,
                "",
                "provider unavailable",
                ExecutionUnavailable: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo hi"],
        });

        Assert.False(result.Success);
        Assert.True(result.ExecutionUnavailable);
        Assert.Equal("provider unavailable", result.Stderr);
    }

    [Fact]
    public async Task DefaultProcessRunner_StopsReadingWhenStderrLimitIsExceeded()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new DefaultProcessRunner();
        // See sibling stdout test for the rationale: the cap is a hang
        // backstop, not a tight bound on limit-detection latency.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            ["sh", "-c", "while :; do printf 1234567890 >&2; done"],
            stdin: null,
            ct: timeout.Token,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);

        Assert.True(result.StderrLimitExceeded);
        Assert.True(result.Stderr.Length <= 1024, $"captured {result.Stderr.Length} bytes");
    }

    [Fact]
    public async Task SynthesizeInputAsync_ThrowsWhenXdotoolFails()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, "", "xdotool failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" }]));

        Assert.Contains("graphical input event 'Key' failed", ex.Message);
        Assert.Contains("xdotool failed", ex.Message);
    }

    [Fact]
    public async Task SynthesizeInputAsync_RejectsMalformedInputEvents()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var invalidEvents = new[]
        {
            new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10 },
            new SandboxInputEvent { Type = SandboxInputEventType.Click, X = -1, Y = 0 },
            new SandboxInputEvent { Type = SandboxInputEventType.Key },
            new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 10 },
            new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 0, Y = -1 },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 1, Y = 1 },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 1001 },
            new SandboxInputEvent { Type = SandboxInputEventType.Type },
        };

        foreach (var inputEvent in invalidEvents)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                sandbox.SynthesizeInputAsync([inputEvent]));
        }
    }

    [Fact]
    public async Task SynthesizeInputAsync_RejectsNullEventListAndUnknownEventType()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sandbox.SynthesizeInputAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            sandbox.SynthesizeInputAsync([]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            sandbox.SynthesizeInputAsync([null!]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = (SandboxInputEventType)999 }]));
    }

    [Fact]
    public async Task SynthesizeInputAsync_BuildsExpectedXdotoolCommands()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.SynthesizeInputAsync(
            [
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 12, Y = 34 },
                new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" },
                new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 45, Y = 56 },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = -2 },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 3 },
                new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "typed text" },
            ]);

        var commands = runner.Calls.Select(c => ExtractXdotoolCommand(c.Argv)).ToArray();
        Assert.Equal(
            [
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "mousemove", "--sync", "12", "34", "click", "1"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "key", "--clearmodifiers", "Return"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "mousemove", "--sync", "45", "56"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "click", "--repeat", "2", "4"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "click", "--repeat", "3", "7"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "type", "--clearmodifiers", "--delay", "0", "--", "typed text"],
            ],
            commands);
    }

    [Fact]
    public void LaunchArgv_MapsConfiguredProfileToHostBridgeAndRejectsUnknownProfiles()
    {
        var provider = NewProvider(networkProfiles: new Dictionary<string, string>
        {
            ["claude"] = "cb-claude",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            Limits = new SandboxResourceLimits
            {
                CpuCount = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,
                DiskBytes = 20L * 1024 * 1024 * 1024,
            },
        };

        var argv = provider.BuildLaunchArgv("codeybox-test", spec, "/staging/cloud-init.yaml");

        Assert.Equal("multipass", argv[0]);
        Assert.Contains("--cpus", argv);
        Assert.Contains("4", argv);
        var argvList = argv.ToList();
        var networkIndex = argvList.IndexOf("--network");
        Assert.True(networkIndex > 0, string.Join(' ', argv));
        Assert.Equal("name=cb-claude,mode=auto", argv[networkIndex + 1]);
        Assert.True(argvList.IndexOf("24.04") > networkIndex);

        var missing = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "missing" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.BuildLaunchArgv("codeybox-test", missing, "/staging/cloud-init.yaml"));
        Assert.Contains("missing", ex.Message);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void StagingRoot_IsCreatedWithOperatorOnlyPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var staging = Path.Combine(_workspace, "staging");

        _ = NewProvider(stagingDirectory: staging);

        var mode = File.GetUnixFileMode(staging);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead), mode.ToString());
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead), mode.ToString());
    }

    [Fact]
    public async Task DisposeLeakedAsync_RejectsUnsafeVmNamesBeforeShellingOut()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProvider(runner: runner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.DisposeLeakedAsync("codeybox-bad/../../../escape", CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DisposeAsync_DeleteFailureUntracksActiveSandboxWithoutMaskingCompletedPhase()
    {
        var disposedNames = new List<string>();
        var noLongerActiveNames = new List<string>();
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "delete", "--purge", "codeybox-deletefail"])
            {
                deleteCalls++;
                return Task.FromResult(deleteCalls == 1
                    ? new ProcessRunResult(17, "", "still running")
                    : new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = new MultipassSandbox(
            "codeybox-deletefail",
            Path.Combine(_workspace, "delete-fail-root"),
            new SandboxSpec { ImageReference = "ignored" },
            new MultipassSandboxOptions { MultipassBinary = "/bin/false" },
            NullLogger<MultipassSandboxProvider>.Instance,
            onDisposed: disposedNames.Add,
            onNoLongerTrackedActive: noLongerActiveNames.Add,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        Assert.Empty(disposedNames);
        Assert.Equal(["codeybox-deletefail"], noLongerActiveNames);

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        Assert.Empty(disposedNames);
    }

    [Fact]
    public async Task DisposeAsync_DeleteFailure_WhenOwnedByShutdownHandler_Throws()
    {
        var noLongerActiveNames = new List<string>();
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "delete", "--purge", "codeybox-shutdown-deletefail"])
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(17, "", "still running"));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = new MultipassSandbox(
            "codeybox-shutdown-deletefail",
            Path.Combine(_workspace, "shutdown-delete-fail-root"),
            new SandboxSpec { ImageReference = "ignored" },
            new MultipassSandboxOptions { MultipassBinary = "/bin/false" },
            NullLogger<MultipassSandboxProvider>.Instance,
            onNoLongerTrackedActive: noLongerActiveNames.Add,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        ((IShutdownTeardownSandbox)sandbox).MarkOwnedByShutdownHandler();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.DisposeAsync());

        Assert.Contains("multipass delete --purge codeybox-shutdown-deletefail failed", ex.Message);
        Assert.Equal(1, deleteCalls);
        Assert.Equal(["codeybox-shutdown-deletefail"], noLongerActiveNames);
    }

    [Fact]
    public async Task ProviderCreatedSandbox_DeleteFailureUntracksActiveCacheAndReaperDisposes()
    {
        var staging = Path.Combine(_workspace, "provider-delete-fail-staging");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteCalls = 0;
        var listCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(states.TryGetValue(name, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found"));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "list", "--format", "json"])
            {
                listCalls++;
                var list = states.Keys
                    .OrderBy(vmName => vmName, StringComparer.Ordinal)
                    .Select(vmName => new { name = vmName })
                    .ToArray();
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { list }), ""));
            }

            if (argv is [_, "info", "--format", "json", ..])
            {
                var info = states.Keys.ToDictionary(
                    vmName => vmName,
                    vmName => new
                    {
                        created = "2026-05-18T00:00:00Z",
                        disks = new Dictionary<string, object>
                        {
                            ["sda1"] = new { used = "1048576" },
                        },
                    },
                    StringComparer.Ordinal);
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteCalls++;
                if (deleteCalls == 1)
                    return Task.FromResult(new ProcessRunResult(17, "", "transient delete failure"));

                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var name = sandbox.Id;
        var active = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.True(active.IsTrackedActive);

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        var untracked = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.Equal(name, untracked.Name);
        Assert.False(untracked.IsTrackedActive);
        Assert.Equal(2, listCalls);

        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                AutoDispose = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = TimeSpan.Zero,
                MaxConcurrentAutoDispose = 1,
            },
            NullLogger<SandboxLeakReaper>.Instance);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, deleteCalls);
        Assert.Empty(states);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task CreateAsync_TracksVmAsActiveWhileLaunchIsStillInProgress()
    {
        var staging = Path.Combine(_workspace, "provider-inflight-launch-staging");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunchToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                launchEntered.TrySetResult();
                await allowLaunchToFinish.Task.WaitAsync(ct);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return states.TryGetValue(name, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found");

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "list", "--format", "json"])
            {
                var list = states.Keys
                    .OrderBy(vmName => vmName, StringComparer.Ordinal)
                    .Select(vmName => new { name = vmName })
                    .ToArray();
                return new ProcessRunResult(0, JsonSerializer.Serialize(new { list }), "");
            }

            if (argv is [_, "info", "--format", "json", ..])
            {
                var info = states.Keys.ToDictionary(
                    vmName => vmName,
                    vmName => new
                    {
                        created = "2026-05-18T00:00:00Z",
                        disks = new Dictionary<string, object>(),
                    },
                    StringComparer.Ordinal);
                return new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), "");
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var createTask = provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await launchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var inFlight = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.True(inFlight.IsTrackedActive);

        allowLaunchToFinish.SetResult();
        await using var sandbox = await createTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersCodeyboxVmsAddsDiskInfoAndUsesTtlCache()
    {
        var staging = Path.Combine(_workspace, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-alpha"));
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-beta"));
        await File.WriteAllTextAsync(Path.Combine(staging, "codeybox-beta", ".codeybox-preempt"), "preserved");

        var listCalls = 0;
        var infoCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
            {
                listCalls++;
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[
                      {"name":"primary"},
                      {"name":"cb-baseline-claude"},
                      {"name":"codeybox-alpha"},
                      {"name":"codeybox-beta"},
                      {"name":"codeybox-orphan"},
                      {"name":"codeybox-invalid.name"}
                    ]}
                    """, ""));
            }

            if (argv.Count >= 4 && argv[1] == "info")
            {
                infoCalls++;
                return Task.FromResult(new ProcessRunResult(0, """
                    {"info":{
                      "codeybox-alpha":{"disks":{"sda1":{"used":"1048576"}}},
                      "codeybox-beta":{"disks":{"sda1":{"used":"2097152"}}},
                      "codeybox-orphan":{"created":"2026-05-18T00:00:00Z","disks":{"sda1":{"used":"3145728"}}}
                    }}
                    """, ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + string.Join(' ', argv)));
        });
        var provider = NewProvider(stagingDirectory: staging, runner: runner);

        var first = await provider.ListAllManagedAsync(CancellationToken.None);
        var second = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, listCalls);
        Assert.Equal(1, infoCalls);
        Assert.Equal(["codeybox-alpha", "codeybox-beta", "codeybox-orphan"], first.Select(s => s.Name).ToArray());
        Assert.All(first, s => Assert.NotNull(s.CreatedAt));
        Assert.Equal(1024 * 1024, first.Single(s => s.Name == "codeybox-alpha").DiskBytes);
        Assert.True(first.Single(s => s.Name == "codeybox-beta").HasPreemptMarker);
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-18T00:00:00Z"),
            first.Single(s => s.Name == "codeybox-orphan").CreatedAt);
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("creation_time")]
    [InlineData("creationTimestamp")]
    public async Task ListAllManagedAsync_ReadsMultipassCreationTimestampAliases(string timestampProperty)
    {
        var expected = DateTimeOffset.Parse("2026-05-19T01:02:03Z");
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[{"name":"codeybox-alias"}]}
                    """, ""));

            if (argv.Count >= 4 && argv[1] == "info")
            {
                var info = new Dictionary<string, object>
                {
                    ["codeybox-alias"] = new Dictionary<string, object?>
                    {
                        [timestampProperty] = "2026-05-19T01:02:03Z",
                        ["disks"] = new Dictionary<string, object>(),
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-" + timestampProperty),
            runner: runner);

        var managed = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(expected, managed.CreatedAt);
    }

    [Theory]
    [InlineData("Suspending", true)]
    [InlineData("Suspended", true)]
    [InlineData("Running", false)]
    [InlineData("Stopped", false)]
    public async Task ListAllManagedAsync_MapsMultipassStateToSuspendLifecycleFlag(
        string multipassState, bool expectedFlag)
    {
        // SandboxLeakReaper reads ManagedSandboxInfo.IsSuspendLifecycleOrFrozen to
        // spot VMs frozen/freezing with no live mapping. The multipass state
        // vocabulary stays inside the provider, which must map BOTH Suspending
        // (snapshot in progress) and Suspended (complete) to true, and running /
        // stopped states to false. This pins the JSON-parse → flag wiring so a
        // regression there can't silently blind the reaper.
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[{"name":"codeybox-stateful"}]}
                    """, ""));

            if (argv.Count >= 4 && argv[1] == "info")
            {
                var info = new Dictionary<string, object>
                {
                    ["codeybox-stateful"] = new Dictionary<string, object?>
                    {
                        ["state"] = multipassState,
                        ["disks"] = new Dictionary<string, object>(),
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-state-" + multipassState),
            runner: runner);

        var managed = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(expectedFlag, managed.IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public async Task BaselineImages_BakeOncePerProfileUnderConcurrentCreatesThenCloneSandboxes()
    {
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var baselineLaunches = 0;
        var cloneCount = 0;
        var installCount = 0;

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return new ProcessRunResult(0, state, "");
                return new ProcessRunResult(1, "", "not found");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var launchName = argv[3];
                if (launchName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref baselineLaunches);
                    states[launchName] = "Running";
                    launchEntered.TrySetResult();
                    await allowLaunch.Task.WaitAsync(ct);
                }
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", "");

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref installCount);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                states[cloneName] = "Stopped";
                Interlocked.Increment(ref cloneCount);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            extraRuncmd: ["touch /opt/codeybox-baseline"],
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        };

        var firstCreate = provider.CreateAsync(spec, CancellationToken.None);
        await launchEntered.Task;
        var secondCreate = provider.CreateAsync(spec, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref baselineLaunches));

        allowLaunch.SetResult();
        var sandboxes = await Task.WhenAll(firstCreate, secondCreate);
        await sandboxes[0].DisposeAsync();
        await sandboxes[1].DisposeAsync();

        Assert.Equal(1, baselineLaunches);
        Assert.Equal(1, installCount);
        Assert.Equal(2, cloneCount);
    }

    [Fact]
    public async Task BaselineImages_CloneAlreadyExistsStoppedTarget_TreatsAsSuccess()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloneCalls = 0;
        string? cloneName = null;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (infoName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessRunResult(0, "Stopped", ""));
                return Task.FromResult(states.TryGetValue(infoName, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found"));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var target])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                cloneName = target;
                cloneCalls++;
                states[target] = "Stopped";
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    $"multipass clone failed: instance \"{target}\" already exists"));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-clone-already-exists"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(1, cloneCalls);
        Assert.Equal(cloneName, sandbox.Id);
        Assert.NotNull(cloneName);
        Assert.Equal("Running", states[cloneName!]);
    }

    [Fact]
    public async Task BaselineImages_CloneAlreadyExistsRunningTarget_PurgesBeforeRetry()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloneCalls = 0;
        string? cloneName = null;
        var purged = new List<string>();

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (infoName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessRunResult(0, "Stopped", ""));
                return Task.FromResult(states.TryGetValue(infoName, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found"));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var target])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                cloneName = target;
                cloneCalls++;
                if (cloneCalls == 1)
                {
                    states[target] = "Running";
                    return Task.FromResult(new ProcessRunResult(
                        1,
                        "",
                        $"multipass clone failed: instance \"{target}\" already exists"));
                }

                states[target] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                purged.Add(deleteName);
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-clone-stale-running"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(2, cloneCalls);
        Assert.Equal(cloneName, sandbox.Id);
        var purgedName = Assert.Single(purged);
        Assert.Equal(cloneName, purgedName);
        Assert.NotNull(cloneName);
        Assert.Equal("Running", states[cloneName!]);
    }

    [Fact]
    public async Task BaselineImages_CloneAlreadyExistsOnRetryStoppedTarget_TreatsAsSuccess()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloneCalls = 0;
        var targetInfoCalls = 0;
        var started = false;
        string? cloneName = null;
        var purged = new List<string>();

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (infoName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessRunResult(0, "Stopped", ""));

                if (!started)
                    targetInfoCalls++;
                return Task.FromResult(states.TryGetValue(infoName, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found"));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var target])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                cloneName = target;
                cloneCalls++;
                states[target] = cloneCalls == 1 ? "Running" : "Stopped";
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    $"multipass clone failed: instance \"{target}\" already exists"));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                purged.Add(deleteName);
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                started = true;
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-clone-retry-collision-stopped"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        });

        Assert.Equal(2, cloneCalls);
        Assert.Equal(2, targetInfoCalls);
        var purgedName = Assert.Single(purged);
        Assert.Equal(cloneName, purgedName);
        Assert.Equal(cloneName, sandbox.Id);
        Assert.NotNull(cloneName);
        Assert.Equal("Running", states[cloneName!]);
    }

    [Fact]
    public async Task BaselineImages_CloneAlreadyExistsStillColliding_DefersInsteadOfHardFail()
    {
        var cloneCalls = 0;
        var targetInfoCalls = 0;
        string? cloneName = null;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (infoName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                    return Task.FromResult(new ProcessRunResult(0, "Stopped", ""));

                targetInfoCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv is [_, "stop", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "clone", var source, "--name", var target])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                cloneName = target;
                cloneCalls++;
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    $"multipass clone failed: instance \"{target}\" already exists"));
            }

            if (argv is [_, "delete", "--purge", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-clone-still-colliding"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                Network = new SandboxNetworkPolicy { ProfileName = "claude" },
                WorkingDirectory = "/work",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("clone", ex.Operation);
        Assert.Equal("multipass-clone-target-already-exists", ex.ErrorClass);
        Assert.Contains(cloneName!, ex.Detail);
        Assert.Equal(2, cloneCalls);
        Assert.Equal(2, targetInfoCalls);
    }

    [Fact]
    public async Task BaselineImages_CloneRetryExhaustion_DefersWithCloneOperation()
    {
        var cloneCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv is [_, "info", var infoName, "--format=csv"])
                return Task.FromResult(infoName.StartsWith("cb-baseline-", StringComparison.Ordinal)
                    ? new ProcessRunResult(0, "Stopped", "")
                    : new ProcessRunResult(1, "", "not found"));

            if (argv is [_, "stop", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "clone", var source, "--name", _])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                cloneCalls++;
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    "clone failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'"));
            }

            if (argv is [_, "delete", "--purge", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-clone-retry-exhausted"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                Network = new SandboxNetworkPolicy { ProfileName = "claude" },
                WorkingDirectory = "/work",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("clone", ex.Operation);
        Assert.Equal("multipass-instance-lock-contention", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Equal(3, cloneCalls);
    }

    [Fact]
    public async Task BaselineImages_BaselineLaunchRetryExhaustion_DefersWithBaselineLaunchOperation()
    {
        var launchCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv is [_, "info", _, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));

            if (argv.Count >= 2 && argv[1] == "launch")
            {
                launchCalls++;
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    "launch failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'"));
            }

            if (argv is [_, "delete", "--purge", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-baseline-launch-exhausted"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                Network = new SandboxNetworkPolicy { ProfileName = "claude" },
                WorkingDirectory = "/work",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("baseline-launch", ex.Operation);
        Assert.Equal("multipass-instance-lock-contention", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Equal(3, launchCalls);
    }

    [Fact]
    public async Task BaselineImages_GraphicalFlavorUsesSharedGraphicalBaselineAndInstallsDesktop()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var installCommands = new List<string>();
        string? baselineLaunchName = null;
        string? baselineLaunchNetwork = null;
        string? baselineCloudInit = null;
        string? cloneSource = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, state, ""));
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                var cloudInitIndex = argv.ToList().IndexOf("--cloud-init");
                baselineCloudInit = cloudInitIndex >= 0 ? File.ReadAllText(argv[cloudInitIndex + 1]) : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", var command]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                installCommands.Add(command);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-graphical"),
            networkProfiles: new Dictionary<string, string>
            {
                ["claude"] = "cb-claude",
                [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
            },
            useBaselineImages: true,
            extraRuncmd: ["touch /opt/codeybox-project-tools"],
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = SandboxConventions.GraphicalNetworkProfile },
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // B1: baseline name is now content-hashed (cb-baseline-{12hex}); the
        // exact value depends on cloud-init + runcmd contents. Assert the
        // structure and that the same value is reused for the subsequent
        // clone — re-baking would have produced a different launch name.
        Assert.NotNull(baselineLaunchName);
        Assert.StartsWith("cb-baseline-", baselineLaunchName);
        Assert.Equal("name=cb-graphical,mode=auto", baselineLaunchNetwork);
        Assert.Equal(baselineLaunchName, cloneSource);
        var baselineCloudInitText = Assert.IsType<string>(baselineCloudInit);
        Assert.Contains("systemctl enable --now codeybox-route.service", baselineCloudInitText);
        Assert.DoesNotContain("apt-get install -y --no-install-recommends xvfb", baselineCloudInitText);
        Assert.Contains(installCommands, cmd =>
            cmd.Contains("xvfb x11vnc xfce4", StringComparison.Ordinal)
            && cmd.Contains("xdotool scrot ffmpeg", StringComparison.Ordinal));
        Assert.Contains(installCommands, cmd =>
            cmd.Contains("touch /opt/codeybox-project-tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaselineImages_GraphicalFlavorPreservesSelectedNetworkProfile()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? baselineLaunchName = null;
        string? baselineLaunchNetwork = null;
        string? cloneSource = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, state, ""));
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", _]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-graphical-profile"),
            networkProfiles: new Dictionary<string, string>
            {
                ["ci"] = "cb-ci",
                [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
            },
            useBaselineImages: true,
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = "ci" },
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // B1: see other graphical-baseline test — name is content-hashed now.
        Assert.NotNull(baselineLaunchName);
        Assert.StartsWith("cb-baseline-", baselineLaunchName);
        Assert.Equal("name=cb-ci,mode=auto", baselineLaunchNetwork);
        Assert.Equal(baselineLaunchName, cloneSource);
    }

    [Fact]
    public async Task CreateAsync_RetriesCloudInitExitOneAndAcceptsRecoveredStatus()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-exit-one-retry");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloudInitCalls = 0;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(cloudInitCalls == 1
                    ? new ProcessRunResult(1, "", "")
                    : new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", _])
            {
                probeCalls++;
                return Task.FromResult(new ProcessRunResult(99, "", "readiness probe should not run after recovered cloud-init status"));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(3, cloudInitCalls);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task CreateAsync_CloudInitStatusWaitTimeoutDefersProvisioning()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-wait-timeout");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;
        var observedCloudInitCancellation = false;

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    observedCloudInitCancellation = true;
                    throw;
                }

                return new ProcessRunResult(0, "unreachable: cloud-init wait should be cancelled", "");
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            vmStartTimeout: TimeSpan.FromMilliseconds(50));
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("cloud-init", ex.Operation);
        Assert.Equal("multipass-cloud-init-timeout", ex.ErrorClass);
        Assert.Contains("cloud-init status --wait", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did not complete within", ex.Message, StringComparison.Ordinal);
        Assert.True(observedCloudInitCancellation);
        Assert.Equal(launchedName, deletedName);
        Assert.False(
            Directory.Exists(Path.Combine(staging, launchedName!)),
            "staging directory for failed sandbox must be removed during cleanup");
    }

    [Fact]
    public async Task CreateAsync_CloudInitExitOneAfterRetriesUsesReadinessProbe()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-probe-success");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloudInitCalls = 0;
        var probeCalls = 0;
        var logger = new RecordingLogger<MultipassSandboxProvider>();

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    0,
                    "/work=present /usr/local/bin/codeybox-exec=present\n",
                    ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            logger: logger,
            cloudInitReadyRetryAttempts: 2,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(4, cloudInitCalls);
        Assert.Equal(2, probeCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("readiness probe passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_CloudInitDegradedUsesReadinessProbeWhenProbePasses()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-degraded-probe-success");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloudInitCalls = 0;
        var probeCalls = 0;
        var logger = new RecordingLogger<MultipassSandboxProvider>();

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(new ProcessRunResult(2, "", "status: degraded"));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    0,
                    "/work=present /usr/local/bin/codeybox-exec=present\n",
                    ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            logger: logger);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(2, cloudInitCalls);
        Assert.Equal(2, probeCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("cloud-init status returned degraded", StringComparison.Ordinal)
            && e.Message.Contains("readiness probe passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_CloudInitExitOneAfterRetriesThrowsWhenReadinessProbeFails()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-probe-failure");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;
        var cloudInitCalls = 0;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "/work=missing /usr/local/bin/codeybox-exec=missing\n",
                    ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("cloud-init", ex.Operation);
        Assert.Equal("multipass-cloud-init-not-ready", ex.ErrorClass);
        Assert.Equal(MultipassSandboxOptions.DefaultCloudInitReadyRetryAttempts, cloudInitCalls);
        Assert.Equal(1, probeCalls);
        Assert.Contains("readiness probe failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/work=missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/codeybox-exec=missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Expected /work and /usr/local/bin/codeybox-exec to exist", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(launchedName);
        Assert.Equal(launchedName, deletedName);
        Assert.False(
            Directory.Exists(Path.Combine(staging, launchedName!)),
            "staging directory for failed sandbox must be removed during cleanup");
    }

    [Fact]
    public async Task CreateAsync_ThrowsAndCleansUpWhenCloudInitWaitReturnsNonZero()
    {
        // WaitForVmReadyAsync surfaces cloud-init failures so a half-installed
        // graphical (or headless) VM doesn't return as a "ready" sandbox handle.
        // The catch in CreateAsync must also tear down the VM and staging dir
        // so a failed launch doesn't leak.
        var staging = Path.Combine(_workspace, "staging-cloud-init-failure");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? cloudInitTarget = null;
        string? deletedName = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitTarget = execName;
                return Task.FromResult(new ProcessRunResult(3, "", "schema validation failed: bad runcmd"));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--long"])
                return Task.FromResult(new ProcessRunResult(0, "status: error\nschema validation failed: bad runcmd", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(stagingDirectory: staging, runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("cloud-init", ex.Operation);
        Assert.Equal("multipass-cloud-init-failed", ex.ErrorClass);
        Assert.Contains("cloud-init failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("schema validation failed: bad runcmd", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(launchedName);
        Assert.Contains(launchedName!, ex.Message, StringComparison.Ordinal);
        Assert.Equal(launchedName, cloudInitTarget);
        Assert.Equal(launchedName, deletedName);
        Assert.False(
            Directory.Exists(Path.Combine(staging, launchedName!)),
            "staging directory for failed sandbox must be removed during cleanup");
    }

    [Fact]
    public async Task CreateAsync_ThrowsAndCleansUpWhenCloudInitReportsDegraded()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-degraded");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(2, "", "status: degraded"));

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "/work=present /usr/local/bin/codeybox-exec=missing\n",
                    ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--long"])
                return Task.FromResult(new ProcessRunResult(0, "status: degraded\ncloud-config failed schema validation", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(stagingDirectory: staging, runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("cloud-init", ex.Operation);
        Assert.Equal("multipass-cloud-init-degraded", ex.ErrorClass);
        Assert.Contains("cloud-init degraded", ex.Message, StringComparison.Ordinal);
        Assert.Contains("readiness probe failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/codeybox-exec=missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cloud-config failed schema validation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, probeCalls);
        Assert.Equal(launchedName, deletedName);
    }

    [Fact]
    public async Task CreateAsync_DegradedDiagnostics_FallBackWhenLongStatusItselfFails()
    {
        // The fallback path inside ReadCloudInitLongStatusAsync — `cloud-init
        // status --long` failing with nonzero — is the worst-case diagnostic
        // shape: cloud-init couldn't even tell us what went wrong. The thrown
        // message must still embed exit code / stdout / stderr from the failed
        // diagnostic, instead of swallowing them and surfacing a bare
        // "cloud-init degraded". The original failure (exit 2 "status:
        // degraded") must also stay in the chain so an operator can tell that
        // the long-status fallback is what was masking the real cause.
        var staging = Path.Combine(_workspace, "staging-cloud-init-degraded-long-fail");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(2, "", "status: degraded"));

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "/work=missing /usr/local/bin/codeybox-exec=missing\n",
                    "marker probe failed"));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--long"])
                return Task.FromResult(new ProcessRunResult(3, "partial status output", "permission denied"));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(stagingDirectory: staging, runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("cloud-init", ex.Operation);
        Assert.Equal("multipass-cloud-init-degraded", ex.ErrorClass);
        // The degraded-state framing must survive.
        Assert.Contains("cloud-init degraded", ex.Message, StringComparison.Ordinal);
        Assert.Contains("readiness probe failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("status: degraded", ex.Message, StringComparison.Ordinal);
        // The long-status failure framing AND its exit code / streams must be
        // surfaced — otherwise an operator looking at a bake failure sees only
        // "degraded" and never learns that the diagnostic itself broke.
        Assert.Contains("cloud-init status --long failed (exit 3)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("partial status output", ex.Message, StringComparison.Ordinal);
        Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, probeCalls);
        Assert.Equal(launchedName, deletedName);
    }

    [Fact]
    public void LaunchArgv_GraphicalFlavorUsesConfiguredProfileBridge()
    {
        var provider = NewProvider(networkProfiles: new Dictionary<string, string>
        {
            ["claude"] = "cb-claude",
            [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
        };

        var argv = provider.BuildLaunchArgv("codeybox-test", spec, "/staging/cloud-init.yaml");
        var networkIndex = argv.ToList().IndexOf("--network");

        Assert.True(networkIndex > 0, string.Join(' ', argv));
        Assert.Equal("name=cb-claude,mode=auto", argv[networkIndex + 1]);
    }

    [Fact]
    public void MultipassSandboxOptions_VmTimeouts_DefaultToSpecValues()
    {
        // Defaults match the values previously hardcoded in WaitForRunningAsync
        // and WaitForStoppedAsync, so behaviour is unchanged when operators do
        // not override either knob.
        var options = new MultipassSandboxOptions();

        Assert.Equal(TimeSpan.FromMinutes(3), options.VmStartTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), options.VmStopTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), MultipassSandboxOptions.DefaultVmStartTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), MultipassSandboxOptions.DefaultVmStopTimeout);
    }

    [Fact]
    public async Task CreateAsync_VmStartTimeout_AppliesConfiguredDeadlineWhenInfoNeverReportsRunning()
    {
        var staging = Path.Combine(_workspace, "staging-vm-start-timeout");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                // Stay in "Starting" forever so the WaitForRunning loop exhausts
                // the configured deadline rather than ever observing Running.
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Starting";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var configuredTimeout = TimeSpan.FromMilliseconds(250);
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            vmStartTimeout: configuredTimeout);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Contains("did not reach Running state", ex.Message, StringComparison.Ordinal);
        Assert.Contains(configuredTimeout.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal("vm-start", ex.Operation);
        Assert.Equal("multipass-vm-start-timeout", ex.ErrorClass);
        Assert.NotNull(launchedName);
        Assert.Equal(launchedName, deletedName);
    }

    [Fact]
    public async Task RetryHelper_RetriesTransientSshReadinessWithoutRealDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1
                ? new ProcessRunResult(1, "", "ssh connection failed: Connection refused")
                : new ProcessRunResult(0, "ok", "")),
            log: NullLogger.Instance,
            description: "uat transfer",
            ct: CancellationToken.None,
            delay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
        Assert.Equal(2, attempts);
        Assert.Equal([MultipassRetry.DefaultInitialDelay], delays);
    }

    private static MultipassSandboxProvider NewProvider(
        string? stagingDirectory = null,
        IReadOnlyDictionary<string, string>? networkProfiles = null,
        bool useBaselineImages = false,
        IReadOnlyList<string>? extraRuncmd = null,
        RecordingMultipassRunner? runner = null,
        RecordingLogger<MultipassSandboxProvider>? logger = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        int? cloudInitReadyRetryAttempts = null,
        TimeSpan? cloudInitReadyRetryDelay = null,
        TimeSpan? vmStartTimeout = null,
        TimeSpan? vmStopTimeout = null,
        int? maxConcurrentBoots = null,
        TimeSpan? bootLaunchDelay = null)
    {
        var options = new MultipassSandboxOptions
        {
            MultipassBinary = runner is null ? "multipass" : "/bin/false",
            StagingDirectory = stagingDirectory,
            NetworkProfiles = networkProfiles ?? new Dictionary<string, string>(),
            UseBaselineImages = useBaselineImages,
            ExtraRuncmd = extraRuncmd ?? [],
            CloudInitReadyRetryAttempts = cloudInitReadyRetryAttempts
                ?? MultipassSandboxOptions.DefaultCloudInitReadyRetryAttempts,
            CloudInitReadyRetryDelay = cloudInitReadyRetryDelay
                ?? MultipassSandboxOptions.DefaultCloudInitReadyRetryDelay,
            VmStartTimeout = vmStartTimeout
                ?? MultipassSandboxOptions.DefaultVmStartTimeout,
            VmStopTimeout = vmStopTimeout
                ?? MultipassSandboxOptions.DefaultVmStopTimeout,
            MaxConcurrentBoots = maxConcurrentBoots
                ?? MultipassSandboxOptions.DefaultMaxConcurrentBoots,
            BootLaunchDelay = bootLaunchDelay
                ?? MultipassSandboxOptions.DefaultBootLaunchDelay,
        };
        Microsoft.Extensions.Logging.ILogger<MultipassSandboxProvider> resolvedLogger = logger is not null
            ? logger
            : NullLogger<MultipassSandboxProvider>.Instance;

        return runner is null
            ? new MultipassSandboxProvider(options, resolvedLogger)
            : new MultipassSandboxProvider(
                options,
                resolvedLogger,
                null,
                runner,
                daemonRetryPolicy);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler,
        int? maxScreenshotPngBytes = null,
        TimeSpan? vmStopTimeout = null)
    {
        return NewMultipassSandbox(flavor, new RecordingMultipassRunner(handler), maxScreenshotPngBytes, vmStopTimeout);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        RecordingMultipassRunner runner,
        int? maxScreenshotPngBytes = null,
        TimeSpan? vmStopTimeout = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null)
    {
        return new MultipassSandbox(
            "codeybox-test",
            _workspace,
            new SandboxSpec
            {
                ImageReference = "ignored",
                Flavor = flavor,
                WorkingDirectory = "/work",
            },
            new MultipassSandboxOptions
            {
                MultipassBinary = "/bin/true",
                VmStopTimeout = vmStopTimeout ?? MultipassSandboxOptions.DefaultVmStopTimeout,
            },
            NullLogger<MultipassSandboxProvider>.Instance,
            runner: runner,
            daemonRetryPolicy: daemonRetryPolicy,
            maxScreenshotPngBytes: maxScreenshotPngBytes);
    }

    private static string[] ExtractXdotoolCommand(IReadOnlyList<string> argv)
    {
        var envIndex = argv.ToList().IndexOf("env");
        Assert.True(envIndex >= 0, "missing xdotool env command in argv: " + JsonSerializer.Serialize(argv));
        return argv.Skip(envIndex).ToArray();
    }

    private static string ExtractShellEnvValue(string envFileContent, string key)
    {
        var prefix = key + "=";
        var line = envFileContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var value = line[prefix.Length..];
        Assert.StartsWith("'", value, StringComparison.Ordinal);
        Assert.EndsWith("'", value, StringComparison.Ordinal);
        return value[1..^1].Replace("'\"'\"'", "'", StringComparison.Ordinal);
    }

    private static string RewriteShellEnvValue(string envFileContent, string key, string value)
    {
        var prefix = key + "=";
        var replacement = prefix + MultipassSandboxProvider.ShellSingleQuote(value);
        var lines = envFileContent.Split('\n');
        var replaced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.Ordinal))
                continue;

            lines[i] = replacement;
            replaced = true;
        }

        Assert.True(replaced, $"Expected env file to contain {key}");
        return string.Join('\n', lines);
    }

    private static async Task PostAgentOutputAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string stream,
        long seq,
        string token,
        string body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/{Uri.EscapeDataString(runId)}/{Uri.EscapeDataString(stream)}/{seq}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string BuildPollOutput(int pgid, int? exitCode) =>
        exitCode is { } code
            ? $"exited {pgid} {code}\n"
            : $"exited {pgid}\n";

    private static async Task<(int Exit, string Stdout, string Stderr)> RunLocalProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct = default,
        IReadOnlyList<string>? environmentKeysToRemove = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (environmentKeysToRemove is not null)
        {
            foreach (var key in environmentKeysToRemove)
                psi.Environment.Remove(key);
        }
        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            throw;
        }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;
            await Task.Delay(50);
        }

        Assert.True(File.Exists(path), $"Expected file to be written: {path}");
    }

    private static async Task WaitForExitCodeAsync(
        MultipassAgentOutputHttpIngestSession session,
        int expectedExitCode,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.TryGetExitCode(out var exitCode) && exitCode == expectedExitCode)
                return;
            await Task.Delay(50);
        }

        Assert.True(
            session.TryGetExitCode(out var finalExitCode) && finalExitCode == expectedExitCode,
            $"Expected authenticated exit code {expectedExitCode}.");
    }

    private static async Task WaitForProcessGroupGoneAsync(string markerPath, TimeSpan timeout)
    {
        await WaitForFileAsync(markerPath, timeout);
        var pgid = (await File.ReadAllTextAsync(markerPath)).Trim();
        await WaitForProcessGroupIdGoneAsync(pgid, timeout);
    }

    private static async Task WaitForProcessGroupIdGoneAsync(string pgid, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var (exit, _, _) = await RunLocalProcessAsync(
                "/bin/sh",
                ["-c", "kill -0 \"-$1\" 2>/dev/null", "codeybox-pgid-gone", pgid]);
            if (exit != 0)
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"Expected process group {pgid} to exit.");
    }

    private static async Task KillProcessGroupAsync(string pgid)
    {
        if (string.IsNullOrWhiteSpace(pgid))
            return;

        await RunLocalProcessAsync(
            "/bin/sh",
            ["-c", "kill -KILL \"-$1\" 2>/dev/null || true", "codeybox-pgid-cleanup", pgid]);
    }

    private string CreateFakeSudoBin()
        => CreateFakeSudoBin(
            """
            #!/bin/sh
            if [ "$1" = "-n" ]; then shift; fi
            exec "$@"
            """);

    private string CreateFakeSudoBin(string script)
    {
        var dir = Path.Combine(_workspace, "fake-sudo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sudo = Path.Combine(dir, "sudo");
        File.WriteAllText(sudo, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    private IReadOnlyDictionary<string, string> FakeSudoPathEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = CreateFakeSudoBin() + Path.PathSeparator + path,
        };
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var (key, value) in second)
            merged[key] = value;
        return merged;
    }

    private MultipassSandbox NewLoopbackHttpIngestSandbox(
        RecordingMultipassRunner runner,
        MultipassSandbox.AgentOutputHttpIngestSessionStarter? agentOutputIngestSessionStarter = null)
    {
        var sandbox = new MultipassSandbox(
            "codeybox-test",
            _workspace,
            new SandboxSpec
            {
                ImageReference = "ignored",
                Flavor = SandboxProfileFlavor.Headless,
                WorkingDirectory = "/work",
                Network = new SandboxNetworkPolicy { ProfileName = "loopback" },
            },
            new MultipassSandboxOptions
            {
                MultipassBinary = "/bin/true",
                NetworkProfiles = new Dictionary<string, string> { ["loopback"] = "lo" },
            },
            NullLogger<MultipassSandboxProvider>.Instance,
            runner: runner,
            agentOutputIngestSessionStarter: agentOutputIngestSessionStarter);
        // Keep the detached-completion poll loop off the wall clock so its
        // 2s production cadence does not race a short test deadline under
        // full-suite thread-pool contention.
        sandbox.DetachedPollIntervalOverride = TimeSpan.FromMilliseconds(20);
        return sandbox;
    }

    private sealed class CapturingDetachedExitPoster
    {
        private MultipassAgentOutputHttpIngestSession? _session;

        public async Task<MultipassAgentOutputHttpIngestSession?> StartAsync(
            System.Net.IPAddress bindAddress,
            string runId,
            ILogger log,
            Action<string>? stdoutChunkCallback,
            Action<string>? stderrChunkCallback,
            CancellationToken ct)
        {
            _session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
                bindAddress,
                runId,
                log,
                stdoutChunkCallback,
                stderrChunkCallback,
                ct);
            return _session;
        }

        public async Task PostExitAsync(int exitCode)
        {
            var session = _session ?? throw new InvalidOperationException("HTTP ingest session has not started.");
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{session.BaseUrl}/{Uri.EscapeDataString(session.RunId)}/exit/0");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.ExitToken}");
            request.Content = new StringContent(exitCode.ToString(CultureInfo.InvariantCulture) + "\n");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
    }

    private sealed class LocalDetachedVm
    {
        private const int DetachedProcessGroupMalformedExitCode = 73;
        private const string VmExecDir = "/home/ubuntu/.codeybox-exec/";
        private const string VmExecEnvDir = "/home/ubuntu/.codeybox-exec-env/";
        private const string VmExecStdinDir = "/home/ubuntu/.codeybox-exec-stdin/";
        private const string VmSupervisorDir = "/run/codeybox-exec/";

        private readonly string _root;
        private readonly string _workDir;
        private readonly string _execDir;
        private readonly string _execEnvDir;
        private readonly string _execStdinDir;
        private readonly string _supervisorDir;
        private readonly string _fakeSudoBinDir;

        public LocalDetachedVm(string workDir)
        {
            _workDir = workDir;
            _root = Path.Combine(workDir, "local-detached-vm");
            _execDir = Path.Combine(_root, "home", "ubuntu", ".codeybox-exec");
            _execEnvDir = Path.Combine(_root, "home", "ubuntu", ".codeybox-exec-env");
            _execStdinDir = Path.Combine(_root, "home", "ubuntu", ".codeybox-exec-stdin");
            _supervisorDir = Path.Combine(_root, "run", "codeybox-exec");
            _fakeSudoBinDir = Path.Combine(_root, "fake-bin");
            Directory.CreateDirectory(_execDir);
            Directory.CreateDirectory(_execEnvDir);
            Directory.CreateDirectory(_execStdinDir);
            Directory.CreateDirectory(_supervisorDir);
            Directory.CreateDirectory(_fakeSudoBinDir);
            var fakeSudo = Path.Combine(_fakeSudoBinDir, "sudo");
            File.WriteAllText(
                fakeSudo,
                """
                #!/bin/sh
                if [ "$1" = "-n" ]; then shift; fi
                exec "$@"
                """);
            ExecWrapperPath = Path.Combine(_root, "usr", "local", "bin", "codeybox-exec");
            Directory.CreateDirectory(Path.GetDirectoryName(ExecWrapperPath)!);
            File.WriteAllText(ExecWrapperPath, MultipassSandboxProvider.ExecWrapperScript);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fakeSudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    ExecWrapperPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private string ExecWrapperPath { get; }

        public async Task TransferAsync(string hostPath, string destination, CancellationToken ct)
        {
            var vmPath = TransferDestinationToVmPath(destination);
            var localPath = MapVmPath(vmPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var content = await File.ReadAllTextAsync(hostPath, ct);
            await File.WriteAllTextAsync(localPath, LocalizeScriptContent(content), ct);
            if (!OperatingSystem.IsWindows() && content.StartsWith("#!", StringComparison.Ordinal))
            {
                File.SetUnixFileMode(
                    localPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public async Task<ProcessRunResult> RunLaunchScriptAsync(string vmLaunchScript, string? stdin, CancellationToken ct)
        {
            var launchScript = MapVmPath(vmLaunchScript);
            var (exit, stdout, stderr) = await RunLocalProcessAsync(
                "/bin/bash",
                [launchScript],
                ct,
                [
                    "CODEYBOX_AGENT_LOG_FILE",
                    "CODEYBOX_AGENT_OUTPUT_RUN_ID",
                    "CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN",
                    "CODEYBOX_AGENT_OUTPUT_TOKEN",
                    "CODEYBOX_AGENT_OUTPUT_URL",
                    "CODEYBOX_AGENT_RUN_ID",
                ],
                environmentOverrides: FakeSudoPathEnvironment(),
                stdin: stdin);
            return new ProcessRunResult(exit, stdout, stderr);
        }

        public async Task<ProcessRunResult> PollProcessGroupAsync(string vmProcessGroupMarker, CancellationToken ct)
        {
            var marker = MapVmPath(vmProcessGroupMarker);
            if (!File.Exists(marker))
                return new ProcessRunResult(0, "missing\n", "");

            var text = (await File.ReadAllTextAsync(marker, ct)).Trim();
            if (!int.TryParse(text, out var pgid) || pgid <= 0)
            {
                return new ProcessRunResult(
                    DetachedProcessGroupMalformedExitCode,
                    "",
                    $"detached exec process group marker {vmProcessGroupMarker} was malformed: {text}\n");
            }

            var (exit, _, _) = await RunLocalProcessAsync(
                "/bin/sh",
                ["-c", "kill -0 \"-$1\" 2>/dev/null", "codeybox-local-poll", text],
                ct);
            if (exit == 0)
                return new ProcessRunResult(0, $"alive {pgid}\n", "");

            // Process group is gone. Mirror the real poll script: if the
            // diagnostic exit-code sidecar exists, include it in the poll
            // shape. The host still requires authenticated HTTP completion.
            var exitFile = MapVmPath(vmProcessGroupMarker + ".exit");
            if (File.Exists(exitFile))
            {
                var exitCodeText = (await File.ReadAllTextAsync(exitFile, ct)).Trim();
                if (int.TryParse(exitCodeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    return new ProcessRunResult(0, $"exited {pgid} {exitCodeText} gone\n", "");
            }

            return new ProcessRunResult(0, $"exited {pgid}\n", "");
        }

        public async Task<ProcessRunResult> ReadOutputSidecarAsync(string vmPath, CancellationToken ct)
        {
            var path = MapVmPath(vmPath);
            if (!File.Exists(path))
                return new ProcessRunResult(0, "", "");

            return new ProcessRunResult(0, await File.ReadAllTextAsync(path, ct), "");
        }

        public void RemoveVmPaths(IEnumerable<string> vmPaths)
        {
            foreach (var vmPath in vmPaths)
            {
                try
                {
                    File.Delete(MapVmPath(vmPath));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private string LocalizeScriptContent(string content)
            => content
                .Replace("/usr/local/bin/codeybox-exec", ExecWrapperPath, StringComparison.Ordinal)
                .Replace(VmExecEnvDir, WithTrailingSeparator(_execEnvDir), StringComparison.Ordinal)
                .Replace(VmExecStdinDir, WithTrailingSeparator(_execStdinDir), StringComparison.Ordinal)
                .Replace(VmExecDir, WithTrailingSeparator(_execDir), StringComparison.Ordinal)
                .Replace(VmSupervisorDir, WithTrailingSeparator(_supervisorDir), StringComparison.Ordinal)
                .Replace("/work", _workDir, StringComparison.Ordinal);

        private string MapVmPath(string vmPath)
        {
            if (vmPath.StartsWith(VmExecDir, StringComparison.Ordinal))
                return Path.Combine(_execDir, vmPath[VmExecDir.Length..]);
            if (vmPath.StartsWith(VmExecEnvDir, StringComparison.Ordinal))
                return Path.Combine(_execEnvDir, vmPath[VmExecEnvDir.Length..]);
            if (vmPath.StartsWith(VmExecStdinDir, StringComparison.Ordinal))
                return Path.Combine(_execStdinDir, vmPath[VmExecStdinDir.Length..]);
            if (vmPath.StartsWith(VmSupervisorDir, StringComparison.Ordinal))
                return Path.Combine(_supervisorDir, vmPath[VmSupervisorDir.Length..]);
            if (string.Equals(vmPath, "/work", StringComparison.Ordinal))
                return _workDir;
            if (vmPath.StartsWith("/work/", StringComparison.Ordinal))
                return Path.Combine(_workDir, vmPath["/work/".Length..]);

            throw new InvalidOperationException($"Unexpected VM path: {vmPath}");
        }

        private static string TransferDestinationToVmPath(string destination)
        {
            var colon = destination.IndexOf(':', StringComparison.Ordinal);
            var path = colon >= 0 ? destination[(colon + 1)..] : destination;
            if (path.StartsWith("/", StringComparison.Ordinal))
                return path;
            return "/home/ubuntu/" + path;
        }

        private static string WithTrailingSeparator(string path)
            => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private IReadOnlyDictionary<string, string> FakeSudoPathEnvironment()
            => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = _fakeSudoBinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            };
    }

    private static void AssertDetachedLaunchStdin(string? stdin, string expectedAgentStdin = "")
    {
        Assert.NotNull(stdin);
        var separator = stdin!.IndexOf('\n', StringComparison.Ordinal);
        Assert.True(separator > 0, "detached launch stdin must start with an exit-token line");
        var token = stdin[..separator];
        Assert.Equal(64, token.Length);
        Assert.All(token, ch => Assert.True(Uri.IsHexDigit(ch), $"non-hex detached exit token character: {ch}"));
        Assert.Equal(expectedAgentStdin, stdin[(separator + 1)..]);
    }

    private static bool IsCodeyboxExecCall(MultipassCall call) => IsCodeyboxExecArgv(call.Argv);

    private static bool IsCodeyboxExecArgv(IReadOnlyList<string> argv)
        => argv.Contains("/usr/local/bin/codeybox-exec", StringComparer.Ordinal);

    private static bool IsDetachedProcessGroupPoll(IReadOnlyList<string> argv)
        => argv is [_, "exec", _, "--", "sudo", "-n", "sh", "-c", var command, "codeybox-detached-poll", _]
           && command.Contains("codeybox_pgid_marker", StringComparison.Ordinal);

    private static bool IsDetachedOutputSidecarRead(IReadOnlyList<string> argv)
        => argv is [_, "exec", _, "--", "sudo", "-n", "sh", "-c", _, "codeybox-detached-output", _];

    private static bool IsDetachedProcessGroupKill(IReadOnlyList<string> argv)
        => argv is [_, "exec", _, "--", "sudo", "-n", "sh", "-c", var command, "codeybox-detached-kill", _]
           && command.Contains("kill -TERM", StringComparison.Ordinal);

    private static void AssertPngSignature(byte[] bytes)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(
            bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature),
            "screenshot bytes must start with the PNG signature");
    }

    [Fact]
    public async Task CreateAsync_RetriesTransientMultipassSocketLaunchFailureAndSucceeds()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.15.0", ""));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchCalls++;
                if (launchCalls == 1)
                    return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
            {
                var state = states.TryGetValue(name, out var current) ? current : "Running";
                return Task.FromResult(new ProcessRunResult(0, state, ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = WorkItemId.New(),
        });
        await sandbox.DisposeAsync();

        Assert.Equal(2, launchCalls);
        Assert.Equal(1, versionCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("transient multipass daemon error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_StartAlreadyRunning_TreatsAsSuccess()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var startCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0,
                    states.TryGetValue(name, out var current) ? current : "Running",
                    ""));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                startCalls++;
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(1, "", $"instance \"{startName}\" already running"));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-start-already-running"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = WorkItemId.New(),
        });

        Assert.Equal(1, startCalls);
        Assert.Equal("Running", states[sandbox.Id]);
    }

    [Fact]
    public async Task CreateAsync_StartRetryExhaustion_DefersWithStartOperation()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var startCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0,
                    states.TryGetValue(name, out var current) ? current : "Running",
                    ""));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", _])
            {
                startCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "start failed: argument not found"));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-start-exhausted"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("start", ex.Operation);
        Assert.Equal("multipass-start-argument-not-found", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Equal(3, startCalls);
    }

    [Fact]
    public async Task CreateAsync_LaunchFailureWithFailedDeletePurgeAndStillPresent_DefersWithRetainedSandboxName()
    {
        // Exercises MultipassSandboxProvider.CreateAsync's failed-create
        // cleanup branch: launch raises an exception, the best-effort
        // delete --purge returns non-zero, and the subsequent info probe
        // still finds the VM. The provider must surface a
        // SandboxProvisioningDeferredException whose RetainedSandboxName
        // matches the launched VM so the admission decorator can retain the
        // sandbox token until inventory proves the VM is gone.
        var sandboxNames = new ConcurrentQueue<string>();
        var deleteAttempts = new ConcurrentQueue<string>();
        var infoJsonQueries = new ConcurrentQueue<string>();

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                sandboxNames.Enqueue(argv[3]);
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "",
                    "launch failed: invalid memory"));
            }

            if (argv is [_, "info", _, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));

            if (argv is [_, "info", var jsonName, "--format=json"])
            {
                infoJsonQueries.Enqueue(jsonName);
                // VM may still exist on multipassd's side even though our
                // launch raised — emulate by returning success.
                return Task.FromResult(new ProcessRunResult(0, "{\"info\":{}}", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteAttempts.Enqueue(deleteName);
                return Task.FromResult(new ProcessRunResult(1, "", "delete --purge failed: instance is busy"));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-failed-create-still-present"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                TimingWorkItemId = WorkItemId.New(),
            }));

        var sandboxName = Assert.Single(sandboxNames);
        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("create-cleanup", ex.Operation);
        Assert.Equal("multipass-delete-purge-failed", ex.ErrorClass);
        Assert.Equal(sandboxName, ex.RetainedSandboxName);
        Assert.Contains(sandboxName, deleteAttempts);
        Assert.Contains(sandboxName, infoJsonQueries);
    }

    [Theory]
    [InlineData("stop-before-mount", "stop")]
    [InlineData("cloud-init-exec", "exec")]
    [InlineData("env-transfer", "transfer")]
    [InlineData("env-chmod-exec", "exec")]
    [InlineData("baseline-install-exec", "exec")]
    [InlineData("baseline-stop", "stop")]
    [InlineData("clone-source-stop", "stop")]
    public async Task CreateAsync_ProvisioningRetryExhaustionAcrossLifecycleOperations_Defers(
        string failurePoint,
        string expectedOperation)
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var failingCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var csvName, "--format=csv"])
            {
                if (states.TryGetValue(csvName, out var current))
                    return Task.FromResult(new ProcessRunResult(0, current, ""));
                if (csvName.StartsWith("cb-baseline-", StringComparison.Ordinal)
                    && failurePoint == "clone-source-stop")
                    return Task.FromResult(new ProcessRunResult(0, "Stopped", ""));
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                if (failurePoint == "cloud-init-exec")
                    return Task.FromResult(FailTransient(ref failingCalls));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", _]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                if (failurePoint == "baseline-install-exec")
                    return Task.FromResult(FailTransient(ref failingCalls));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                var isBaseline = stopName.StartsWith("cb-baseline-", StringComparison.Ordinal);
                if (failurePoint == "stop-before-mount"
                    || (isBaseline && failurePoint is "baseline-stop" or "clone-source-stop"))
                    return Task.FromResult(FailTransient(ref failingCalls));

                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
            {
                if (failurePoint == "env-transfer")
                    return Task.FromResult(FailTransient(ref failingCalls));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
            {
                if (failurePoint == "env-chmod-exec")
                    return Task.FromResult(FailTransient(ref failingCalls));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var useBaseline = failurePoint is "baseline-install-exec" or "baseline-stop" or "clone-source-stop";
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-" + failurePoint),
            networkProfiles: useBaseline ? new Dictionary<string, string> { ["claude"] = "cb-claude" } : null,
            useBaselineImages: useBaseline,
            extraRuncmd: failurePoint == "baseline-install-exec" ? ["touch /opt/codeybox-baseline"] : null,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                Network = useBaseline ? new SandboxNetworkPolicy { ProfileName = "claude" } : new SandboxNetworkPolicy(),
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal(expectedOperation, ex.Operation);
        Assert.Equal("multipass-instance-lock-contention", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Equal(3, failingCalls);
    }

    [Fact]
    public async Task CreateAsync_TransientMultipassSocketLaunchFailureExhaustsRetriesWithClearMessage()
    {
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "launch")
            {
                launchCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "delete")
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("launch", ex.Operation);
        Assert.Equal("multipass-socket-unreachable", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Contains("multipass daemon unreachable after 2 retries", ex.Detail);
        Assert.Equal(3, launchCalls);
        Assert.Equal(3, versionCalls);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task SandboxExec_RetriesTransientMultipassSocketErrorAndSucceeds()
    {
        // Covers the sandbox-side retry wrapper (MultipassSandbox.RunMultipassAsync),
        // which is a second integration of MultipassDaemonRetry distinct from
        // the provider's CreateAsync path. A transient multipass-socket error
        // surfaced from `multipass exec` mid-lifetime must be retried, not
        // propagated as an exec failure to the caller.
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var execCalls = 0;
        var versionCalls = 0;
        SandboxExecResult? execResult = null;
        var workItemId = WorkItemId.New();

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.15.0", ""));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0, states.TryGetValue(name, out var current) ? current : "Running", ""));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            // Exec of the user payload — first attempt returns a transient
            // socket failure; the second must succeed and produce "hello".
            if (argv.Count >= 4 && argv[1] == "exec" && argv[3] == "--")
            {
                execCalls++;
                if (execCalls == 1)
                    return Task.FromResult(new ProcessRunResult(
                        1, "", "cannot connect to the multipass socket"));
                return Task.FromResult(new ProcessRunResult(0, "hello\n", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });
        try
        {
            execResult = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["echo", "hello"],
            });
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        Assert.NotNull(execResult);
        Assert.Equal(0, execResult.ExitCode);
        Assert.Equal("hello\n", execResult.Stdout);
        // Two exec attempts — first transient, second success.
        Assert.Equal(2, execCalls);
        // Sandbox-side wrapper must have probed multipass version between
        // attempts (proving the retry layer ran on the sandbox, not the
        // provider).
        Assert.True(versionCalls >= 1, $"expected at least one health probe; got {versionCalls}");
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("transient multipass daemon error", StringComparison.Ordinal));
    }


    // ── R8-core SuspendAsync / ResumeSandboxAsync coverage ──────────────────

    [Fact]
    public async Task SuspendAsync_RunsMultipassSuspendAndWritesPreemptMarker()
    {
        // Covers: argv ordering, preempt marker write, _preserveOnDispose
        // being set only AFTER multipass suspend returns success.
        var suspendCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", "codeybox-test"])
            {
                suspendCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await ((ISuspendableSandbox)sandbox).SuspendAsync();

        Assert.Equal(1, suspendCalls);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "SuspendAsync must write the .codeybox-preempt marker so SandboxLeakReaper applies PreemptRetention to leaked suspended VMs.");

        // After success, DisposeAsync must be a no-op (preserve flag flipped).
        // We verify by counting multipass delete calls — there should be zero.
        var deleteCalls = 0;
        runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv.Count >= 2 && argv[1] == "delete") deleteCalls++;
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        // Build a second sandbox so we can verify the no-op-dispose contract
        // independently — the first sandbox above is still preserved in
        // memory and would tie up the workspace if disposed.
        var workspace2 = Directory.CreateTempSubdirectory("codeybox-suspend-").FullName;
        try
        {
            var sandbox2 = new MultipassSandbox(
                "codeybox-test-2", workspace2,
                new SandboxSpec { ImageReference = "ignored", Flavor = SandboxProfileFlavor.Headless },
                new MultipassSandboxOptions { MultipassBinary = "/bin/true" },
                NullLogger<MultipassSandboxProvider>.Instance,
                runner: runner);
            await ((ISuspendableSandbox)sandbox2).SuspendAsync();
            await sandbox2.DisposeAsync();
            Assert.Equal(0, deleteCalls);
        }
        finally
        {
            if (Directory.Exists(workspace2))
                Directory.Delete(workspace2, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndPreserveAsync_RunsStopAndVerifiesStopped()
    {
        var stopCalls = 0;
        var infoCalls = 0;
        var deleteCalls = 0;
        var calls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                calls.Add("delete");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "stop", "codeybox-test"])
            {
                calls.Add("stop");
                stopCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "info", "codeybox-test", "--format=csv"])
            {
                calls.Add("info");
                infoCalls++;
                return Task.FromResult(new ProcessRunResult(
                    0,
                    stopCalls > 0 ? "Stopped" : "Running",
                    ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();

        Assert.Equal(1, stopCalls);
        Assert.True(infoCalls >= 1);
        Assert.True(calls.IndexOf("stop") >= 0 && calls.IndexOf("stop") < calls.IndexOf("info"),
            $"expected stop before info verification, got: {string.Join(", ", calls)}");
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")));
        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_RetriesTransientDaemonStopFailureAndSucceeds()
    {
        var stopCalls = 0;
        var infoCalls = 0;
        var versionCalls = 0;
        var deleteCalls = 0;
        var calls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                calls.Add("version");
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
            }

            if (argv.Count >= 2 && argv[1] == "delete")
            {
                calls.Add("delete");
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "stop", "codeybox-test"])
            {
                calls.Add("stop");
                stopCalls++;
                if (stopCalls == 1)
                    return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", "codeybox-test", "--format=csv"])
            {
                calls.Add("info");
                infoCalls++;
                return Task.FromResult(new ProcessRunResult(
                    0,
                    stopCalls >= 2 ? "Stopped" : "Running",
                    ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = NewMultipassSandbox(
            SandboxProfileFlavor.Headless,
            runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();

        Assert.Equal(2, stopCalls);
        Assert.Equal(1, versionCalls);
        Assert.True(infoCalls >= 1);
        var firstStop = calls.IndexOf("stop");
        var probe = calls.IndexOf("version");
        var secondStop = calls.IndexOf("stop", firstStop + 1);
        Assert.True(firstStop >= 0 && probe > firstStop && secondStop > probe,
            $"expected stop, health probe, second stop; got: {string.Join(", ", calls)}");
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")));

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_NonZeroStopExit_ThrowsAndPreservesOnDispose()
    {
        var stopCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
            {
                stopCalls++;
                return Task.FromResult(new ProcessRunResult(3, "", "multipassd: stop failed"));
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.True(stopCalls >= 1);
        Assert.Contains("multipass stop codeybox-test failed", ex.Message);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "stop/preserve must mark the VM as preempt-retained before multipass stop can fail");

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_NotStoppedAfterSuccessfulStop_ThrowsAndPreservesOnDispose()
    {
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "info", "codeybox-test", "--format=csv"])
                return Task.FromResult(new ProcessRunResult(0, "Running", ""));
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(
            SandboxProfileFlavor.Headless,
            runner,
            vmStopTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.Contains("did not reach Stopped state", ex.Message);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "an abandoned stop verification must still leave the VM preempt-retained");

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_CanceledStop_PreservesVmOnDispose()
    {
        var stopCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
            {
                stopCalls++;
                throw new OperationCanceledException(ct);
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.Equal(1, stopCalls);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")));

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task KillActiveExecsAsync_StopsMultipassVmWithoutDisposing()
    {
        var stopCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
            {
                stopCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await sandbox.KillActiveExecsAsync();

        Assert.Equal(1, stopCalls);
        Assert.Equal(0, deleteCalls);
        Assert.Contains(
            runner.Calls,
            call => call.Argv.SequenceEqual(["/bin/true", "stop", "codeybox-test"]));

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        Assert.Contains(
            runner.Calls,
            call => call.Argv.SequenceEqual(["/bin/true", "delete", "--purge", "codeybox-test"]));
    }

    [Fact]
    public async Task SuspendAsync_NonZeroExit_ThrowsAndLeavesDisposeActive()
    {
        // Critical safety contract: a failed `multipass suspend` MUST NOT flip
        // _preserveOnDispose to true. Otherwise the subsequent DisposeAsync
        // becomes a no-op while the VM is still Running on disk — a silent
        // leak. The SandboxShutdownTeardownService caller persists the
        // SuspendedVmName mapping BEFORE awaiting suspend and CLEARS it again
        // when suspend throws a non-cancellation exception, so this failed,
        // still-Running VM is left with no resume bookkeeping and DisposeAsync
        // tears it down.
        var suspendCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", _])
            {
                suspendCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "multipassd: I/O error suspending VM"));
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            // multipass version probe (daemon health) returns 0 from the
            // retry layer; we want the suspend to fail through.
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((ISuspendableSandbox)sandbox).SuspendAsync());

        Assert.True(suspendCalls >= 1);

        // DisposeAsync now runs and MUST actually call multipass delete,
        // because _preserveOnDispose should still be false after the failed
        // suspend.
        await sandbox.DisposeAsync();
        Assert.Equal(1, deleteCalls);
    }

    [Fact]
    public async Task SuspendAsync_TimedOut_PreservesVmOnDispose()
    {
        // Critical safety contract for the per-VM suspend timeout: when
        // `multipass suspend` is abandoned by OperationCanceledException (the
        // SandboxShutdownTeardownService per-VM timeout fired while multipassd
        // was still writing the RAM snapshot), DisposeAsync MUST NOT run
        // `multipass delete --purge`. multipassd keeps freezing the VM after we
        // give up; the caller keeps the persisted SuspendedVmName mapping so the
        // next startup resumes it (or the leak reaper purges it after the
        // preempt grace). Deleting here would destroy the snapshot mid-write and
        // defeat the whole persist-before-await fix.
        var suspendCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", _])
            {
                suspendCalls++;
                throw new OperationCanceledException("per-VM suspend timeout");
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((ISuspendableSandbox)sandbox).SuspendAsync());

        Assert.True(suspendCalls >= 1);
        // IsSuspended stays false: the VM is not confirmed frozen, so
        // PipelineRunner relies on the persisted mapping gate, not IsSuspended.
        Assert.False(((ISuspendableSandbox)sandbox).IsSuspended);

        // DisposeAsync must be a no-op: the VM multipassd is still snapshotting
        // is owned by the orchestrator's resume/reaper path, not destroyed here.
        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public void MemoryBytes_ReflectsSpecLimits_ForSuspendTimeoutScaling()
    {
        // SandboxShutdownTeardownService.SuspendTimeoutFor scales the per-VM
        // suspend timeout by ISuspendableSandbox.MemoryBytes. MultipassSandbox
        // must surface the provisioned RAM from its SandboxSpec.Limits — and
        // specifically the *memory* field, not disk — so scaling is fed a real
        // value rather than the flat floor. DiskBytes is deliberately set to a
        // different value so a regression that read the wrong field is caught.
        const long sixGiB = 6L * 1024 * 1024 * 1024;
        const long fortyGiB = 40L * 1024 * 1024 * 1024;
        var sandbox = NewMemorySandbox(new SandboxResourceLimits
        {
            MemoryBytes = sixGiB,
            DiskBytes = fortyGiB,
            CpuCount = 4,
        });

        Assert.Equal(sixGiB, ((ISuspendableSandbox)sandbox).MemoryBytes);

        // No reported RAM → null, so SuspendTimeoutFor falls back to the flat
        // floor rather than scaling off a bogus value. A hardcoded non-null
        // would break this.
        var unsized = NewMemorySandbox(new SandboxResourceLimits { DiskBytes = fortyGiB });
        Assert.Null(((ISuspendableSandbox)unsized).MemoryBytes);
    }

    private MultipassSandbox NewMemorySandbox(SandboxResourceLimits limits) =>
        new(
            "codeybox-mem",
            _workspace,
            new SandboxSpec
            {
                ImageReference = "ignored",
                Flavor = SandboxProfileFlavor.Headless,
                Limits = limits,
            },
            new MultipassSandboxOptions { MultipassBinary = "/bin/true" },
            NullLogger<MultipassSandboxProvider>.Instance,
            runner: new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", ""))));

    [Fact]
    public async Task ResumeSandboxAsync_WaitsForSuspendingToSettleBeforeStart()
    {
        // The previous process may have abandoned `multipass suspend` mid-flight,
        // leaving the VM in the transitional `Suspending` state. `multipass start`
        // fails against a Suspending instance, so ResumeSandboxAsync must poll
        // `multipass info` until the state settles before calling start.
        var infoCalls = 0;
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                infoCalls++;
                // First two polls report Suspending, then the snapshot completes.
                var state = infoCalls < 3 ? "Suspending" : "Suspended";
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = state,
                        ["memory"] = new Dictionary<string, object> { ["total"] = 1024L * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-settling", CancellationToken.None);

        Assert.True(infoCalls >= 3, $"expected the resume to poll info until settled; saw {infoCalls} call(s)");
        Assert.Equal(["codeybox-settling"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_WaitLoop_HonoursCancellationWhileStillSuspending()
    {
        // The Suspending wait is bounded by the RAM-scaled SuspendTimeoutPolicy
        // budget (up to 30 min for the default 12 GiB VM), so the loop cannot just
        // ignore the caller's token and block for that long if shutdown / startup
        // is itself aborted. With the VM held permanently at Suspending, cancelling
        // the token must interrupt the wait promptly — the resume surfaces
        // OperationCanceledException and never reaches `multipass start`.
        var startCalls = new List<string>();
        using var cts = new CancellationTokenSource();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(20));
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = "Suspending",
                        ["memory"] = new Dictionary<string, object> { ["total"] = 12L * 1024 * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-stuck", cts.Token));
        Assert.Empty(startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_StillSuspendingPastBudget_StartsAnyway()
    {
        // A VM that never leaves Suspending must not strand the resume: once the
        // RAM-scaled settle budget elapses, WaitWhileSuspendingAsync logs a
        // warning and proceeds to `multipass start` regardless, letting start
        // surface any real error into the standard recovery path. The budget is
        // floored at 10 min in production, so the test injects a tiny override to
        // drive the deadline-expiry branch without waiting out real time.
        var infoCalls = 0;
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                infoCalls++;
                // Permanently Suspending — the snapshot never completes.
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = "Suspending",
                        ["memory"] = new Dictionary<string, object> { ["total"] = 4L * 1024 * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        provider.SuspendSettleBudgetOverride = TimeSpan.FromMilliseconds(50);

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-stillstuck", CancellationToken.None);

        Assert.True(infoCalls >= 1, $"expected at least the initial Suspending probe; saw {infoCalls} call(s)");
        Assert.Equal(["codeybox-stillstuck"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_RunsMultipassStartWithValidatedName()
    {
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-abc123", CancellationToken.None);

        Assert.Equal(["codeybox-abc123"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_StartAlreadyRunning_TreatsAsSuccess()
    {
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(1, "", $"instance \"{name}\" already started"));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-already-running", CancellationToken.None);

        Assert.Equal(["codeybox-already-running"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_StartRetryExhaustion_DefersProvisioning()
    {
        var startCalls = 0;
        var versionCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var infoName, "--format=json"])
            {
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = "Stopped",
                        ["memory"] = new Dictionary<string, object> { ["total"] = 1024L * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            if (argv is [_, "start", _])
            {
                startCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "start failed: argument not found"));
            }

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            runner: runner,
            stagingDirectory: Path.Combine(_workspace, "staging"),
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-start-exhausted", CancellationToken.None));

        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("start", ex.Operation);
        Assert.Equal("multipass-start-argument-not-found", ex.ErrorClass);
        Assert.Equal(InstantDaemonRetryPolicy().ExhaustedRequeueDelay, ex.RecheckIn);
        Assert.Equal(3, startCalls);
        Assert.Equal(3, versionCalls);
    }

    private static ProcessRunResult FailTransient(ref int calls)
    {
        calls++;
        return new ProcessRunResult(
            1,
            "",
            "operation failed: Could not acquire lock for '/var/snap/multipass/common/data/multipassd/multipassd-vm-instances.json'");
    }

    [Fact]
    public async Task ResumeSandboxAsync_RejectsInvalidName()
    {
        var runner = new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox/../../etc/passwd", CancellationToken.None));
    }

    [Fact]
    public async Task ResumeSandboxAsync_NonZeroExit_Throws()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "start", _])
                return Task.FromResult(new ProcessRunResult(2, "", "multipassd: instance not found"));
            // multipass version probe responses
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-zzz", CancellationToken.None));
        Assert.Contains("multipass start", ex.Message);
        Assert.Contains("instance not found", ex.Message);
    }

    [Fact]
    public async Task SuspendableOwners_Registry_PopulatesOnCreateWithWorkItemAndClearsOnDispose()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        // CreateAsync WITHOUT TimingWorkItemId — must NOT register (no owner
        // to suspend back to). The snapshot stays empty.
        var sandboxNoOwner = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        Assert.Empty(((IActiveSandboxProvider)provider).SnapshotActiveSandboxes());

        // CreateAsync WITH TimingWorkItemId — populated; snapshot returns one entry.
        var workItemId = WorkItemId.New();
        var sandboxWithOwner = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });
        var snapshot = ((IActiveSandboxProvider)provider).SnapshotActiveSandboxes();
        Assert.Single(snapshot);
        Assert.Equal(workItemId, snapshot[0].WorkItemId);

        // Dispose removes the owner from the snapshot — defends against the
        // suspend handler trying to freeze a sandbox that just released.
        await sandboxWithOwner.DisposeAsync();
        Assert.Empty(((IActiveSandboxProvider)provider).SnapshotActiveSandboxes());

        await sandboxNoOwner.DisposeAsync();
    }

    [Fact]
    public async Task ActiveSandboxProgressProjection_PopulatesOnCreateWithWorkItemAndClearsOnDispose()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-progress"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());
        var workItemId = WorkItemId.New();

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });

        try
        {
            var progress = ((IActiveSandboxProgressProvider)provider).SnapshotActiveSandboxProgress();
            var entry = Assert.Single(progress);
            Assert.Equal(workItemId, entry.WorkItemId);
            Assert.Equal(sandbox.Id, entry.SandboxId);
            Assert.Equal("active", entry.Status);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        Assert.Empty(((IActiveSandboxProgressProvider)provider).SnapshotActiveSandboxProgress());
    }

    [Fact]
    public async Task CreateAsync_WithTimingWorkItemId_TagsHostCommandsAndGuestEnvironment()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-timing-env"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());
        var workItemId = WorkItemId.New();

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
            Environment = new Dictionary<string, string> { ["CALLER_ENV"] = "kept" },
        });

        try
        {
            var provisioningCalls = runner.Calls
                .Where(call => call.Argv.Count >= 2 && call.Argv[1] != "version")
                .ToArray();
            Assert.NotEmpty(provisioningCalls);
            Assert.All(provisioningCalls, call =>
            {
                Assert.NotNull(call.Environment);
                Assert.Equal(
                    workItemId.ToString(),
                    call.Environment![SandboxConventions.WorkItemIdEnvironmentVariable]);
            });

            var transfer = Assert.Single(runner.Calls, call =>
                call.Argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal));
            var envFile = transfer.Argv[2];
            var envContent = await File.ReadAllTextAsync(envFile);
            Assert.Contains($"{SandboxConventions.WorkItemIdEnvironmentVariable}='{workItemId}'", envContent);
            Assert.Contains("CALLER_ENV='kept'", envContent);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_RemountsReadOnlyHostMountsInsideVm()
    {
        var readOnlySource = Path.Combine(_workspace, "readonly-source");
        var writableSource = Path.Combine(_workspace, "writable-source");
        Directory.CreateDirectory(readOnlySource);
        Directory.CreateDirectory(writableSource);
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-readonly-mount"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { HostPath = readOnlySource, SandboxPath = "/mirror", ReadOnly = true },
                new SandboxMount { HostPath = writableSource, SandboxPath = "/repo", ReadOnly = false },
                new SandboxMount { SandboxPath = "/work", Tmpfs = true },
            ],
        });

        try
        {
            var remount = Assert.Single(runner.Calls, call =>
                call.Argv is [_, "exec", _, "--", "sudo", "mount", "-o", "remount,ro", _]);
            Assert.Equal("/mirror", remount.Argv[^1]);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Argv is [_, "exec", _, "--", "sudo", "mount", "-o", "remount,ro", "/repo"]);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Argv is [_, "exec", _, "--", "sudo", "mount", "-o", "remount,ro", "/work"]);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_SnapshotForIsolationMountsStagedReadOnlySnapshot()
    {
        var source = Path.Combine(_workspace, "snapshot-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "marker.txt"), "snapshot content");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var staging = Path.Combine(_workspace, "staging-snapshot-mount");
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                    SnapshotForIsolation = true,
                },
            ],
        });

        var mount = Assert.Single(runner.Calls, call =>
            call.Argv is [_, "mount", "--type=native", _, var vmTarget]
            && vmTarget.EndsWith(":/repo", StringComparison.Ordinal));
        var mountedSource = mount.Argv[3];

        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(mountedSource));
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}readonly-binds{Path.DirectorySeparatorChar}",
            mountedSource,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(mountedSource, "marker.txt")));
        Assert.Equal("snapshot content", await File.ReadAllTextAsync(Path.Combine(mountedSource, "marker.txt")));
    }

    [Fact]
    public async Task DisposeLeakedAsync_ForTrackedActiveVm_ClearsOwnerSnapshot()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var workItemId = WorkItemId.New();
        await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });
        var vmName = Assert.Single(states.Keys);
        Assert.Single(((IActiveSandboxProvider)provider).SnapshotActiveSandboxes());

        await provider.DisposeLeakedAsync(vmName, CancellationToken.None);

        Assert.Empty(((IActiveSandboxProvider)provider).SnapshotActiveSandboxes());
        Assert.Empty(states);
    }

    /// <summary>
    /// Build a RecordingMultipassRunner that satisfies the full CreateAsync
    /// happy path (launch → cloud-init wait → stop → start → transfer env →
    /// chmod → optional delete). Used by tests that exercise side effects of
    /// the lifecycle (registry population, etc.) rather than the lifecycle
    /// itself.
    /// </summary>
    private static RecordingMultipassRunner BuildSuccessfulCreateRunner(ConcurrentDictionary<string, string> states)
    {
        return new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "info", var csvName, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0, states.TryGetValue(csvName, out var current) ? current : "Running", ""));
            if (argv is [_, "info", var jsonName, "--format=json"])
            {
                var state = states.TryGetValue(jsonName, out var current) ? current : "Running";
                var stdout = JsonSerializer.Serialize(new
                {
                    info = new Dictionary<string, object>
                    {
                        [jsonName] = new
                        {
                            state,
                            memory = new { total = 17179869184L },
                            disks = new Dictionary<string, object>(),
                        },
                    },
                });
                return Task.FromResult(new ProcessRunResult(
                    0,
                    stdout,
                    ""));
            }
            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "test", "-e", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "mount", "--type=native", _, _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "exec", _, "--", "sudo", "mount", "-o", "remount,ro", _])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv"));
        });
    }

    [Fact]
    public void ExecWrapper_TeesStdoutAndStderrWhenAgentLogFileEnvIsSet()
    {
        // R8-core: the codeybox-exec wrapper inside the VM honours
        // CODEYBOX_AGENT_LOG_FILE so the orchestrator can re-tail what the
        // agent emitted after a multipass suspend/start cycle. We verify the
        // wrapper script text directly because we can't shell-exec inside a
        // unit test.
        Assert.Contains("CODEYBOX_AGENT_LOG_FILE", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("tee -a \"$CODEYBOX_AGENT_LOG_FILE\"", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains(".exit", MultipassSandboxProvider.ExecWrapperScript);
        // Defence-in-depth: the existing stdin-close behaviour stays intact
        // when the agent log path is set.
        Assert.Contains("codeybox_run_user_command \"$@\" 2>&1 | tee -a", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("--stdin-file", MultipassSandboxProvider.ExecWrapperScript);
    }

    [Fact]
    public void ExecWrapper_HttpOutputTransportPreflightsAndUnsetsTokenBeforeAgentLaunch()
    {
        Assert.Contains("CODEYBOX_AGENT_OUTPUT_URL", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("CODEYBOX_AGENT_OUTPUT_TOKEN", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("codeybox_http_ready", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("agent output HTTP ingest unavailable before launch", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains(
            "unset CODEYBOX_AGENT_OUTPUT_URL CODEYBOX_AGENT_OUTPUT_TOKEN CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN CODEYBOX_AGENT_OUTPUT_RUN_ID",
            MultipassSandboxProvider.ExecWrapperScript);
    }

    [Fact]
    public async Task ExecWrapper_DoesNotRequireExitTokenForHttpTransport()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The wrapper still tolerates a missing completion token for attached
        // HTTP users. Detached production launches provide the token and require
        // authenticated completion, but the missing token alone must not block
        // this lower-level wrapper path.
        var wrapper = Path.Combine(_workspace, "codeybox-exec-no-exit-token");
        var sentinel = Path.Combine(_workspace, "no-exit-token-agent-started");
        var exitFile = Path.Combine(_workspace, "no-exit-token-agent.exit");
        await File.WriteAllTextAsync(wrapper, MultipassSandboxProvider.ExecWrapperScript);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "exit-token-not-required",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MultipassAgentOutputHttpIngestSession.UrlEnvironmentVariable] = session.BaseUrl,
            [MultipassAgentOutputHttpIngestSession.TokenEnvironmentVariable] = session.Token,
            [MultipassAgentOutputHttpIngestSession.RunIdEnvironmentVariable] = session.RunId,
            ["CODEYBOX_AGENT_EXIT_FILE"] = exitFile,
        };

        var (exit, stdout, _) = await RunLocalProcessAsync(
            "/bin/bash",
            [wrapper, _workspace, "/bin/sh", "-c", "printf launched > \"$1\"; exit 4", "codeybox-no-exit-token", sentinel],
            environmentKeysToRemove:
            [
                "CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN",
            ],
            environmentOverrides: environment);

        Assert.Equal(4, exit);
        Assert.Equal("", stdout);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(exitFile));
        Assert.Equal("4", (await File.ReadAllTextAsync(exitFile)).Trim());
    }

    [Fact]
    public async Task DetachedLaunchScript_PostsAuthenticatedExitForHttpTransport()
    {
        if (OperatingSystem.IsWindows())
            return;

        // With a reachable stream channel, the detached agent must launch, the
        // wrapper must write the diagnostic exit file, and the host listener
        // must receive the authenticated completion value.
        await using var session = await MultipassAgentOutputHttpIngestSession.TryStartAsync(
            System.Net.IPAddress.Loopback,
            "launch-script-no-exit-token",
            NullLogger.Instance,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            CancellationToken.None);
        if (session is null)
            return;

        var wrapper = Path.Combine(_workspace, "codeybox-exec-launch-no-exit-token");
        var envFile = Path.Combine(_workspace, "detached-no-exit-token.env");
        var exitTokenFile = Path.Combine(_workspace, "detached-no-exit-token.token");
        var launchScript = Path.Combine(_workspace, "detached-no-exit-token.sh");
        var processGroupMarker = Path.Combine(_workspace, "detached-no-exit-token.pgid");
        var sentinel = Path.Combine(_workspace, "detached-no-exit-token-agent-started");
        var exitFile = processGroupMarker + ".exit";

        await File.WriteAllTextAsync(wrapper, MultipassSandboxProvider.ExecWrapperScript);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sessionEnvironment = session.BuildEnvironment(includeExitToken: false);
        Assert.DoesNotContain(MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable, sessionEnvironment.Keys);
        var lines = new List<string>();
        foreach (var (k, v) in sessionEnvironment)
            lines.Add($"{k}={MultipassSandboxProvider.ShellSingleQuote(v)}");
        lines.Add($"CODEYBOX_AGENT_EXIT_FILE={MultipassSandboxProvider.ShellSingleQuote(exitFile)}");
        lines.Add("");
        await File.WriteAllTextAsync(envFile, string.Join('\n', lines));
        await File.WriteAllTextAsync(exitTokenFile, session.ExitToken);

        await File.WriteAllTextAsync(
            launchScript,
            MultipassSandbox.BuildDetachedLaunchScript(
                envFile,
                processGroupMarker,
                null,
                [wrapper, _workspace, "/bin/sh", "-c", "printf launched > \"$1\"; exit 2", "codeybox-detached-no-exit-token", sentinel],
                exitTokenFile: exitTokenFile));
        File.SetUnixFileMode(launchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, _) = await RunLocalProcessAsync(
            "/bin/bash",
            [launchScript],
            environmentKeysToRemove:
            [
                "CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN",
            ],
            environmentOverrides: FakeSudoPathEnvironment());
        await WaitForFileAsync(exitFile, TimeSpan.FromSeconds(6));
        await WaitForExitCodeAsync(session, 2, TimeSpan.FromSeconds(6));
        await WaitForProcessGroupGoneAsync(processGroupMarker, TimeSpan.FromSeconds(6));

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.True(File.Exists(processGroupMarker));
        Assert.True(File.Exists(sentinel));
        Assert.False(File.Exists(exitTokenFile));
        Assert.Equal("2", (await File.ReadAllTextAsync(exitFile)).Trim());
    }

    /// <summary>
    /// <c>ExecAsync</c> must retry when the underlying multipass exec fails with
    /// a transient SSH-not-ready error ("Connection refused"). The retry wrapper
    /// around <c>RunMultipassAsync</c> gives sshd one more opportunity to accept
    /// the connection before the work item sees a failure.
    /// </summary>
    [Fact]
    public async Task ExecAsync_RetriesOnSshNotReady_ReturnsSuccessOnRetry()
    {
        var attempts = 0;
        var runner = new RecordingMultipassRunner((_, _, _) =>
        {
            attempts++;
            if (attempts == 1)
                return Task.FromResult(new ProcessRunResult(1, "",
                    "ssh connection failed: 'Connection refused'"));
            return Task.FromResult(new ProcessRunResult(0, "ok", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["git", "add", "-A"] });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Non-SSH errors (e.g. "git: command not found") must NOT trigger a retry.
    /// The retry wrapper must fail fast so callers see the real diagnostic
    /// instead of burning the retry budget on what was never a transient failure.
    /// </summary>
    [Fact]
    public async Task ExecAsync_DoesNotRetryNonSshError()
    {
        var attempts = 0;
        var runner = new RecordingMultipassRunner((_, _, _) =>
        {
            attempts++;
            return Task.FromResult(new ProcessRunResult(127, "", "git: command not found"));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["git", "status"] });

        Assert.Equal(127, result.ExitCode);
        Assert.Contains("git: command not found", result.Stderr);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ProvisioningGate_CapsConcurrentBootsAtConfiguredMax(int maxBoots)
    {
        var provider = NewProvider(maxConcurrentBoots: maxBoots);
        var concurrentCount = 0;
        var maxObserved = 0;
        var lockObj = new object();
        var blocker = new SemaphoreSlim(0);
        var filledBarrier = new TaskCompletionSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var totalTasks = maxBoots + 4;
        var tasks = new Task[totalTasks];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var opts = new MultipassSandboxOptions { MaxConcurrentBoots = maxBoots };
                using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
                var count = Interlocked.Increment(ref concurrentCount);
                lock (lockObj) { if (count > maxObserved) maxObserved = count; }
                if (count >= maxBoots)
                    filledBarrier.TrySetResult();
                await blocker.WaitAsync(cts.Token);
                Interlocked.Decrement(ref concurrentCount);
            });
        }

        // Wait for at least maxBoots tasks to reach the gate (deterministic
        // barrier, not a timeout polling loop).
        await filledBarrier.Task.WaitAsync(cts.Token);

        // Let any queued tasks settle so we can read the peak.
        await Task.Delay(200, cts.Token);

        // Read maxObserved under lock — workers write it under lock, so
        // the assert thread must synchronise to avoid a data race.
        int observed;
        lock (lockObj) { observed = maxObserved; }

        // The gate must prevent more than maxBoots concurrent entries.
        Assert.True(observed <= maxBoots,
            $"Expected at most {maxBoots} concurrent boots, observed {observed}");

        // The gate must allow exactly maxBoots concurrent entries to
        // confirm it's configured to the intended capacity.
        Assert.True(observed == maxBoots,
            $"Expected exactly {maxBoots} concurrent boots, observed {observed}");

        // Release all blockers so tasks can finish cleanly.
        blocker.Release(tasks.Length);

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task BootLaunchDelay_DelaysBeforeReturning()
    {
        const int delayMs = 200;
        var provider = NewProvider(bootLaunchDelay: TimeSpan.FromMilliseconds(delayMs));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions
        {
            BootLaunchDelay = TimeSpan.FromMilliseconds(delayMs)
        };

        var sw = Stopwatch.StartNew();
        using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(delayMs - 50),
            $"Expected at least {delayMs - 50}ms delay, elapsed {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task BootLaunchDelay_CancellationDuringDelayReleasesSlot()
    {
        // Use capacity 1 so the slot release is observable: after the
        // cancelled task releases its slot, a new task can acquire.
        var provider = NewProvider(maxConcurrentBoots: 1, bootLaunchDelay: TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var opts = new MultipassSandboxOptions
        {
            MaxConcurrentBoots = 1,
            BootLaunchDelay = TimeSpan.FromSeconds(10)
        };

        // Acquire the gate — will succeed, then block on the 10s delay.
        // The CTS fires after 200ms, cancelling the delay.
        var ex = await Record.ExceptionAsync(async () =>
        {
            using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
        });
        Assert.NotNull(ex);
        Assert.True(ex is OperationCanceledException || ex is TaskCanceledException);

        // The cancelled task's catch block must have released the slot, so a
        // new acquisition must succeed (not deadlock). Use zero delay for the
        // verification call so it doesn't get stuck in its own delay.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var optsNoDelay = new MultipassSandboxOptions { MaxConcurrentBoots = 1 };
        using var gate2 = await provider.EnterBootGateAsync(optsNoDelay, cts2.Token);
    }

    [Fact]
    public async Task BootLaunchDelay_NegativeDelayLogsWarning()
    {
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var mutableOpts = new MultipassSandboxOptions
        {
            MaxConcurrentBoots = 1,
            BootLaunchDelay = TimeSpan.FromMilliseconds(-500),
        };
        MultipassSandboxOptions ReadOpts() => mutableOpts;
        var provider = new MultipassSandboxProvider(
            ReadOpts,
            logger,
            timings: null,
            runner: new RecordingMultipassRunner((_, _, _) =>
                Task.FromResult(new ProcessRunResult(0, "", ""))),
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var gate = await provider.EnterBootGateAsync(mutableOpts, cts.Token);

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning);
        Assert.Contains("negative", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootGate_ResizesWhenMaxConcurrentBootsChanges()
    {
        // Create a provider with a mutable options delegate so we can
        // change MaxConcurrentBoots between gate acquisitions.
        var mutableOpts = new MultipassSandboxOptions { MaxConcurrentBoots = 3 };
        MultipassSandboxOptions ReadOpts() => mutableOpts;
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = new MultipassSandboxProvider(
            ReadOpts,
            NullLogger<MultipassSandboxProvider>.Instance,
            timings: null,
            runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Fill the old gate (capacity 3).
        var oldHolders = new List<IDisposable>();
        for (var i = 0; i < 3; i++)
            oldHolders.Add(await provider.EnterBootGateAsync(mutableOpts, cts.Token));

        // Increase capacity to 5. The new semaphore has 5 slots; the old
        // holders still reference the old semaphore and don't count
        // against the new one (transient exceedance window, as documented).
        mutableOpts = mutableOpts with { MaxConcurrentBoots = 5 };

        // Up to 5 new acquirers can enter the new semaphore.
        var midHolders = new List<IDisposable>();
        for (var i = 0; i < 5; i++)
            midHolders.Add(await provider.EnterBootGateAsync(mutableOpts, cts.Token));

        foreach (var h in midHolders) h.Dispose();
        foreach (var h in oldHolders) h.Dispose();

        // Downward resize: change to 1.
        mutableOpts = mutableOpts with { MaxConcurrentBoots = 1 };
        var downHolder = await provider.EnterBootGateAsync(mutableOpts, cts.Token);

        // Second acquisition must block (capacity 1, already held).
        var blockedTask = provider.EnterBootGateAsync(mutableOpts, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.False(blockedTask.IsCompleted, "Second acquisition should block on the 1-slot gate");

        downHolder.Dispose();
        using var released = await blockedTask;
    }

    [Fact]
    public async Task ProvisioningGate_CapsConcurrentLaunchesThroughCreateAsync()
    {
        const int maxBoots = 2;
        var staging = Path.Combine(_workspace, "provider-gate-createasync-staging");

        var concurrentLaunches = 0;
        var maxConcurrentLaunches = 0;
        var lockObj = new object();
        var launchEntered = new TaskCompletionSource();
        var allowLaunch = new TaskCompletionSource();
        var allLaunchesStarted = new TaskCompletionSource();

        // Runner that tracks concurrent in-flight launch operations and
        // blocks them until signalled. Also tracks VM states so the full
        // CreateAsync lifecycle (launch→stop→start→transfer→chmod) succeeds.
        var vmStates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return new ProcessRunResult(0, "multipass 1.16.0", "");

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var vmName = argv[3];
                vmStates[vmName] = "Running";

                var c = Interlocked.Increment(ref concurrentLaunches);
                lock (lockObj)
                {
                    if (c > maxConcurrentLaunches) maxConcurrentLaunches = c;
                    if (maxConcurrentLaunches >= maxBoots)
                        allLaunchesStarted.TrySetResult();
                }
                launchEntered.TrySetResult();
                await allowLaunch.Task.WaitAsync(ct);
                Interlocked.Decrement(ref concurrentLaunches);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "info", var infoName, "--format=csv"])
                return new ProcessRunResult(0,
                    vmStates.TryGetValue(infoName, out var state) ? state : "Running", "");

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                vmStates[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                // start is also gated but the test focuses on launch
                // concurrency. Return immediately so the lifecycle proceeds.
                vmStates[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var dest]
                && dest.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                vmStates.TryRemove(deleteName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(0, "", "");
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            maxConcurrentBoots: maxBoots,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start 6 concurrent CreateAsync calls (all non-baseline path).
        var createTasks = new Task<ISandbox>[6];
        for (var i = 0; i < createTasks.Length; i++)
        {
            createTasks[i] = Task.Run<ISandbox>(async () =>
            {
                return await provider.CreateAsync(
                    new SandboxSpec
                    {
                        ImageReference = "ignored",
                        WorkingDirectory = "/work",
                    },
                    ct: cts.Token);
            });
        }

        // Wait until at least maxBoots launches have entered, confirming
        // the gate is active and capping concurrency.
        await allLaunchesStarted.Task.WaitAsync(cts.Token);

        // Brief settle delay so tasks queued at the gate don't increase
        // the count further yet.
        await Task.Delay(200, cts.Token);

        int observedLaunches;
        lock (lockObj) { observedLaunches = maxConcurrentLaunches; }

        Assert.True(observedLaunches <= maxBoots,
            $"Expected at most {maxBoots} concurrent launches through CreateAsync, observed {observedLaunches}");

        Assert.True(observedLaunches == maxBoots,
            $"Expected exactly {maxBoots} concurrent launches through CreateAsync, observed {observedLaunches}");

        // Release all blocked launches.
        allowLaunch.SetResult();

        await Task.WhenAll(createTasks);
        foreach (var t in createTasks)
        {
            var sandbox = await t;
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisioningGate_CapsConcurrentBaselineBakes()
    {
        const int maxBoots = 1;
        var staging = Path.Combine(_workspace, "provider-gate-baseline-staging");

        var concurrentBakes = 0;
        var maxConcurrentBakes = 0;
        var lockObj = new object();
        var allowBake = new TaskCompletionSource();
        var allBakesStarted = new TaskCompletionSource();

        var vmStates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return new ProcessRunResult(0, "multipass 1.16.0", "");

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (vmStates.TryGetValue(infoName, out var s))
                    return new ProcessRunResult(0, s, "");
                return new ProcessRunResult(1, "", "");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name"
                && argv[3].StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                var vmName = argv[3];
                vmStates[vmName] = "Running";

                var c = Interlocked.Increment(ref concurrentBakes);
                lock (lockObj)
                {
                    if (c > maxConcurrentBakes) maxConcurrentBakes = c;
                    if (maxConcurrentBakes >= maxBoots)
                        allBakesStarted.TrySetResult();
                }
                await allowBake.Task.WaitAsync(ct);
                Interlocked.Decrement(ref concurrentBakes);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                vmStates[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "clone", var sourceName, "--name", var newName])
            {
                vmStates[newName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                vmStates[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var dest]
                && dest.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var delName])
            {
                vmStates.TryRemove(delName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(0, "", "");
        });

        var networkProfiles = new Dictionary<string, string>
        {
            ["test-iso"] = "cb-iso",
            ["test-claude"] = "cb-claude",
        };

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            networkProfiles: networkProfiles,
            useBaselineImages: true,
            maxConcurrentBoots: maxBoots,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var profiles = new[] { "test-iso", "test-claude" };
        var createTasks = new Task<ISandbox>[2];
        for (var i = 0; i < createTasks.Length; i++)
        {
            var profile = profiles[i];
            createTasks[i] = Task.Run<ISandbox>(async () =>
            {
                return await provider.CreateAsync(
                    new SandboxSpec
                    {
                        ImageReference = "ignored",
                        WorkingDirectory = "/work",
                        Network = new SandboxNetworkPolicy { ProfileName = profile },
                    },
                    ct: cts.Token);
            });
        }

        await allBakesStarted.Task.WaitAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        int observed;
        lock (lockObj) { observed = maxConcurrentBakes; }

        Assert.True(observed <= maxBoots,
            $"Expected at most {maxBoots} concurrent baseline bakes, observed {observed}");
        Assert.True(observed == maxBoots,
            $"Expected exactly {maxBoots} concurrent baseline bakes, observed {observed}");

        allowBake.SetResult();

        await Task.WhenAll(createTasks);
        foreach (var t in createTasks)
        {
            var sandbox = await t;
            await sandbox.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("suspend")]
    [InlineData("delete")]
    [InlineData("purge")]
    [InlineData("mount")]
    [InlineData("umount")]
    [InlineData("transfer")]
    [InlineData("clone")]
    [InlineData("set")]
    public void OpGate_ClassifiesLifecycleAndFilesystemOpsAsHeavy(string op)
    {
        // The verified contention surface on the snap-confined multipassd is
        // lifecycle + filesystem ops, not exec or status polls. Pin every
        // entry so a future rename of the gate semantic surfaces here.
        Assert.True(MultipassSandboxProvider.IsHeavyMultipassOperation(
            ["multipass", op, "some-vm"]));
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("info")]
    [InlineData("list")]
    [InlineData("version")]
    [InlineData("networks")]
    [InlineData("get")]
    [InlineData("find")]
    [InlineData("alias")]
    [InlineData("shell")]
    public void OpGate_ClassifiesExecAndStatusOpsAsLight(string op)
    {
        // CRITICAL: `multipass exec` MUST classify as light. An agent run
        // issues hundreds of exec calls against an already-booted VM, and
        // gating those to N=2 would serialise the entire fleet.
        Assert.False(MultipassSandboxProvider.IsHeavyMultipassOperation(
            ["multipass", op, "some-vm"]));
    }

    [Fact]
    public void OpGate_ClassifiesShortArgvAsLight()
    {
        // Defensive: a bare or unrecognised argv shouldn't accidentally
        // serialise.
        Assert.False(MultipassSandboxProvider.IsHeavyMultipassOperation([]));
        Assert.False(MultipassSandboxProvider.IsHeavyMultipassOperation(["multipass"]));
        Assert.False(MultipassSandboxProvider.IsHeavyMultipassOperation(
            ["multipass", "definitely-not-a-real-subcommand"]));
    }

    [Fact]
    public async Task OpGate_CapsConcurrentHeavyOpsAtConfiguredMax()
    {
        // Mirror of ProvisioningGate_CapsConcurrentBootsAtConfiguredMax but
        // exercises the op-classifying entrypoint with the FULL heavy-op
        // surface (mount / transfer / stop / delete / clone) — the verified
        // root cause of the 2026-06-10/11 mount-stat-timeout cluster was
        // these ops, not launch/start alone.
        const int maxOps = 2;
        var provider = NewProvider(maxConcurrentBoots: maxOps);
        var concurrentCount = 0;
        var maxObserved = 0;
        var lockObj = new object();
        var blocker = new SemaphoreSlim(0);
        var filledBarrier = new TaskCompletionSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Vary the heavy op verb across acquisitions so the test pins that
        // the SAME semaphore is shared across op types (transfer mustn't
        // open its own ungated path because the verified breakage was a
        // mix of mount + clone + transfer all hitting multipassd at once).
        string[] heavyOps = ["mount", "transfer", "stop", "delete", "clone", "launch"];
        const int totalTasks = 8;
        var tasks = new Task[totalTasks];
        for (var i = 0; i < tasks.Length; i++)
        {
            var op = heavyOps[i % heavyOps.Length];
            tasks[i] = Task.Run(async () =>
            {
                var opts = new MultipassSandboxOptions { MaxConcurrentBoots = maxOps };
                IReadOnlyList<string> argv = ["multipass", op, "vm-" + op];
                using var gate = await provider.EnterMultipassOpGateAsync(opts, argv, cts.Token);
                var count = Interlocked.Increment(ref concurrentCount);
                lock (lockObj) { if (count > maxObserved) maxObserved = count; }
                if (count >= maxOps)
                    filledBarrier.TrySetResult();
                await blocker.WaitAsync(cts.Token);
                Interlocked.Decrement(ref concurrentCount);
            });
        }

        await filledBarrier.Task.WaitAsync(cts.Token);
        // Let any queued tasks settle so we can read the peak under lock.
        await Task.Delay(200, cts.Token);

        int observed;
        lock (lockObj) { observed = maxObserved; }

        Assert.True(observed <= maxOps,
            $"Expected at most {maxOps} concurrent heavy multipass ops, observed {observed}");
        Assert.True(observed == maxOps,
            $"Expected exactly {maxOps} concurrent heavy multipass ops, observed {observed}");

        blocker.Release(tasks.Length);
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task OpGate_DoesNotGateExec()
    {
        // CRITICAL invariant from the work order: gating `multipass exec`
        // would cripple fleet throughput (an agent run issues hundreds of
        // execs against an already-booted VM). Pin that exec ALWAYS gets a
        // no-op disposable so it runs at unbounded concurrency, even with
        // MaxConcurrentBoots clamped to 1.
        const int maxOps = 1;
        var provider = NewProvider(maxConcurrentBoots: maxOps);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opts = new MultipassSandboxOptions { MaxConcurrentBoots = maxOps };

        const int execFanout = 16;
        var concurrentExecs = 0;
        var maxObservedExecs = 0;
        var lockObj = new object();
        var blocker = new TaskCompletionSource();
        var saturated = new TaskCompletionSource();

        var tasks = new Task[execFanout];
        for (var i = 0; i < execFanout; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                IReadOnlyList<string> execArgv =
                    ["multipass", "exec", "some-vm", "--", "echo", "hi"];
                using var gate = await provider.EnterMultipassOpGateAsync(opts, execArgv, cts.Token);
                var count = Interlocked.Increment(ref concurrentExecs);
                lock (lockObj) { if (count > maxObservedExecs) maxObservedExecs = count; }
                if (count >= execFanout)
                    saturated.TrySetResult();
                await blocker.Task.WaitAsync(cts.Token);
                Interlocked.Decrement(ref concurrentExecs);
            });
        }

        // If exec were being serialised by the boot gate (capacity 1),
        // saturated would never complete and the timeout would fire.
        await saturated.Task.WaitAsync(cts.Token);

        int observed;
        lock (lockObj) { observed = maxObservedExecs; }
        Assert.Equal(execFanout, observed);

        blocker.SetResult();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task OpGate_HeavyAndExecShareDifferentLanes()
    {
        // Pin the SCOPE separation directly: with MaxConcurrentBoots=1, a
        // heavy op holds the only slot — but exec must still proceed
        // immediately, because the op-gate hands exec a no-op disposable.
        // Regression target: if a future refactor folds exec into the same
        // semaphore, this test fails before fleet throughput silently
        // collapses in production.
        var provider = NewProvider(maxConcurrentBoots: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions { MaxConcurrentBoots = 1 };

        // Hold the single heavy slot.
        using var heavyHold = await provider.EnterMultipassOpGateAsync(
            opts, ["multipass", "mount", "host", "vm:guest"], cts.Token);

        // Exec must STILL acquire immediately even though the heavy lane is
        // full — fan-out a few execs concurrently and require they all
        // enter their gates within the deadline.
        var execTasks = new Task[8];
        for (var i = 0; i < execTasks.Length; i++)
        {
            execTasks[i] = Task.Run(async () =>
            {
                using var gate = await provider.EnterMultipassOpGateAsync(
                    opts,
                    ["multipass", "exec", "some-vm", "--", "echo", "hi"],
                    cts.Token);
                // Exit immediately; the assertion is "this completes
                // without contending with the held heavy slot."
            });
        }
        await Task.WhenAll(execTasks).WaitAsync(cts.Token);
    }

    [Fact]
    public async Task OpGate_DoesNotApplyLaunchDelayToNonBootHeavyOps()
    {
        // BootLaunchDelay was added to stagger qemu spin-up; applying it
        // to every transfer / stop / delete would needlessly slow down
        // teardown without changing the IO-contention shape. Pin that
        // non-boot heavy ops skip the stagger.
        var provider = NewProvider(
            maxConcurrentBoots: 4,
            bootLaunchDelay: TimeSpan.FromMilliseconds(750));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions
        {
            MaxConcurrentBoots = 4,
            BootLaunchDelay = TimeSpan.FromMilliseconds(750),
        };

        var sw = Stopwatch.StartNew();
        using var gate = await provider.EnterMultipassOpGateAsync(
            opts, ["multipass", "transfer", "src", "dst"], cts.Token);
        sw.Stop();

        // 250ms slack covers thread-pool startup variance on cold runs.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250),
            $"Expected transfer to skip BootLaunchDelay; elapsed {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task ProvisioningGate_ClampsInvalidMaxConcurrentBootsToOne()
    {
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(maxConcurrentBoots: 0, logger: logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions { MaxConcurrentBoots = 0 };

        using var gate = await provider.EnterBootGateAsync(opts, cts.Token);

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning);
        Assert.Contains("clamping to 1", warning.Message);
        Assert.Contains("0", warning.Message);
    }

    private static MultipassDaemonRetryPolicy InstantDaemonRetryPolicy() => new()
    {
        Delay = (_, _) => Task.CompletedTask,
        HealthProbeTimeout = TimeSpan.FromMilliseconds(100),
    };
}
