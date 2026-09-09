using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class SandboxCredentialMaterializationSecurityTests
{
    [Fact]
    public async Task EnvironmentMaterialiser_RejectsOversizedPayloadBeforePreservingExistingFile()
    {
        const int testLimitBytes = 1024;
        const string payloadEnvironmentVariable = "TEST_CREDENTIAL_PAYLOAD";
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Environment = new Dictionary<string, string>
            {
                [payloadEnvironmentVariable] = new string('x', testLimitBytes + 1),
            },
        });
        var seed = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "mkdir -p -- \"$HOME/.credential-test\" && " +
                "printf '%s' 'refreshed-in-sandbox' > \"$HOME/.credential-test/auth.json\"",
            ],
        });
        Assert.True(seed.Success, seed.Stderr);

        var script = SandboxCredentialFileWriter.BuildEnvironmentMaterialisationScript(
            payloadEnvironmentVariable,
            ".credential-test/auth.json",
            overwritePolicy: SandboxCredentialOverwritePolicy.PreserveNonEmpty);
        var productionLimit = AgentCredentialMaterializationPolicy.MaterializationBudgetBytes.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(productionLimit, script, StringComparison.Ordinal);
        script = script.Replace(
            productionLimit,
            testLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        var materialize = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", script],
        });

        Assert.False(materialize.Success);
        var verify = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["bash", "-c", "cat -- \"$HOME/.credential-test/auth.json\""],
        });
        Assert.True(verify.Success, verify.Stderr);
        Assert.Equal("refreshed-in-sandbox", verify.Stdout.TrimEnd());
    }

    [Fact]
    public async Task PreserveNonEmpty_DrainsNearLimitPayloadAndHardensExistingFileTo0600()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var seed = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "mkdir -p -- \"$HOME/.credential-test\" && " +
                "printf '%s' 'refreshed-in-sandbox' > \"$HOME/.credential-test/auth.json\" && " +
                "chmod 0755 \"$HOME/.credential-test\" && chmod 0644 \"$HOME/.credential-test/auth.json\"",
            ],
        });
        Assert.True(seed.Success, seed.Stderr);

        var nearLimitPayload = new string(
            'x',
            checked((int)AgentCredentialMaterializationPolicy.MaterializationBudgetBytes - 1));
        await SandboxCredentialFileWriter.WriteAsync(
            sandbox,
            new SandboxCredentialFileTarget(
                SandboxCredentialFileRoot.Home,
                ".credential-test/auth.json"),
            nearLimitPayload,
            SandboxCredentialOverwritePolicy.PreserveNonEmpty);

        var verify = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "printf '%s|%s|%s' " +
                "\"$(stat -c %a \"$HOME/.credential-test\")\" " +
                "\"$(stat -c %a \"$HOME/.credential-test/auth.json\")\" " +
                "\"$(cat \"$HOME/.credential-test/auth.json\")\"",
            ],
        });
        Assert.True(verify.Success, verify.Stderr);
        Assert.Equal("700|600|refreshed-in-sandbox", verify.Stdout.Trim());
    }

    [Theory]
    [InlineData(PreflightScenario.Count)]
    [InlineData(PreflightScenario.InvalidPath)]
    [InlineData(PreflightScenario.PageRoundedAggregate)]
    [InlineData(PreflightScenario.MultipleAmbientFiles)]
    public async Task RunnerPreflight_RejectsInvalidWholePlanBeforeFirstSandboxExec(
        PreflightScenario scenario)
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var inner = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await using var sandbox = new CountingSandbox(inner);
        var runner = new PreflightRunner(scenario);
        var credential = scenario == PreflightScenario.MultipleAmbientFiles
            ? null
            : new AgentCredential(
                runner.Kind,
                new Dictionary<string, string>
                {
                    [PreflightRunner.PayloadEnvironmentVariable] = scenario == PreflightScenario.PageRoundedAggregate
                        ? new string(
                            'x',
                            checked((int)(AgentCredentialMaterializationPolicy.MaterializationBudgetBytes / 2 + 1)))
                        : "credential",
                },
                new Dictionary<string, string>());

        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential);

        Assert.False(result.Success);
        Assert.Contains("credential materialization plan is invalid", result.Summary, StringComparison.Ordinal);
        Assert.Equal(0, sandbox.ExecCount);
    }

    [Fact]
    public void MaterializationPlan_RejectsAbsoluteAndRelativeHomeTargetsAsPotentialAliases()
    {
        var plan = new[]
        {
            new SandboxCredentialFileMaterialization(
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ".config/tool/auth.json"),
                "first"),
            new SandboxCredentialFileMaterialization(
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ".local/share/tool/auth.json",
                    "/home/codeybox/.config/tool/auth.json"),
                "second"),
        };

        var error = Assert.Throws<ArgumentException>(
            () => SandboxCredentialFileWriter.ValidateMaterializationPlan(credential: null, plan));

        Assert.Contains("cannot coexist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializationPlan_AllowsDistinctCanonicalAbsoluteHomeTargets()
    {
        var plan = new[]
        {
            new SandboxCredentialFileMaterialization(
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ".config/tool/first.json",
                    "/home/codeybox/.config/tool/first.json"),
                "first"),
            new SandboxCredentialFileMaterialization(
                new SandboxCredentialFileTarget(
                    SandboxCredentialFileRoot.Home,
                    ".config/tool/second.json",
                    "/home/codeybox/.config/tool/second.json"),
                "second"),
        };

        SandboxCredentialFileWriter.ValidateMaterializationPlan(credential: null, plan);
    }

    [Fact]
    public void AgentCredential_SnapshotsMutableInputBeforeItCanReachSandboxSinks()
    {
        var environment = new Dictionary<string, string> { ["TOKEN"] = "original" };
        var files = new Dictionary<string, string> { ["agent/auth.json"] = "original" };
        var credential = new AgentCredential(new AgentKind("test"), environment, files);

        environment["TOKEN"] = "mutated";
        files["agent/auth.json"] = "mutated";
        files["agent//alias.json"] = "invalid";

        Assert.Equal("original", credential.EnvironmentVariables["TOKEN"]);
        Assert.Equal("original", credential.Files["agent/auth.json"]);
        Assert.Single(credential.Files);
    }

    [Fact]
    public void AgentCredential_RejectsOversizedEnvironmentValueBeforeScanningForNul()
    {
        var oversizedWithTrailingNul =
            new string('x', checked((int)AgentCredentialMaterializationPolicy.MaterializationBudgetBytes + 1)) +
            "\0";

        var error = Assert.Throws<ArgumentException>(() => new AgentCredential(
            new AgentKind("test"),
            new Dictionary<string, string> { ["TOKEN"] = oversizedWithTrailingNul },
            new Dictionary<string, string>()));

        Assert.Contains("budget", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NUL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentCredential_RejectsOversizedWhitespaceMountPathBeforeWhitespaceScan()
    {
        var oversizedWhitespacePath = new string(
            ' ',
            AgentCredentialMaterializationPolicy.MaximumPathUtf8Bytes + 1);

        var error = Assert.Throws<ArgumentException>(() => new AgentCredential(
            new AgentKind("test"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>())
        {
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = oversizedWhitespacePath,
                    SandboxPath = "/home/codeybox/.config/tool",
                    ReadOnly = true,
                },
            ],
        });

        Assert.Contains("at most", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("name a host source", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentCredential_RejectsUnsafeCredentialAdjunctMounts()
    {
        const string hostPath = "/tmp/codeybox-credential-adjunct";
        const string sandboxPath = "/home/codeybox/.config/tool";
        var unsafeMounts = new SandboxMount[]
        {
            new() { HostPath = hostPath, SandboxPath = sandboxPath, ReadOnly = false },
            new() { HostPath = hostPath, SandboxPath = sandboxPath, ReadOnly = true, Tmpfs = true },
            new() { SandboxPath = sandboxPath, ReadOnly = true },
            new() { HostPath = "relative/source", SandboxPath = sandboxPath, ReadOnly = true },
            new() { HostPath = "/tmp/../etc/passwd", SandboxPath = sandboxPath, ReadOnly = true },
            new() { HostPath = hostPath, SandboxPath = "relative/destination", ReadOnly = true },
            new() { HostPath = hostPath, SandboxPath = "/home/../etc", ReadOnly = true },
            new() { HostPath = hostPath, SandboxPath = sandboxPath, ReadOnly = true, SizeBytes = 4096 },
        };

        foreach (var mount in unsafeMounts)
        {
            _ = Assert.Throws<ArgumentException>(() => new AgentCredential(
                new AgentKind("test"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>())
            {
                Mounts = [mount],
            });
        }
    }

    [Fact]
    public void AgentCredential_SnapshotsSafeCredentialAdjunctMounts()
    {
        var source = new List<SandboxMount>
        {
            new()
            {
                HostPath = "/tmp/codeybox-credential-adjunct",
                SandboxPath = "/home/codeybox/.config/tool",
                ReadOnly = true,
                SnapshotForIsolation = true,
            },
        };
        var credential = new AgentCredential(
            new AgentKind("test"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>())
        {
            Mounts = source,
        };

        source.Clear();

        var mount = Assert.Single(credential.Mounts);
        Assert.True(mount.ReadOnly);
        Assert.True(mount.SnapshotForIsolation);
        Assert.Equal("/tmp/codeybox-credential-adjunct", mount.HostPath);
        Assert.Equal("/home/codeybox/.config/tool", mount.SandboxPath);
    }

    public enum PreflightScenario
    {
        Count,
        InvalidPath,
        PageRoundedAggregate,
        MultipleAmbientFiles,
    }

    private sealed class PreflightRunner(PreflightScenario scenario) : CliAgentRunnerBase
    {
        internal const string PayloadEnvironmentVariable = "TEST_CREDENTIAL_PAYLOAD";

        public override AgentKind Kind => new("credential-preflight-test");

        protected override IReadOnlyList<EnvBackedCredentialFile> EnvBackedCredentialFiles =>
            scenario switch
            {
                PreflightScenario.Count => Enumerable.Range(
                        0,
                        AgentCredentialMaterializationPolicy.MaximumFiles + 1)
                    .Select(index => new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        $".credentials/file-{index:D3}",
                        "count test"))
                    .ToArray(),
                PreflightScenario.InvalidPath =>
                [
                    new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        ".credentials//auth.json",
                        "path test"),
                ],
                PreflightScenario.PageRoundedAggregate =>
                [
                    new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        ".credentials/first.json",
                        "aggregate test"),
                    new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        ".credentials/second.json",
                        "aggregate test"),
                ],
                PreflightScenario.MultipleAmbientFiles =>
                [
                    new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        ".credentials/first.json",
                        "ambient test",
                        MaterialiseFromSandboxEnvironmentWhenCredentialMissing: true),
                    new EnvBackedCredentialFile(
                        PayloadEnvironmentVariable,
                        ".credentials/second.json",
                        "ambient test",
                        MaterialiseFromSandboxEnvironmentWhenCredentialMissing: true),
                ],
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown preflight scenario."),
            };

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false) =>
            new(["/bin/true"]);
    }

    private sealed class CountingSandbox(ISandbox inner) : ISandboxDecorator
    {
        public ISandbox InnerSandbox { get; } = inner;
        public string Id => InnerSandbox.Id;
        public int ExecCount { get; private set; }

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecCount++;
            return await InnerSandbox.ExecAsync(exec, ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => InnerSandbox.DisposeAsync();
    }
}
