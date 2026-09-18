using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CodeyBox.Tests;

/// <summary>
/// Teardown-resilience coverage for the Incus provider: the benign AppArmor
/// profile-unload race ("Profile doesn't exist") must read as success once
/// the goal state is verified, while any other teardown failure must stay
/// reported and keep the sandbox tracked in the managed inventory.
/// </summary>
public sealed class IncusTeardownResilienceTests
{
    private const string ApparmorEvidenceStderr =
        "Error: Failed to run: apparmor_parser -RWL /var/lib/incus/security/apparmor/cache " +
        "/var/lib/incus/security/apparmor/profiles/incus-codeybox-sandboxes_codeybox-48a63b85de644b5d9312: " +
        "exit status 254 (apparmor_parser: Unable to remove " +
        "\"incus-codeybox-sandboxes_codeybox-48a63b85de644b5d9312_</var/lib/incus>\". Profile doesn't exist)";

    [Fact]
    public void BenignMatcher_MatchesEvidenceShapedStopFailure()
    {
        var failure = new InvalidOperationException(
            $"Incus VM stop failed with exit code 1: {ApparmorEvidenceStderr}");

        Assert.True(IncusBenignTeardown.IsBenignApparmorProfileTeardown(failure));
    }

    [Fact]
    public void BenignMatcher_MatchesWrappedEvidenceFailure()
    {
        var inner = new InvalidOperationException(
            $"Incus VM stop failed with exit code 1: {ApparmorEvidenceStderr}");
        var outer = new InvalidOperationException("Incus stop could not reach a verified STOPPED state.", inner);

        Assert.True(IncusBenignTeardown.IsBenignApparmorProfileTeardown(outer));
    }

    [Theory]
    [InlineData("Incus VM stop failed with exit code 1: Error: Device not found")]
    [InlineData("Incus VM stop failed with exit code 1: Error: Failed to run: apparmor_parser -RWL /cache /profiles/x: exit status 254 (apparmor_parser: failed to load profile)")]
    [InlineData("Incus VM stop failed with exit code 1: Error: lxc: Profile doesn't exist")]
    [InlineData("")]
    public void BenignMatcher_RejectsNonBenignFailures(string message)
    {
        Assert.False(IncusBenignTeardown.IsBenignApparmorProfileTeardown(
            new InvalidOperationException(message)));
    }

    [Fact]
    public void BenignMatcher_RejectsNull()
    {
        Assert.False(IncusBenignTeardown.IsBenignApparmorProfileTeardown(null));
    }

    [Fact]
    public async Task ProviderStop_BenignApparmorFailureWithStoppedVm_Succeeds()
    {
        const string instanceName = "codeybox-teardown-benign";
        var runner = new StopProbeRunner(instanceName, stopStderr: ApparmorEvidenceStderr, stopExitCode: 1, listStatus: "STOPPED");
        var provider = StopProbeProvider(runner);
        var options = StopProbeOptions();

        await provider.StopInstanceAsync(options, instanceName, stateful: false, CancellationToken.None);

        Assert.Equal(1, runner.StopCalls);
        Assert.True(runner.ListCalls >= 1);
    }

    [Fact]
    public async Task ProviderStop_BenignApparmorFailureWithAbsentVm_Succeeds()
    {
        const string instanceName = "codeybox-teardown-absent";
        var runner = new StopProbeRunner(instanceName, stopStderr: ApparmorEvidenceStderr, stopExitCode: 1, listStatus: null);
        var provider = StopProbeProvider(runner);
        var options = StopProbeOptions();

        await provider.StopInstanceAsync(options, instanceName, stateful: false, CancellationToken.None);

        Assert.Equal(1, runner.StopCalls);
    }

    [Fact]
    public async Task ProviderStop_BenignApparmorFailureWithRunningVm_Rethrows()
    {
        const string instanceName = "codeybox-teardown-running";
        var runner = new StopProbeRunner(instanceName, stopStderr: ApparmorEvidenceStderr, stopExitCode: 1, listStatus: "RUNNING");
        var provider = StopProbeProvider(runner);
        var options = StopProbeOptions();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StopInstanceAsync(options, instanceName, stateful: false, CancellationToken.None));

        Assert.Contains("VM stop failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Profile doesn't exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderStop_BenignApparmorFailureWithUnverifiableVm_RethrowsOriginal()
    {
        const string instanceName = "codeybox-teardown-unverifiable";
        var runner = new StopProbeRunner(instanceName, stopStderr: ApparmorEvidenceStderr, stopExitCode: 1, listStatus: "STOPPED", listThrows: true);
        var provider = StopProbeProvider(runner);
        var options = StopProbeOptions();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StopInstanceAsync(options, instanceName, stateful: false, CancellationToken.None));

        Assert.Contains("VM stop failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Profile doesn't exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderStop_OrdinaryFailureWithStoppedVm_Rethrows()
    {
        const string instanceName = "codeybox-teardown-ordinary";
        var runner = new StopProbeRunner(instanceName, stopStderr: "Error: Device not found", stopExitCode: 1, listStatus: "STOPPED");
        var provider = StopProbeProvider(runner);
        var options = StopProbeOptions();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StopInstanceAsync(options, instanceName, stateful: false, CancellationToken.None));

        Assert.Contains("VM stop failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxStopAndPreserve_BenignApparmorFailure_RemainsPreservedWithoutThrow()
    {
        const string sandboxName = "codeybox-preserve-benign";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-preserve-benign-{Guid.NewGuid():N}");
        var status = "RUNNING";
        var inactive = 0;
        var runner = new TeardownScriptRunner(
            onConfigSet: static () => new ProcessRunResult(0, string.Empty, string.Empty),
            onStop: static _ => new ProcessRunResult(1, string.Empty, ApparmorEvidenceStderr),
            onList: () => new ProcessRunResult(0, OwnedInstanceJson(sandboxName, status), string.Empty),
            onDelete: static _ => new ProcessRunResult(0, string.Empty, string.Empty));
        var sandbox = CreateTeardownSandbox(sandboxName, root, runner, name => Interlocked.Increment(ref inactive), out var binding);
        runner.SetRecoveryBinding(binding.TokenHash, binding.ManifestHash);
        SandboxLiveCounter.Increment();

        try
        {
            status = "STOPPED";

            await sandbox.StopAndPreserveAsync();

            Assert.Contains(
                runner.Commands,
                command => command.Any(argument => string.Equals(
                    argument,
                    $"{IncusSandboxProvider.PreemptKey}=true",
                    StringComparison.Ordinal)));
            Assert.True(Directory.Exists(Path.Combine(root, sandboxName)));

            await sandbox.DisposeAsync();

            Assert.DoesNotContain(runner.Commands, command => command.Contains("delete", StringComparer.Ordinal));
            Assert.Equal(1, Volatile.Read(ref inactive));
        }
        finally
        {
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SandboxStopAndPreserve_OrdinaryFailureWithStoppedVm_StillThrows()
    {
        const string sandboxName = "codeybox-preserve-ordinary";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-preserve-ordinary-{Guid.NewGuid():N}");
        var inactive = 0;
        var runner = new TeardownScriptRunner(
            onConfigSet: static () => new ProcessRunResult(0, string.Empty, string.Empty),
            onStop: static _ => new ProcessRunResult(1, string.Empty, "Error: Device not found"),
            onList: () => new ProcessRunResult(0, OwnedInstanceJson(sandboxName, "STOPPED"), string.Empty),
            onDelete: static _ => new ProcessRunResult(0, string.Empty, string.Empty));
        var sandbox = CreateTeardownSandbox(sandboxName, root, runner, name => Interlocked.Increment(ref inactive), out var binding);
        runner.SetRecoveryBinding(binding.TokenHash, binding.ManifestHash);
        SandboxLiveCounter.Increment();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.StopAndPreserveAsync());

            Assert.Contains("remains preserved", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SandboxDispose_GenuineDeleteFailure_RemainsTrackedAndSurfaced()
    {
        const string sandboxName = "codeybox-dispose-genuine";
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-dispose-genuine-{Guid.NewGuid():N}");
        var inactive = 0;
        var runner = new TeardownScriptRunner(
            onConfigSet: static () => new ProcessRunResult(0, string.Empty, string.Empty),
            onStop: static _ => new ProcessRunResult(0, string.Empty, string.Empty),
            onList: () => new ProcessRunResult(0, OwnedInstanceJson(sandboxName, "RUNNING"), string.Empty),
            onDelete: static _ => new ProcessRunResult(1, string.Empty, "Error: Device or resource busy"));
        var sandbox = CreateTeardownSandbox(sandboxName, root, runner, name => Interlocked.Increment(ref inactive), out var binding);
        runner.SetRecoveryBinding(binding.TokenHash, binding.ManifestHash);
        SandboxLiveCounter.Increment();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sandbox.DisposeAsync().AsTask());

            Assert.Contains("delete sandbox VM", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref inactive));
            Assert.True(Directory.Exists(Path.Combine(root, sandboxName)));
        }
        finally
        {
            if (Volatile.Read(ref inactive) == 0)
                SandboxLiveCounter.Decrement();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IncusSandboxOptions StopProbeOptions() => new()
    {
        StagingDirectory = Path.GetTempPath(),
        DiskGuard = null,
        OperationTimeout = TimeSpan.FromSeconds(30),
        VmStopTimeout = TimeSpan.FromSeconds(1),
        ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
    };

    private static IncusSandboxProvider StopProbeProvider(IProcessRunner runner) =>
        new(
            StopProbeOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

    private static IncusSandbox CreateTeardownSandbox(
        string sandboxName,
        string root,
        TeardownScriptRunner runner,
        Action<string> onDisposed,
        out RecoveryBinding binding)
    {
        var sandboxRoot = Path.Combine(root, sandboxName);
        Directory.CreateDirectory(sandboxRoot);
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, DateTimeOffset.UtcNow);
        var options = new IncusSandboxOptions
        {
            CaptureResourceMetrics = false,
            DiskGuard = null,
            OperationTimeout = TimeSpan.FromSeconds(30),
            VmStopTimeout = TimeSpan.FromMilliseconds(100),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
        };
        var spec = new SandboxSpec { ImageReference = "local-image" };
        var authorization = IncusRecoveryAuthorization.CaptureValidated(
            null,
            [],
            [],
            [],
            options);
        var token = $"test-recovery-token-{sandboxName}";
        var lease = new SandboxRecoveryLease(IncusSandboxProvider.ProviderId, sandboxName, token);
        var manifest = IncusRecoveryManifest.Create(
            sandboxName,
            spec,
            options,
            IncusRecoveryManifestCodec.ComputeTokenSha256(token),
            baselineRef: null,
            authorization);
        var store = IncusRecoveryManifestStore.Acquire(sandboxRoot);
        var manifestHash = store.Write(manifest, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        binding = new RecoveryBinding(
            IncusRecoveryManifestCodec.ComputeTokenSha256(token),
            manifestHash);
        return new IncusSandbox(
            sandboxName,
            sandboxRoot,
            root,
            spec,
            options,
            new IncusCliRunner(runner),
            NullLogger.Instance,
            timings: null,
            WorkItemId.New(),
            "work",
            baselineRef: null,
            resourceUsageStore: null,
            onDisposed,
            authorization,
            lease,
            manifest,
            store);
    }

    private sealed record RecoveryBinding(string TokenHash, string ManifestHash);

    private static string OwnedInstanceJson(string sandboxName, string status) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                name = sandboxName,
                type = "virtual-machine",
                status,
                config = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [IncusSandboxProvider.ManagedKey] = "true",
                    [IncusSandboxProvider.KindKey] = IncusSandboxProvider.SandboxKind,
                },
            },
        });

    private sealed class StopProbeRunner(
        string instanceName,
        string stopStderr,
        int stopExitCode,
        string? listStatus,
        bool listThrows = false) : IProcessRunner
    {
        internal int StopCalls { get; private set; }
        internal int ListCalls { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            ct.ThrowIfCancellationRequested();
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                StopCalls++;
                return Task.FromResult(new ProcessRunResult(stopExitCode, string.Empty, stopStderr));
            }
            if (argv.Contains("list", StringComparer.Ordinal))
            {
                ListCalls++;
                if (listThrows)
                    throw new InvalidOperationException("simulated instance-list probe failure");
                var stdout = listStatus is null
                    ? "[]"
                    : JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            name = instanceName,
                            type = "virtual-machine",
                            status = listStatus,
                            config = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [IncusSandboxProvider.ManagedKey] = "true",
                                [IncusSandboxProvider.KindKey] = IncusSandboxProvider.SandboxKind,
                            },
                        },
                    });
                return Task.FromResult(new ProcessRunResult(0, stdout, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected Incus stop-probe command: {string.Join(' ', argv)}");
        }
    }

    private sealed class TeardownScriptRunner(
        Func<ProcessRunResult> onConfigSet,
        Func<IReadOnlyList<string>, ProcessRunResult> onStop,
        Func<ProcessRunResult> onList,
        Func<IReadOnlyList<string>, ProcessRunResult> onDelete) : IProcessRunner
    {
        private string? _recoveryTokenHash;
        private string? _recoveryManifestHash;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

        internal void SetRecoveryBinding(string tokenHash, string manifestHash) =>
            (_recoveryTokenHash, _recoveryManifestHash) = (tokenHash, manifestHash);

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(argv.ToArray());
            if (argv.Contains("config", StringComparer.Ordinal) && argv.Contains("set", StringComparer.Ordinal))
                return Task.FromResult(onConfigSet());
            if (argv.Contains("stop", StringComparer.Ordinal))
                return Task.FromResult(onStop(argv));
            if (argv.Contains("list", StringComparer.Ordinal))
                return Task.FromResult(InjectRecoveryBinding(onList()));
            if (argv.Contains("delete", StringComparer.Ordinal))
                return Task.FromResult(onDelete(argv));
            throw new InvalidOperationException($"Unexpected Incus teardown command: {string.Join(' ', argv)}");
        }

        private ProcessRunResult InjectRecoveryBinding(ProcessRunResult result)
        {
            if (_recoveryTokenHash is null || _recoveryManifestHash is null || string.IsNullOrEmpty(result.Stdout))
                return result;
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            var rewritten = document.RootElement.EnumerateArray().Select(element =>
            {
                if (!element.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                    return element.GetRawText();
                var merged = config.EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => property.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal);
                merged[IncusSandboxProvider.RecoveryTokenHashKey] = _recoveryTokenHash;
                merged[IncusSandboxProvider.RecoveryManifestHashKey] = _recoveryManifestHash;
                return JsonSerializer.Serialize(new
                {
                    name = element.GetProperty("name").GetString(),
                    type = element.GetProperty("type").GetString(),
                    status = element.TryGetProperty("status", out var status) ? status.GetString() : null,
                    config = merged,
                });
            });
            return result with { Stdout = $"[{string.Join(",", rewritten)}]" };
        }
    }
}
