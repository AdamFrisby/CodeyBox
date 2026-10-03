using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Failure classification, credential chain, capacity/load, suspend/resume,
/// file round-trips, and pure shell/path unit coverage for the Tart provider.
/// </summary>
public sealed class TartSandboxProviderExtendedTests
{
    private static TartSandboxOptions TestOptions() => new()
    {
        Enabled = true,
        SshPassword = "test-password",
        ReadyTimeoutSeconds = 30,
        PollIntervalMilliseconds = 10,
        CliTimeoutSeconds = 5,
        SshConnectTimeoutSeconds = 2,
    };

    private static TartSandboxProvider NewProvider(
        FakeTartProcessRunner runner,
        TartSandboxOptions? opts = null,
        Func<bool>? isMacOS = null) =>
        NewProvider((ITartProcessRunner)runner, opts, isMacOS);

    private static TartSandboxProvider NewProvider(
        ITartProcessRunner runner,
        TartSandboxOptions? opts = null,
        Func<bool>? isMacOS = null) =>
        new(
            () => opts ?? TestOptions(),
            runner,
            isMacOS ?? (() => true),
            TimeProvider.System,
            NullLogger.Instance);

    private static SandboxSpec BasicSpec() => new()
    {
        ImageReference = "ghcr.io/cirruslabs/macos-sequoia-base:latest",
        WorkingDirectory = "/work",
    };

    [Fact]
    public async Task SuspendAsync_PreservesVm_AcrossDispose()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await suspendable.SuspendAsync(CancellationToken.None);

        Assert.Contains(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["stop", _]);
        var stopsBeforeDispose = runner.Invocations.Count(i => i.Executable == "tart" && i.Argv is ["stop", _]);
        await sandbox.DisposeAsync();

        Assert.DoesNotContain(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["delete", _]);
        Assert.Equal(stopsBeforeDispose, runner.Invocations.Count(i => i.Executable == "tart" && i.Argv is ["stop", _]));
        Assert.True(runner.VmExists(sandbox.Id));
    }

    [Fact]
    public async Task StopAndPreserveAsync_PreservesVm_WithoutSuspendFlag()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        await tart.StopAndPreserveAsync(CancellationToken.None);
        await sandbox.DisposeAsync();

        Assert.DoesNotContain(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["delete", _]);
        Assert.True(runner.VmExists(sandbox.Id));
    }

    [Fact]
    public async Task ResumeSandboxAsync_StoppedVm_RunsToReady()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        await tart.StopAndPreserveAsync(CancellationToken.None);
        Assert.False(runner.VmRunning(sandbox.Id));

        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);
        Assert.True(runner.VmRunning(sandbox.Id));
    }

    [Fact]
    public async Task ResumeSandboxAsync_MissingVm_IsNonFatal()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await provider.ResumeSandboxAsync("codeybox-gone", CancellationToken.None);
        Assert.DoesNotContain(runner.Invocations, i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");
    }

    [Fact]
    public async Task ResumeSandboxAsync_RunningVm_IsNoop()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var runsBefore = runner.Invocations.Count(i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);
        Assert.Equal(runsBefore, runner.Invocations.Count(i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run"));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WriteAndReadFile_RoundTrips()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        await tart.WriteFileAsync("/work/note.txt", "file-contents", CancellationToken.None);
        Assert.Equal("file-contents", await tart.ReadFileAsync("/work/note.txt", CancellationToken.None));

        var write = runner.Invocations.First(i =>
            (i.Executable == "ssh" || i.Executable == "sshpass") && i.Argv[^1].StartsWith("umask 077", StringComparison.Ordinal));
        Assert.True(write.StdinBytes > 0);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task WritableMount_SyncsBackOnDispose()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, "seed.txt"), "seed");
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = hostDir, ReadOnly = false }],
            };

            await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
            var tart = Assert.IsType<TartSandbox>(sandbox);
            await tart.WriteFileAsync("/data/result.txt", "agent-output", CancellationToken.None);
            await sandbox.DisposeAsync();

            Assert.Equal("agent-output", await File.ReadAllTextAsync(Path.Combine(hostDir, "result.txt")));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public void SyncBack_EscapeAttempt_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => TartGuestPath.ContainHostPath("/tmp/owner", "../escape.txt"));
        Assert.Throws<InvalidOperationException>(() => TartGuestPath.ContainHostPath("/tmp/owner", "sub/../../escape.txt"));
        var contained = TartGuestPath.ContainHostPath("/tmp/owner", "sub/ok.txt");
        Assert.Equal(Path.GetFullPath("/tmp/owner/sub/ok.txt"), contained);
    }

    [Fact]
    public async Task CreateAsync_CloneUnauthorised_DefersAsInfrastructure()
    {
        var runner = new FakeTartProcessRunner { FailCloneStderr = "Error: unauthorized: authentication required", FailCloneExit = 1 };
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("tart", ex.Provider);
        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_CloneConflict_DefersAsInfrastructure()
    {
        var runner = new FakeTartProcessRunner { FailCloneStderr = "VM \"x\" already exists", FailCloneExit = 1 };
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("conflict", ex.ErrorClass);
    }

    [Fact]
    public async Task CreateAsync_CloneThrottled_DefersWithLongerBackoff()
    {
        var runner = new FakeTartProcessRunner { FailCloneStderr = "Error: 429 too many requests", FailCloneExit = 1 };
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("throttled", ex.ErrorClass);
        Assert.True(ex.RecheckIn >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task CreateAsync_MissingBinary_DefersAsUnreachable()
    {
        var throwing = new ThrowingRunner();
        var provider = NewProvider(throwing);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unreachable", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_ProbeAuthFailure_FailsFastAsUnauthorised()
    {
        var runner = new FakeTartProcessRunner { FailProbeAuth = true };
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ExecAsync_SuccessPath_ReturnsGuestOutput()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = _ => (0, "ok", string.Empty);
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        Assert.Equal("ok", (await tart.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None)).Stdout);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public void FailureClassification_ServiceFailures_AreInfrastructure()
    {
        Assert.True(TartFailureClassification.IsInfrastructure(
            new TartCliException("tart", ["clone"], 1, "clone-failed", "no")));
        Assert.True(TartFailureClassification.IsInfrastructure(new SocketException()));
        Assert.True(TartFailureClassification.IsInfrastructure(new TimeoutException()));
        Assert.True(TartFailureClassification.IsInfrastructure(new HttpRequestException("down")));
        Assert.False(TartFailureClassification.IsInfrastructure(new InvalidOperationException("guest said no")));

        Assert.Equal("unreachable", TartFailureClassification.Classify(127, "op", string.Empty).ErrorClass);
        Assert.Equal("unreachable", TartFailureClassification.Classify(255, "op", string.Empty).ErrorClass);
        Assert.Equal("unreachable", TartFailureClassification.Classify(null, "op", string.Empty).ErrorClass);
        Assert.Equal("unauthorised", TartFailureClassification.Classify(1, "op", "Permission denied (publickey)").ErrorClass);
        Assert.Equal("throttled", TartFailureClassification.Classify(1, "op", "429 too many requests").ErrorClass);
        Assert.Equal("conflict", TartFailureClassification.Classify(1, "op", "already exists").ErrorClass);
        Assert.Equal("not-found", TartFailureClassification.Classify(1, "op", "VM not found").ErrorClass);
        Assert.Equal("request-rejected", TartFailureClassification.Classify(1, "op", "some other failure").ErrorClass);
    }

    [Fact]
    public void ResolvePassword_MissingEnvironment_FailsClosedNamingVariable()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);
        var opts = TestOptions() with { SshPassword = null, SshPasswordEnvVar = "CODEYBOX_TART_TEST_MISSING" };
        Environment.SetEnvironmentVariable("CODEYBOX_TART_TEST_MISSING", null);

        var ex = Assert.Throws<InvalidOperationException>(() => TartCredentialChain.RequireSshPassword(opts, Environment.GetEnvironmentVariable));
        Assert.Contains("CODEYBOX_TART_TEST_MISSING", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePassword_TestInjection_WinsOverEnvironment()
    {
        const string variable = "CODEYBOX_TART_TEST_PW";
        Environment.SetEnvironmentVariable(variable, "env-value");
        try
        {
            var opts = TestOptions() with { SshPassword = "injected", SshPasswordEnvVar = variable };
            Assert.Equal("injected", TartCredentialChain.RequireSshPassword(opts, Environment.GetEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void FromConfiguration_NeverReadsSecrets()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["x:SshPassword"] = "should-be-ignored",
                ["x:SshPasswordEnvVar"] = "TART_SSH_PASSWORD",
                ["x:Enabled"] = "true",
            })
            .Build();
        var opts = TartSandboxOptions.FromConfiguration(config.GetSection("x"));
        Assert.True(opts.Enabled);
        Assert.Null(opts.SshPassword);
    }

    [Fact]
    public async Task KeyAuth_UsesKey_NotPassword()
    {
        var runner = new FakeTartProcessRunner();
        var keyPath = Path.GetTempFileName();
        try
        {
            var provider = NewProvider(runner, TestOptions() with { SshPassword = null, SshPrivateKeyPath = keyPath });
            await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);

            var ssh = runner.Invocations.First(i => i.Executable == "ssh");
            Assert.Contains("-i", ssh.Argv, StringComparer.Ordinal);
            Assert.Contains(keyPath, ssh.Argv, StringComparer.Ordinal);
            Assert.DoesNotContain(runner.Invocations, i => i.Executable == "sshpass");
            foreach (var invocation in runner.Invocations)
                Assert.False(invocation.EnvironmentKeys.ContainsKey("SSHPASS"));
            await sandbox.DisposeAsync();
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task SetupCommands_RunBeforeStaging()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner, TestOptions() with { SetupCommands = ["echo setup-done"] });

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);

        var setupIndex = -1;
        for (var i = 0; i < runner.Invocations.Count; i++)
        {
            var invocation = runner.Invocations[i];
            if ((invocation.Executable == "ssh" || invocation.Executable == "sshpass")
                && invocation.Argv[^1].Contains("setup-done", StringComparison.Ordinal))
            {
                setupIndex = i;
                break;
            }
        }
        Assert.True(setupIndex >= 0);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task SetupCommandFailure_DefersAsInfrastructure()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = cmd => cmd.Contains("setup-done", StringComparison.Ordinal) ? (2, string.Empty, "boom") : (0, string.Empty, string.Empty);
        var provider = NewProvider(runner, TestOptions() with { SetupCommands = ["echo setup-done"] });

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("setup-failed", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_OversizedStageFile_RefusedNamingLimit()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner, TestOptions() with { MaxStageFileBytes = 10 });

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, "big.bin"), new string('z', 100));
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = hostDir }],
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
            Assert.Contains("10-byte bound", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, provider.ActiveSandboxCount);
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentAcquisitions_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var runner = new FakeTartProcessRunner { GateClones = true };
        var provider = NewProvider(runner);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-a", "tart", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("tart-b", "tart", preferenceScore: 100, capacity: 5)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        Assert.Equal(8, acquirer.MaxConcurrent);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                CancellationToken.None))
            .ToList();

        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (runner.EnteredClones < 8)
        {
            await Task.Delay(10, enteredCts.Token);
        }

        Assert.Equal(8, runner.MaxConcurrentClones);
        runner.ReleaseClones();

        var sandboxes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(8, sandboxes.Length);
        Assert.Equal(8, acquirer.InFlight);
        Assert.Equal(8, provider.ActiveSandboxCount);

        using var overflowCts = new CancellationTokenSource();
        var overflow = Enumerable.Range(0, 2)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                overflowCts.Token))
            .ToList();
        await Task.Delay(500, CancellationToken.None);
        Assert.Equal(8, runner.MaxConcurrentClones);
        overflowCts.Cancel();
        foreach (var pending in overflow)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        foreach (var sandbox in sandboxes)
        {
            await sandbox.DisposeAsync();
        }

        Assert.Equal(0, acquirer.InFlight);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task FullMember_SpillsOverToLeastLoadedMember()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-a", "tart", preferenceScore: 100, capacity: 1),
                SandboxPlacementTestMembers.Member("tart-b", "tart", preferenceScore: 100, capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None);

        await using var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersToOwnedVms()
    {
        var runner = new FakeTartProcessRunner();
        runner.SeedForeignVm("someone-else");
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var entry = Assert.Single(managed);
        Assert.Equal(sandbox.Id, entry.Name);
        Assert.True(entry.IsTrackedActive);
        Assert.False(entry.IsSuspendLifecycleOrFrozen);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task DisposeLeakedAsync_RemovesVm_AndRefusesForeignNames()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var name = sandbox.Id;
        await sandbox.DisposeAsync();
        Assert.False(runner.VmExists(name));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DisposeLeakedAsync("someone-else", CancellationToken.None));
    }

    [Fact]
    public void ShellCommand_BuildsQuotedCommand()
    {
        var command = TartShellCommand.Build(
            new Dictionary<string, string> { ["BASE"] = "1", ["DROP"] = "x" },
            new SandboxExec
            {
                Argv = ["sh", "-c", "echo hi"],
                ExtraEnvironment = new Dictionary<string, string> { ["GREETING"] = "a'b\"c$d" },
                EnvironmentVariablesToUnset = ["DROP"],
            },
            "/work",
            maxEnvironmentBytes: 1024,
            maxCommandBytes: 4096);

        Assert.Contains("mkdir -p -- '/work'", command, StringComparison.Ordinal);
        Assert.Contains("unset -- 'DROP'", command, StringComparison.Ordinal);
        Assert.Contains("'a'\\''b\"c$d'", command, StringComparison.Ordinal);
        Assert.Contains("'sh' '-c' 'echo hi'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCommand_RejectsOversizedPayloads()
    {
        Assert.Throws<InvalidOperationException>(() => TartShellCommand.Build(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = ["true"], ExtraEnvironment = new Dictionary<string, string> { ["BIG"] = new string('v', 100) } },
            "/work",
            maxEnvironmentBytes: 10,
            maxCommandBytes: 4096));

        Assert.Throws<InvalidOperationException>(() => TartShellCommand.Build(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = [new string('a', 100)] },
            "/work",
            maxEnvironmentBytes: 4096,
            maxCommandBytes: 10));
    }

    [Fact]
    public void ShellCommand_WrapsStatusFile()
    {
        var wrapped = TartShellCommand.WrapWithStatusFile("exec 'true'", "/tmp/.codeybox-exec-status/st-1");
        Assert.Contains("__cb_code=$?", wrapped, StringComparison.Ordinal);
        Assert.Contains("'/tmp/.codeybox-exec-status/st-1'", wrapped, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => TartShellCommand.WrapWithStatusFile("exec 'true'", "relative/path"));
    }

    [Fact]
    public void ShellCommand_SecretEnvFile_ExportsValidatedNames()
    {
        var file = TartShellCommand.BuildSecretEnvFile(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = ["true"], ExtraEnvironment = new Dictionary<string, string> { ["SECRET"] = "v'alue" } },
            maxEnvironmentBytes: 1024);
        Assert.Contains("export 'SECRET'='v'\\''alue'", file, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TartShellCommand.BuildSecretEnvFile(
            new Dictionary<string, string>(),
            new SandboxExec { Argv = ["true"], ExtraEnvironment = new Dictionary<string, string> { ["BAD NAME"] = "x" } },
            maxEnvironmentBytes: 1024));
    }

    [Fact]
    public void GuestPath_Validation()
    {
        TartGuestPath.ValidateAbsolute("/work/a", "p");
        Assert.Throws<ArgumentException>(() => TartGuestPath.ValidateAbsolute("relative", "p"));
        Assert.Throws<ArgumentException>(() => TartGuestPath.ValidateAbsolute("/a/../b", "p"));
        Assert.Throws<ArgumentException>(() => TartGuestPath.ValidateAbsolute("/a\0b", "p"));
        Assert.Throws<ArgumentException>(() => TartShellCommand.BuildReadFileCommand("relative"));
        Assert.Equal("/work/sub/file", TartGuestPath.Join("/work", "sub/file"));
        Assert.Throws<ArgumentException>(() => TartGuestPath.Join("/work", "/absolute"));
        Assert.Throws<ArgumentException>(() => TartGuestPath.Join("/work", "../escape"));
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_StagesFile_NeverInlinesValues()
    {
        const string secret = "s3cr3t-v4lue";
        var secretBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = command =>
        {
            Assert.DoesNotContain(secret, command, StringComparison.Ordinal);
            Assert.DoesNotContain(secretBase64, command, StringComparison.Ordinal);
            return (0, "ok", string.Empty);
        };
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "CODEYBOX_TEST_SECRET"],
            ExtraEnvironment = new Dictionary<string, string> { ["CODEYBOX_TEST_SECRET"] = secret },
            EnvironmentContainsSecrets = true,
        }, CancellationToken.None);

        Assert.True(result.Success, result.Stderr);

        var staged = runner.Invocations.First(i =>
            (i.Executable == "ssh" || i.Executable == "sshpass") && i.Argv[^1].StartsWith("umask 077", StringComparison.Ordinal));
        Assert.True(staged.StdinBytes > 0);

        var userExec = runner.Invocations.First(i =>
            (i.Executable == "ssh" || i.Executable == "sshpass") && i.Argv[^1].Contains("__cb_code", StringComparison.Ordinal));
        var sentCommand = userExec.Argv[^1];
        Assert.Contains(". '/tmp/.codeybox-exec-env/env-", sentCommand, StringComparison.Ordinal);
        Assert.Contains("rm -f -- '/tmp/.codeybox-exec-env/env-", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'CODEYBOX_TEST_SECRET'", sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBase64, sentCommand, StringComparison.Ordinal);
        foreach (var invocation in runner.Invocations)
        {
            foreach (var arg in invocation.Argv)
            {
                Assert.DoesNotContain(secret, arg, StringComparison.Ordinal);
                Assert.DoesNotContain(secretBase64, arg, StringComparison.Ordinal);
            }
        }
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_StagingFailure_NeverFallsBackToInline()
    {
        var runner = new FakeTartProcessRunner { FailWrites = true };
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
                ExtraEnvironment = new Dictionary<string, string> { ["CODEYBOX_TEST_SECRET"] = "s3cr3t-v4lue" },
                EnvironmentContainsSecrets = true,
            }, CancellationToken.None));

        Assert.DoesNotContain(runner.Invocations, i =>
            (i.Executable == "ssh" || i.Executable == "sshpass") && i.Argv[^1].Contains("__cb_code", StringComparison.Ordinal));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public void Options_BadConfig_FallsBackToDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tart-test:SshPort"] = "not-a-number",
                ["tart-test:DefaultCpuCount"] = "999",
                ["tart-test:NamePrefix"] = "",
            })
            .Build();
        var opts = TartSandboxOptions.FromConfiguration(config.GetSection("tart-test"));
        Assert.Equal(22, opts.SshPort);
        Assert.Equal(32, opts.DefaultCpuCount);
        Assert.Equal("codeybox-", opts.NamePrefix);
    }

    private sealed class ThrowingRunner : ITartProcessRunner
    {
        public Task<TartProcessResult> RunAsync(
            TartProcessSpec spec,
            Action<string>? stdoutChunk,
            Action<string>? stderrChunk,
            int maxOutputBytes,
            CancellationToken ct)
        {
            _ = stdoutChunk;
            _ = stderrChunk;
            _ = maxOutputBytes;
            _ = ct;
            throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", "tart: command not found");
        }

        public ITartDetachedProcess StartDetached(TartProcessSpec spec) =>
            throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", "tart: command not found");
    }
}
