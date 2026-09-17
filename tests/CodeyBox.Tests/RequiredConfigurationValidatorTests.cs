using Microsoft.Extensions.Configuration;
using CodeyBox.Api;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the pre-build required-configuration gate. A released
/// binary run with no configuration must fail with a clean diagnostic
/// (messages on stderr, conventional non-zero exit, no stack trace, no core
/// dump) instead of an unhandled DI exception (exit 134). These tests pin the
/// validator behaviour directly; the release-tarball run is verified manually
/// and its exit code recorded in the commit message.
/// </summary>
public sealed class RequiredConfigurationValidatorTests
{
    [Fact]
    public void ConfigurationFailureExitCode_IsConventionalNonZero()
    {
        Assert.Equal(1, RequiredConfigurationValidator.ConfigurationFailureExitCode);
        Assert.NotEqual(134, RequiredConfigurationValidator.ConfigurationFailureExitCode);
    }

    [Fact]
    public void ProductionMissingSandboxProvider_ReportsDiagnostic()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Production"), _ => "test-secret-that-is-long-enough-0123456789");

        Assert.Contains(RequiredConfigurationValidator.MissingSandboxProviderMessage, failures);
        Assert.Contains("CodeyBox:SandboxProvider must be set", failures[0]);
        Assert.Contains("non-Development", failures[0]);
        Assert.Contains("incus, multipass, multipass-remote, sprites, bubblewrap, process", failures[0]);
    }

    [Fact]
    public void ProductionMissingEverything_ReportsAllInOnePass()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "process",
            ["CodeyBox:Changelog:Enabled"] = "true",
            ["CodeyBox:E2eExecution:PoolKind"] = "local",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Production"), _ => null);

        Assert.Equal(5, failures.Count);
        Assert.Contains(failures, f => f.Contains("UNSAFE outside Development"));
        Assert.Contains(failures, f => f.Contains("Untrusted workloads require a dedicated-kernel sandbox"));
        Assert.Contains(failures, f => f.Contains("CODEYBOX_API_KEY must be set"));
        Assert.Contains(failures, f => f.Contains("CodeyBox:Changelog:GitHubWebhookSecretEnvVar"));
        Assert.Contains(failures, f => f.Contains("CodeyBox:E2eExecution:PoolKind=local is development-only"));
    }

    [Fact]
    public void DevelopmentWithNothingSet_ReportsNoFailures()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
            ["CodeyBox:Changelog:Enabled"] = "true",
            ["CodeyBox:E2eExecution:PoolKind"] = "local",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Development"), _ => null);

        Assert.Empty(failures);
    }

    [Fact]
    public void FullyConfiguredProduction_ReportsNoFailures()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
            ["CodeyBox:SandboxProvider"] = "process",
            ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
            ["CodeyBox:WorkloadTrust"] = "Trusted",
            ["CodeyBox:AcknowledgeSharedKernelRisk"] = "true",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Production"), _ => null);

        Assert.Empty(failures);
    }

    [Fact]
    public void ProductionBubblewrapUntrusted_RequiresDedicatedKernel()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
            ["CodeyBox:SandboxProvider"] = "bubblewrap",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Production"), _ => null);

        Assert.Contains(
            "Untrusted workloads require a dedicated-kernel sandbox; provider 'bubblewrap' advertises SharedKernel isolation.",
            failures);
    }

    [Fact]
    public void ProductionBubblewrapTrusted_RequiresRiskAcknowledgement()
    {
        var withoutAck = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
            ["CodeyBox:SandboxProvider"] = "bubblewrap",
            ["CodeyBox:WorkloadTrust"] = "Trusted",
        });

        var failures = RequiredConfigurationValidator.Validate(
            withoutAck, new StubHostEnvironment("Production"), _ => null);

        Assert.Contains(
            "Trusted workloads using provider 'bubblewrap' (SharedKernel) require CodeyBox:AcknowledgeSharedKernelRisk=true.",
            failures);

        var withAck = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:DangerouslyDisableAuth"] = "true",
            ["CodeyBox:SandboxProvider"] = "bubblewrap",
            ["CodeyBox:WorkloadTrust"] = "Trusted",
            ["CodeyBox:AcknowledgeSharedKernelRisk"] = "true",
        });

        Assert.Empty(RequiredConfigurationValidator.Validate(
            withAck, new StubHostEnvironment("Production"), _ => null));
    }

    [Fact]
    public void ShortApiKey_IsReported()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var failures = RequiredConfigurationValidator.Validate(
            config, new StubHostEnvironment("Production"), _ => "short");

        Assert.Contains(failures, f => f.Contains("at least 32 characters of high-entropy random data"));
    }

    [Fact]
    public void MissingApiClientToken_IsReported()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:ApiClients:0:Name"] = "executor-1",
            ["CodeyBox:ApiClients:0:TokenEnvVar"] = "MISSING_CLIENT_TOKEN_ENV",
            ["CodeyBox:ApiClients:0:Principal:Issuer"] = "test",
            ["CodeyBox:ApiClients:0:Principal:Subject"] = "executor-1",
            ["CodeyBox:ApiClients:0:Principal:DisplayName"] = "executor-1",
        });

        var failures = RequiredConfigurationValidator.Validate(
            config,
            new StubHostEnvironment("Production"),
            name => name == ApiKeyAuth.EnvVarName ? "0123456789abcdef0123456789abcdef" : null);

        Assert.Contains(
            "MISSING_CLIENT_TOKEN_ENV must contain at least 32 characters of high-entropy random data.",
            failures);
    }

    [Fact]
    public void WriteFailures_EmitsMessagesWithoutStackTrace()
    {
        var failures = new[]
        {
            RequiredConfigurationValidator.MissingSandboxProviderMessage,
            RequiredConfigurationValidator.ApiKeyMissingMessage,
        };

        using var stderr = new StringWriter();
        RequiredConfigurationValidator.WriteFailures(
            stderr, new StubHostEnvironment("Production"), failures);
        var output = stderr.ToString();

        Assert.Contains("CodeyBox:SandboxProvider must be set", output);
        Assert.Contains("CODEYBOX_API_KEY must be set", output);
        Assert.Contains("Production", output);
        Assert.DoesNotContain("   at ", output);
        Assert.DoesNotContain("Exception", output);
        Assert.DoesNotContain("Unhandled", output);
    }

    [Fact]
    public void GateException_CarriesEveryFailureAndStaysInvalidOperation()
    {
        var failures = new[]
        {
            RequiredConfigurationValidator.MissingSandboxProviderMessage,
            RequiredConfigurationValidator.ApiKeyMissingMessage,
        };

        var ex = new RequiredConfigurationException("Production", failures);

        var invalid = Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("CodeyBox:SandboxProvider must be set", invalid.Message);
        Assert.Contains("CODEYBOX_API_KEY must be set", invalid.Message);
        Assert.Equal(2, ex.Failures.Count);
        Assert.Equal("Production", ex.EnvironmentName);
    }

    [Fact]
    public void UnhandledHandler_WritesDiagnosticAndExitsConventionally()
    {
        var failures = new[]
        {
            RequiredConfigurationValidator.MissingSandboxProviderMessage,
        };
        var gate = new RequiredConfigurationException("Production", failures);

        using var stderr = new StringWriter();
        var exitCode = -1;
        RequiredConfigurationValidator.OnUnhandledException(
            sender: null,
            e: new UnhandledExceptionEventArgs(gate, isTerminating: true),
            stderr: stderr,
            exit: code => exitCode = code);

        var output = stderr.ToString();
        Assert.Contains("CodeyBox:SandboxProvider must be set", output);
        Assert.DoesNotContain("   at ", output);
        Assert.Equal(1, exitCode);
        Assert.NotEqual(134, exitCode);
    }

    [Fact]
    public void UnhandledHandler_IgnoresGenuineCrashes()
    {
        using var stderr = new StringWriter();
        var exitCode = -1;
        RequiredConfigurationValidator.OnUnhandledException(
            sender: null,
            e: new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true),
            stderr: stderr,
            exit: code => exitCode = code);

        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(-1, exitCode);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
