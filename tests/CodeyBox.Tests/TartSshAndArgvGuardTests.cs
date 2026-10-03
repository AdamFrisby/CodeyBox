using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the Tart provider's trust-boundary guards: guest
/// SSH server authentication (accept-new against a provider-owned
/// known_hosts file, never /dev/null) and flag-injection guards on the
/// <c>tart</c> CLI operands that arrive as raw argv (resume name, image).
/// </summary>
public sealed class TartSshAndArgvGuardTests
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

    private static TartSshGuestTransport NewTransport(
        FakeTartProcessRunner runner,
        TartSandboxOptions opts) =>
        new(runner, () => opts, () => opts.SshPassword ?? string.Empty);

    private static TartSandboxProvider NewProvider(
        FakeTartProcessRunner runner,
        TartSandboxOptions? opts = null) =>
        new(
            () => opts ?? TestOptions(),
            runner,
            () => true,
            TimeProvider.System,
            NullLogger.Instance);

    [Fact]
    public void BuildSshInvocation_PasswordAuth_VerifiesHostKeyAgainstProviderKnownHosts()
    {
        var runner = new FakeTartProcessRunner();
        var opts = TestOptions();
        var transport = NewTransport(runner, opts);

        var (_, argv, _) = transport.BuildSshInvocation("192.0.2.11", "echo codeybox-ready");

        Assert.Contains("StrictHostKeyChecking=accept-new", argv);
        Assert.DoesNotContain("StrictHostKeyChecking=no", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("UserKnownHostsFile=/dev/null", StringComparison.Ordinal));
        var knownHosts = Assert.Single(argv, a => a.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal));
        Assert.NotEqual("UserKnownHostsFile=/dev/null", knownHosts);
        Assert.Contains("GlobalKnownHostsFile=/dev/null", argv);
    }

    [Fact]
    public void BuildSshInvocation_KeyAuth_VerifiesHostKeyAgainstProviderKnownHosts()
    {
        var runner = new FakeTartProcessRunner();
        var keyPath = Path.Combine(Path.GetTempPath(), "tart-test-" + Guid.NewGuid().ToString("N"));
        var opts = TestOptions() with { SshPassword = null, SshPrivateKeyPath = keyPath };
        var transport = NewTransport(runner, opts);

        var (_, argv, _) = transport.BuildSshInvocation("192.0.2.11", "echo codeybox-ready");

        Assert.Contains("StrictHostKeyChecking=accept-new", argv);
        Assert.DoesNotContain("StrictHostKeyChecking=no", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("UserKnownHostsFile=/dev/null", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSshInvocation_DevNullKnownHosts_FailsClosed()
    {
        var runner = new FakeTartProcessRunner();
        var transport = NewTransport(runner, TestOptions() with { SshKnownHostsPath = "/dev/null" });

        Assert.Throws<InvalidOperationException>(() => transport.BuildSshInvocation("192.0.2.11", "echo hi"));
    }

    [Fact]
    public void BuildSshInvocation_BlankKnownHosts_FailsClosed()
    {
        var runner = new FakeTartProcessRunner();
        var transport = NewTransport(runner, TestOptions() with { SshKnownHostsPath = "  " });

        Assert.Throws<InvalidOperationException>(() => transport.BuildSshInvocation("192.0.2.11", "echo hi"));
    }

    [Fact]
    public void ResolveKnownHostsPath_ExpandsHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var resolved = TartSshGuestTransport.ResolveKnownHostsPath(
            TestOptions() with { SshKnownHostsPath = "~/.ssh/codeybox-tart-known_hosts" });

        Assert.Equal(Path.Combine(home, ".ssh/codeybox-tart-known_hosts"), resolved);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--upload")]
    public async Task ResumeSandboxAsync_FlagLikeName_RefusedBeforeAnyCliCall(string name)
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.ResumeSandboxAsync(name, CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ResumeSandboxAsync_ForeignNamespace_RefusedBeforeAnyCliCall()
    {
        var runner = new FakeTartProcessRunner();
        runner.SeedForeignVm("foreign-vm");
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ResumeSandboxAsync("foreign-vm", CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("--upload-registry")]
    [InlineData("-v")]
    public async Task CloneAsync_DashLedImage_RefusedBeforeAnyCliCall(string image)
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CloneAsync(TestOptions(), image, "codeybox-abc123", CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task CloneAsync_FlagLikeVmName_RefusedBeforeAnyCliCall()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CloneAsync(TestOptions(), TestOptions().DefaultImage, "--help", CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void GeneratedVmNames_PassOperandValidation()
    {
        for (var i = 0; i < 25; i++)
            Assert.True(TartSandboxProvider.IsValidVmName(TartSandboxProvider.BuildVmName("codeybox-")));
    }

    [Theory]
    [InlineData("codeybox-foo;bar")]
    [InlineData("codeybox-foo bar")]
    [InlineData("codeybox-foo/bar")]
    public async Task DisposeLeakedAsync_InvalidCharset_RefusedBeforeAnyCliCall(string name)
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.DisposeLeakedAsync(name, CancellationToken.None));

        Assert.Empty(runner.Invocations);
    }
}
