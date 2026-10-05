using System.Diagnostics;
using System.Net;
using CodeyBox.Core;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Softnet guest-network mode for the Tart provider: exact <c>tart run</c>
/// argv for a given allowlist, gateway-only empty policy, bounded
/// deterministic resolution with skip-and-log, fail-closed preflight with no
/// NAT fallback, per-sandbox policy reporting, and NAT regression.
/// </summary>
public sealed class TartSoftnetTests
{
    private const string Gateway = "192.168.64.1/32";

    private static TartSandboxOptions SoftnetOptions(
        ITartDnsResolver? resolver = null,
        int maxAllowCidrs = 64,
        int dnsTimeoutSeconds = 5) => new()
    {
        Enabled = true,
        SshPassword = "test-password",
        ReadyTimeoutSeconds = 30,
        PollIntervalMilliseconds = 10,
        CliTimeoutSeconds = 5,
        SshConnectTimeoutSeconds = 2,
        Network = new TartSandboxNetworkOptions
        {
            Mode = TartNetworkMode.Softnet,
            GatewayCidr = Gateway,
            MaxAllowCidrs = maxAllowCidrs,
            DnsTimeoutSeconds = dnsTimeoutSeconds,
        },
    };

    private static TartSandboxOptions NatOptions() => new()
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
        TartSandboxOptions opts,
        ITartDnsResolver? dns = null,
        Func<TartSandboxOptions, CancellationToken, Task>? preflight = null,
        ILogger? logger = null) =>
        new(
            () => opts,
            runner,
            () => true,
            TimeProvider.System,
            logger ?? NullLogger.Instance,
            dns,
            preflight);

    private static SandboxSpec SpecWithHosts(params string[] hosts) => new()
    {
        ImageReference = "ghcr.io/cirruslabs/macos-sequoia-base:latest",
        WorkingDirectory = "/work",
        Network = new SandboxNetworkPolicy { AllowedHosts = hosts },
    };

    private static string RunAllowFlag(FakeTartProcessRunner runner, string vmName)
    {
        var run = Assert.Single(
            runner.Invocations,
            i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run" && i.Argv[^1] == vmName);
        return Assert.Single(run.Argv, a => a.StartsWith(TartSoftnetPolicy.AllowFlagPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SoftnetMode_RunArgv_ContainsExactAllowList()
    {
        var runner = new FakeTartProcessRunner();
        var dns = new TableDnsResolver(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.example.com"] = [IPAddress.Parse("93.184.216.35"), IPAddress.Parse("93.184.216.36")],
        });
        var provider = NewProvider(runner, SoftnetOptions(), dns, (_, _) => Task.CompletedTask);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("api.example.com", "93.184.216.34", "  ", "API.EXAMPLE.COM"),
            CancellationToken.None);

        var run = Assert.Single(
            runner.Invocations,
            i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");
        Assert.Equal("run", run.Argv[0]);
        Assert.Equal(sandbox.Id, run.Argv[^1]);
        Assert.Contains(TartSoftnetPolicy.NetSoftnetFlag, run.Argv, StringComparer.Ordinal);
        Assert.Contains($"{TartSoftnetPolicy.BlockFlagPrefix}{TartSoftnetPolicy.BlockAllCidr}", run.Argv, StringComparer.Ordinal);
        var allow = Assert.Single(run.Argv, a => a.StartsWith(TartSoftnetPolicy.AllowFlagPrefix, StringComparison.Ordinal));
        Assert.Equal(
            $"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway},93.184.216.34/32,93.184.216.35/32,93.184.216.36/32",
            allow);
        foreach (var arg in run.Argv)
            Assert.DoesNotContain("@host", arg, StringComparison.Ordinal);
        Assert.DoesNotContain(TartSoftnetPolicy.BlockAllCidr, allow, StringComparison.Ordinal);
        Assert.Contains("--no-graphics", run.Argv, StringComparer.Ordinal);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task SoftnetMode_EmptyAllowedHosts_AllowsGatewayOnly()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver(), (_, _) => Task.CompletedTask);

        await using var sandbox = await provider.CreateAsync(SpecWithHosts(), CancellationToken.None);

        Assert.Equal($"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway}", RunAllowFlag(runner, sandbox.Id));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task UnresolvableHosts_SkippedAndLogged()
    {
        var runner = new FakeTartProcessRunner();
        var logger = new CapturingLogger();
        var dns = new TableDnsResolver(failHosts: ["gone.example"]);
        var provider = NewProvider(runner, SoftnetOptions(), dns, (_, _) => Task.CompletedTask, logger);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("gone.example"), CancellationToken.None);

        Assert.Equal($"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway}", RunAllowFlag(runner, sandbox.Id));
        Assert.Contains(logger.Messages, m => m.Contains("gone.example", StringComparison.Ordinal));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task HostnameResolution_IsBoundedAndDeterministic()
    {
        var runner = new FakeTartProcessRunner();
        var dns = new TableDnsResolver(
            new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.example.com"] = [IPAddress.Parse("93.184.216.35")],
            },
            delay: TimeSpan.FromSeconds(10),
            delayHosts: ["slow.example"]);
        var provider = NewProvider(runner, SoftnetOptions(dnsTimeoutSeconds: 1), dns, (_, _) => Task.CompletedTask);
        var spec = SpecWithHosts("api.example.com", "slow.example");

        var started = Stopwatch.GetTimestamp();
        await using var first = await provider.CreateAsync(spec, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromSeconds(9), $"Resolution took {elapsed}: DNS was not bounded.");
        var firstAllow = RunAllowFlag(runner, first.Id);
        Assert.Equal($"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway},93.184.216.35/32", firstAllow);
        await first.DisposeAsync();

        await using var second = await provider.CreateAsync(spec, CancellationToken.None);
        Assert.Equal(firstAllow, RunAllowFlag(runner, second.Id));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task MisconfiguredSoftnet_RefusesCreationWithTypedError_NoNatFallback()
    {
        var runner = new FakeTartProcessRunner();
        // No preflight override: the real probe runs `softnet --version`,
        // which the fake answers with 127 (binary not installed).
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver());

        var ex = await Assert.ThrowsAsync<TartSoftnetUnavailableException>(
            () => provider.CreateAsync(SpecWithHosts("api.example.com"), CancellationToken.None));

        Assert.Equal("softnet-missing", ex.ErrorClass);
        Assert.Contains("setuid", ex.SetupStep, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.EnteredClones);
        Assert.DoesNotContain(runner.Invocations, i => i.Argv.Count > 0 && i.Argv[0] == "run");
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task DeniedSoftnet_RefusesCreationWithTypedError_NoNatFallback()
    {
        var runner = new FakeTartProcessRunner();
        Func<TartSandboxOptions, CancellationToken, Task> denied = (_, _) =>
            throw new TartSoftnetUnavailableException(
                "softnet-denied", "Grant Softnet privilege via its setuid bit.", "permission denied");
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver(), denied);

        await Assert.ThrowsAsync<TartSoftnetUnavailableException>(
            () => provider.CreateAsync(SpecWithHosts(), CancellationToken.None));

        Assert.Equal(0, runner.EnteredClones);
        Assert.DoesNotContain(runner.Invocations, i => i.Argv.Count > 0 && i.Argv[0] == "run");
    }

    [Fact]
    public async Task NonGlobalUnicastAddresses_AreSkippedAndLogged()
    {
        var runner = new FakeTartProcessRunner();
        var logger = new CapturingLogger();
        var dns = new TableDnsResolver(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mixed.example.com"] = [
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("10.0.0.5"),
                IPAddress.Parse("172.16.9.9"),
                IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("169.254.169.254"),
                IPAddress.Parse("100.64.0.1"),
                IPAddress.Parse("224.0.0.1"),
                IPAddress.Parse("0.0.0.0"),
                IPAddress.Parse("93.184.216.35"),
            ],
        });
        var provider = NewProvider(runner, SoftnetOptions(), dns, (_, _) => Task.CompletedTask, logger);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("mixed.example.com"), CancellationToken.None);

        Assert.Equal(
            $"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway},93.184.216.35/32",
            RunAllowFlag(runner, sandbox.Id));
        Assert.Contains(logger.Messages, m => m.Contains("127.0.0.1", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("mixed.example.com", StringComparison.Ordinal));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task LiteralPrivateIpHost_IsSkipped_GatewayOnly()
    {
        var runner = new FakeTartProcessRunner();
        var logger = new CapturingLogger();
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver(), (_, _) => Task.CompletedTask, logger);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("127.0.0.1", "10.1.2.3"), CancellationToken.None);

        Assert.Equal($"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway}", RunAllowFlag(runner, sandbox.Id));
        Assert.Contains(logger.Messages, m => m.Contains("127.0.0.1", StringComparison.Ordinal));
        await sandbox.DisposeAsync();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    public void IsGlobalUnicastIPv4_RejectsNonRoutable(string address)
    {
        Assert.False(TartSoftnetPolicy.IsGlobalUnicastIPv4(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("142.250.80.14")]
    [InlineData("8.8.8.8")]
    public void IsGlobalUnicastIPv4_AcceptsPublic(string address)
    {
        Assert.True(TartSoftnetPolicy.IsGlobalUnicastIPv4(IPAddress.Parse(address)));
    }

    [Fact]
    public void BuildAllowCidrs_DropsNonGlobal_DefenseInDepth()
    {
        var allow = TartSoftnetPolicy.BuildAllowCidrs(
            [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("93.184.216.34")],
            Gateway,
            64);

        Assert.Equal([Gateway, "93.184.216.34/32"], allow);
    }

    [Fact]
    public async Task NatMode_RunArgv_Unchanged_Regression()
    {
        var runner = new FakeTartProcessRunner();
        var probed = false;
        Func<TartSandboxOptions, CancellationToken, Task> failIfProbed = (_, _) =>
        {
            probed = true;
            throw new InvalidOperationException("NAT mode must not probe Softnet.");
        };
        var provider = NewProvider(runner, NatOptions(), new TableDnsResolver(), failIfProbed);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("api.example.com"), CancellationToken.None);

        var run = Assert.Single(
            runner.Invocations,
            i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");
        Assert.Equal(["run", "--no-graphics", sandbox.Id], run.Argv);
        Assert.DoesNotContain(run.Argv, a => a.StartsWith("--net-softnet", StringComparison.Ordinal));
        Assert.False(probed);
        var report = Assert.IsAssignableFrom<ITartSoftnetPolicyReport>(sandbox);
        Assert.Equal(TartNetworkMode.Nat, report.NetworkMode);
        Assert.Empty(report.EffectiveAllowCidrs);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task EffectivePolicy_ReportedOnSandbox()
    {
        var runner = new FakeTartProcessRunner();
        var dns = new TableDnsResolver(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.example.com"] = [IPAddress.Parse("93.184.216.35")],
        });
        var provider = NewProvider(runner, SoftnetOptions(), dns, (_, _) => Task.CompletedTask);

        await using var sandbox = await provider.CreateAsync(
            SpecWithHosts("api.example.com"), CancellationToken.None);

        var report = Assert.IsAssignableFrom<ITartSoftnetPolicyReport>(sandbox);
        Assert.Equal(TartNetworkMode.Softnet, report.NetworkMode);
        Assert.Equal(
            [$"{Gateway}", "93.184.216.35/32"],
            report.EffectiveAllowCidrs);
        Assert.Equal(
            $"{TartSoftnetPolicy.AllowFlagPrefix}{string.Join(",", report.EffectiveAllowCidrs)}",
            RunAllowFlag(runner, sandbox.Id));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Resume_ReinstallsStoredSoftnetPolicy()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver(), (_, _) => Task.CompletedTask);

        await using var sandbox = await provider.CreateAsync(SpecWithHosts(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        await tart.StopAndPreserveAsync(CancellationToken.None);
        var runsBefore = runner.Invocations.Count(i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");

        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        var resumeRun = runner.Invocations
            .Where(i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run")
            .Skip(runsBefore)
            .Single();
        Assert.Contains(TartSoftnetPolicy.NetSoftnetFlag, resumeRun.Argv, StringComparer.Ordinal);
        Assert.Contains($"{TartSoftnetPolicy.AllowFlagPrefix}{Gateway}", resumeRun.Argv, StringComparer.Ordinal);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task Resume_UnknownPolicy_FailsClosed()
    {
        var runner = new FakeTartProcessRunner();
        runner.SeedForeignVm("codeybox-old", running: false);
        var provider = NewProvider(runner, SoftnetOptions(), new TableDnsResolver(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TartSoftnetUnavailableException>(
            () => provider.ResumeSandboxAsync("codeybox-old", CancellationToken.None));

        Assert.Equal("softnet-policy-unknown", ex.ErrorClass);
        Assert.DoesNotContain(runner.Invocations, i => i.Argv.Count > 0 && i.Argv[0] == "run");
    }

    [Fact]
    public async Task CidrBound_Exceeded_FailsClosedBeforeClone()
    {
        var runner = new FakeTartProcessRunner();
        var dns = new TableDnsResolver(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.example.com"] = [IPAddress.Parse("93.184.216.35")],
            ["b.example.com"] = [IPAddress.Parse("93.184.216.36")],
        });
        var provider = NewProvider(runner, SoftnetOptions(maxAllowCidrs: 1), dns, (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreateAsync(SpecWithHosts("a.example.com", "b.example.com"), CancellationToken.None));

        Assert.Contains("MaxAllowCidrs", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.EnteredClones);
    }

    [Fact]
    public void FromConfiguration_BindsNetworkSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["t:Network:Mode"] = "softnet",
                ["t:Network:GatewayCidr"] = "192.168.71.1/32",
                ["t:Network:MaxAllowCidrs"] = "32",
                ["t:Network:DnsTimeoutSeconds"] = "3",
                ["t:Network:SoftnetBinaryPath"] = "/usr/local/bin/softnet",
            })
            .Build();
        var opts = TartSandboxOptions.FromConfiguration(config.GetSection("t"));

        Assert.Equal(TartNetworkMode.Softnet, opts.Network.Mode);
        Assert.Equal("192.168.71.1/32", opts.Network.GatewayCidr);
        Assert.Equal(32, opts.Network.MaxAllowCidrs);
        Assert.Equal(3, opts.Network.DnsTimeoutSeconds);
        Assert.Equal("/usr/local/bin/softnet", opts.Network.SoftnetBinaryPath);
    }

    [Fact]
    public void FromConfiguration_DefaultsToNat()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["t:Enabled"] = "true" })
            .Build();
        var opts = TartSandboxOptions.FromConfiguration(config.GetSection("t"));

        Assert.Equal(TartNetworkMode.Nat, opts.Network.Mode);
        Assert.Equal(TartSandboxNetworkOptions.DefaultGatewayCidr, opts.Network.GatewayCidr);
    }

    [Theory]
    [InlineData("SOFTNET")]
    [InlineData(" Softnet ")]
    public void FromConfiguration_ModeParsing_IsCaseInsensitiveAndTrimmed(string mode)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["t:Network:Mode"] = mode })
            .Build();

        Assert.Equal(TartNetworkMode.Softnet, TartSandboxOptions.FromConfiguration(config.GetSection("t")).Network.Mode);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("soft-net")]
    public void FromConfiguration_UnknownMode_FailsClosed(string mode)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["t:Network:Mode"] = mode })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => TartSandboxOptions.FromConfiguration(config.GetSection("t")));
        Assert.Contains("nat", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("softnet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromConfiguration_BadGatewayCidr_FailsClosed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["t:Network:GatewayCidr"] = "@host" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => TartSandboxOptions.FromConfiguration(config.GetSection("t")));
    }

    private sealed class TableDnsResolver(
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>? table = null,
        string[]? failHosts = null,
        TimeSpan? delay = null,
        string[]? delayHosts = null) : ITartDnsResolver
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> _table =
            table ?? new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fail = new(failHosts ?? [], StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _delayHosts = new(delayHosts ?? [], StringComparer.OrdinalIgnoreCase);

        public async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host, CancellationToken ct)
        {
            if (delay.HasValue && _delayHosts.Contains(host))
                await Task.Delay(delay.Value, ct).ConfigureAwait(false);
            if (_fail.Contains(host))
                throw new InvalidOperationException($"DNS NXDOMAIN for {host}");
            if (IPAddress.TryParse(host, out var literal))
                return literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? [literal]
                    : [];
            return _table.TryGetValue(host, out var addresses) ? addresses : [];
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _sync = new();
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
                Messages.Add($"{logLevel}: {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
