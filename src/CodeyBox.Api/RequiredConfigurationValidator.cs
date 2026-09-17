using System.Globalization;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Api;

/// <summary>
/// Thrown by the startup required-configuration gate when one or more
/// settings mandatory in the current environment are missing or invalid.
/// Carries every failure in <see cref="Failures"/> and renders them in
/// <see cref="Message"/> so test hosts observe the same diagnostic the DI
/// guards would raise. The real binary converts this exact type to a clean
/// stderr diagnostic with a conventional non-zero exit code (see
/// <see cref="RequiredConfigurationValidator.OnUnhandledException"/>);
/// it must never be caught and swallowed anywhere else.
/// </summary>
internal sealed class RequiredConfigurationException : InvalidOperationException
{
    public RequiredConfigurationException(string environmentName, IReadOnlyList<string> failures)
        : base(RequiredConfigurationValidator.FormatFailures(environmentName, failures))
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(failures);
        EnvironmentName = environmentName;
        Failures = failures;
    }

    /// <summary>Host environment the configuration was validated for.</summary>
    public string EnvironmentName { get; }

    /// <summary>Every missing/invalid required setting, in validation order.</summary>
    public IReadOnlyList<string> Failures { get; }
}

/// <summary>
/// Pre-startup validation of settings that are mandatory in non-Development
/// environments, plus the API-key secret that is mandatory in every
/// environment unless auth is explicitly disabled.
///
/// Runs right after <c>builder.Build()</c> — once test and operator
/// configuration sources are merged — but before the host starts resolving
/// services, so a missing required setting fails with one clean diagnostic
/// (messages on stderr, exit code
/// <see cref="ConfigurationFailureExitCode"/>) instead of an unhandled
/// <see cref="InvalidOperationException"/> escaping from a DI factory while
/// the host is resolving services (stack trace on stdout, SIGABRT/core dump,
/// exit 134 — indistinguishable from a crash-loop).
///
/// The message builders below are the single source of truth for these
/// diagnostics: the DI-time guards in <c>Program.cs</c> and
/// <c>ApiKeyAuth</c> throw with the same strings, so the pre-build gate and
/// the defense-in-depth factory checks can never drift apart. The factories
/// keep their own guards because future callers must not be able to bypass
/// validation by resolving the service directly.
/// </summary>
internal static class RequiredConfigurationValidator
{
    /// <summary>
    /// Process exit code for missing/invalid required configuration. A
    /// conventional non-zero failure code, deliberately distinct from the
    /// runtime's abnormal-termination code (134/SIGABRT) so a configuration
    /// mistake and a genuine crash are distinguishable from the exit code
    /// alone. Matches <c>BackgroundServiceFailureTracker</c>'s fault code.
    /// </summary>
    internal const int ConfigurationFailureExitCode = 1;

    internal static string MissingSandboxProviderMessage =>
        "CodeyBox:SandboxProvider must be set in non-Development environments. " +
        "Choose one of: incus, multipass, multipass-remote, sprites, bubblewrap, process " +
        "(see docs/concepts/sandboxes.md for trade-offs).";

    internal static string ProcessSandboxUnsafeMessage =>
        "CodeyBox:SandboxProvider=process is UNSAFE outside Development. " +
        "Set CodeyBox:DangerouslyAllowProcessSandbox=true to override (NOT recommended), " +
        "or pick incus | multipass | bubblewrap.";

    internal static string UntrustedWorkloadMessage(string providerName, SandboxIsolationLevel isolation) =>
        $"Untrusted workloads require a dedicated-kernel sandbox; provider '{providerName}' " +
        $"advertises {isolation} isolation.";

    internal static string TrustedSharedKernelMessage(string providerName, SandboxIsolationLevel isolation) =>
        $"Trusted workloads using provider '{providerName}' ({isolation}) require " +
        "CodeyBox:AcknowledgeSharedKernelRisk=true.";

    internal static string E2eLocalPoolMessage =>
        "CodeyBox:E2eExecution:PoolKind=local is development-only. Use remote-ssh for production E2E replay execution.";

    internal static string ApiKeyMissingMessage =>
        $"{ApiKeyAuth.EnvVarName} must be set, or set {ApiKeyAuth.DisableConfigKey}=true to opt out of auth (dev only).";

    internal static string ApiKeyTooShortMessage =>
        $"{ApiKeyAuth.EnvVarName} must be at least 32 characters of high-entropy random data.";

    internal static string ApiClientsEntryMessage =>
        "Each CodeyBox:ApiClients entry requires Name, TokenEnvVar, and Principal.";

    internal static string ApiClientTokenMessage(string tokenEnvVar) =>
        $"{tokenEnvVar} must contain at least 32 characters of high-entropy random data.";

    internal static string ChangelogSecretMessage =>
        "CodeyBox:Changelog:GitHubWebhookSecretEnvVar must be configured in non-Development environments. " +
        "Set it to the name of the environment variable holding the HMAC-SHA256 webhook secret " +
        "(see docs/operating/releases.md).";

    /// <summary>
    /// Collects every missing/invalid required setting in one pass. Pure:
    /// reads configuration and (via <paramref name="getEnvironmentVariable"/>)
    /// secret presence, writes nothing. Only setting names are reported,
    /// never secret values.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="getEnvironmentVariable">Environment lookup. Defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>; injectable
    /// so tests stay hermetic.</param>
    internal static IReadOnlyList<string> Validate(
        IConfiguration configuration,
        IHostEnvironment environment,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var failures = new List<string>();
        var isDevelopment = environment.IsDevelopment();

        ValidateSandboxProvider(configuration, isDevelopment, failures);
        ValidateE2ePoolKind(configuration, isDevelopment, failures);
        ValidateApiKey(configuration, getEnvironmentVariable, failures);
        ValidateChangelogSecret(configuration, isDevelopment, failures);

        return failures;
    }

    /// <summary>
    /// Writes the diagnostic for <see cref="Validate"/> failures to
    /// <paramref name="stderr"/>. Emits only the messages — no stack trace.
    /// </summary>
    internal static void WriteFailures(
        TextWriter stderr,
        IHostEnvironment environment,
        IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(failures);

        stderr.Write(FormatFailures(environment.EnvironmentName, failures));
    }

    /// <summary>
    /// Formats the diagnostic for <see cref="Validate"/> failures. Single
    /// source of truth for the text written to stderr and carried on
    /// <see cref="RequiredConfigurationException"/>.
    /// </summary>
    internal static string FormatFailures(string environmentName, IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(failures);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine(
            $"CodeyBox configuration error: {failures.Count} required setting(s) missing or invalid " +
            $"(environment '{environmentName}'). Fix the following and restart:");
        foreach (var failure in failures)
            writer.WriteLine($"  - {failure}");
        writer.WriteLine("Startup aborted before the host started; no services were initialized.");
        return writer.ToString();
    }

    /// <summary>
    /// Builds the exception for an enforcement site that has already decided
    /// the configuration is invalid (<paramref name="triggerMessage"/>
    /// describes why). Re-runs the full <see cref="Validate"/> pass so the
    /// operator sees every missing setting at once instead of fixing them
    /// one run at a time; the trigger message is always included even if the
    /// validator's conditions ever drift from the caller's.
    /// </summary>
    internal static RequiredConfigurationException CreateAggregateException(
        IConfiguration configuration,
        IHostEnvironment environment,
        string triggerMessage,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(triggerMessage);

        var failures = Validate(configuration, environment, getEnvironmentVariable).ToList();
        if (!failures.Contains(triggerMessage))
            failures.Add(triggerMessage);
        return new RequiredConfigurationException(environment.EnvironmentName, failures);
    }

    /// <summary>
    /// Handles <see cref="AppDomain.UnhandledException"/> for the real
    /// binary: a <see cref="RequiredConfigurationException"/> escaping Main
    /// would otherwise reach the runtime's unhandled-exception handler
    /// (stack trace, SIGABRT/core dump, exit 134). The handler writes the
    /// diagnostic to stderr and exits with
    /// <see cref="ConfigurationFailureExitCode"/> instead. Every other
    /// exception type is ignored here so genuine crashes keep their existing
    /// behaviour. Test hosts catch the entry-point exception themselves, so
    /// this handler never fires there — tests observe the throw.
    /// </summary>
    /// <param name="sender">Event sender (ignored).</param>
    /// <param name="e">Unhandled-exception details.</param>
    internal static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => OnUnhandledException(sender, e, stderr: null, exit: null);

    /// <inheritdoc cref="OnUnhandledException(object?, UnhandledExceptionEventArgs)"/>
    /// <param name="stderr">Diagnostic sink. Defaults to
    /// <see cref="Console.Error"/>.</param>
    /// <param name="exit">Process exit. Defaults to
    /// <see cref="Environment.Exit(int)"/>; injectable so tests stay
    /// hermetic.</param>
    internal static void OnUnhandledException(
        object? sender,
        UnhandledExceptionEventArgs e,
        TextWriter? stderr,
        Action<int>? exit)
    {
        if (e.ExceptionObject is not RequiredConfigurationException configException)
            return;

        var writer = stderr ?? Console.Error;
        writer.Write(configException.Message);
        writer.Flush();
        (exit ?? Environment.Exit)(ConfigurationFailureExitCode);
    }

    /// <summary>
    /// Registers <see cref="OnUnhandledException"/> exactly once per process.
    /// Unsubscribing first keeps repeated entry-point invocations in one
    /// process (WebApplicationFactory boots) from stacking duplicate
    /// handlers, without introducing shared mutable state.
    /// </summary>
    internal static void RegisterUnhandledExceptionHandler()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void ValidateSandboxProvider(
        IConfiguration configuration,
        bool isDevelopment,
        List<string> failures)
    {
        var options = configuration.GetSection("CodeyBox").Get<CodeyBoxOptions>() ?? new CodeyBoxOptions();

        string kind;
        try
        {
            kind = ReloadableSandboxProvider.NormalizeConfiguredProviderId(options.SandboxProvider);
        }
        catch (InvalidOperationException ex)
        {
            failures.Add($"CodeyBox:SandboxProvider is invalid: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(kind))
        {
            if (!isDevelopment)
                failures.Add(MissingSandboxProviderMessage);
            return;
        }

        if (!isDevelopment
            && string.Equals(kind, HostPlatformSupport.Process, StringComparison.Ordinal)
            && !options.DangerouslyAllowProcessSandbox)
        {
            failures.Add(ProcessSandboxUnsafeMessage);
        }

        if (!isDevelopment
            && TryGetIsolationLevel(kind, out var isolation)
            && isolation != SandboxIsolationLevel.DedicatedKernel)
        {
            var trust = Enum.TryParse<WorkloadTrust>(options.WorkloadTrust, true, out var configured)
                ? configured
                : WorkloadTrust.Untrusted;
            if (trust == WorkloadTrust.Untrusted)
                failures.Add(UntrustedWorkloadMessage(kind, isolation));
            else if (!options.AcknowledgeSharedKernelRisk)
                failures.Add(TrustedSharedKernelMessage(kind, isolation));
        }
    }

    private static bool TryGetIsolationLevel(string kind, out SandboxIsolationLevel isolation)
    {
        if (string.Equals(kind, HostPlatformSupport.Incus, StringComparison.Ordinal)
            || string.Equals(kind, HostPlatformSupport.Multipass, StringComparison.Ordinal)
            || string.Equals(kind, HostPlatformSupport.MultipassRemote, StringComparison.Ordinal)
            || string.Equals(kind, HostPlatformSupport.Sprites, StringComparison.Ordinal))
        {
            isolation = SandboxIsolationLevel.DedicatedKernel;
            return true;
        }

        if (string.Equals(kind, HostPlatformSupport.Bubblewrap, StringComparison.Ordinal))
        {
            isolation = SandboxIsolationLevel.SharedKernel;
            return true;
        }

        if (string.Equals(kind, HostPlatformSupport.Process, StringComparison.Ordinal))
        {
            isolation = SandboxIsolationLevel.None;
            return true;
        }

        isolation = default;
        return false;
    }

    private static void ValidateE2ePoolKind(
        IConfiguration configuration,
        bool isDevelopment,
        List<string> failures)
    {
        var e2e = configuration.GetSection("CodeyBox:E2eExecution").Get<E2eExecutionOptions>()
            ?? new E2eExecutionOptions();
        var poolKind = (e2e.PoolKind ?? "remote-ssh").Trim().ToLowerInvariant();
        if (poolKind == "local" && !isDevelopment)
            failures.Add(E2eLocalPoolMessage);
    }

    private static void ValidateApiKey(
        IConfiguration configuration,
        Func<string, string?> getEnvironmentVariable,
        List<string> failures)
    {
        if (configuration.GetValue<bool>(ApiKeyAuth.DisableConfigKey))
            return;

        var key = getEnvironmentVariable(ApiKeyAuth.EnvVarName);
        if (string.IsNullOrWhiteSpace(key))
            failures.Add(ApiKeyMissingMessage);
        else if (key.Length < 32)
            failures.Add(ApiKeyTooShortMessage);

        var clients = configuration.GetSection("CodeyBox:ApiClients").Get<List<ApiClientOptions>>() ?? [];
        foreach (var client in clients)
        {
            if (client is null
                || string.IsNullOrWhiteSpace(client.Name)
                || string.IsNullOrWhiteSpace(client.TokenEnvVar)
                || client.Principal is null)
            {
                failures.Add(ApiClientsEntryMessage);
                continue;
            }

            var token = getEnvironmentVariable(client.TokenEnvVar);
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                failures.Add(ApiClientTokenMessage(client.TokenEnvVar));
        }
    }

    private static void ValidateChangelogSecret(
        IConfiguration configuration,
        bool isDevelopment,
        List<string> failures)
    {
        var changelog = configuration.GetSection("CodeyBox:Changelog").Get<ChangelogOptions>()
            ?? new ChangelogOptions();
        if (changelog.Enabled
            && string.IsNullOrEmpty(changelog.GitHubWebhookSecretEnvVar)
            && !isDevelopment)
        {
            failures.Add(ChangelogSecretMessage);
        }
    }
}
