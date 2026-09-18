using CodeyBox.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Resolves the legacy <c>CodeyBox:SandboxProvider</c> setting to a
/// normalised provider kind. Single source of truth for the defaulting rules
/// previously inline in <c>Program.SelectSandboxProvider</c>: an unset value
/// defaults to <c>"process"</c> in Development and fails closed elsewhere
/// with the same required-configuration diagnostic. Shared by the singleton
/// provider selection and the default sandbox-class synthesis so the two
/// cannot disagree on what "configured" means.
/// </summary>
internal static class SandboxProviderSelection
{
    /// <summary>
    /// Normalises <paramref name="configuredProviderId"/> (trimmed,
    /// lowercase). Applies the unset-value default: <c>"process"</c> when the
    /// environment is Development (with a startup warning), otherwise the
    /// same fail-closed required-configuration exception
    /// <c>SelectSandboxProvider</c> throws today. Does not check host
    /// support — callers validate the resolved kind against the host before
    /// building.
    /// </summary>
    internal static string ResolveConfiguredKind(
        string? configuredProviderId,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);
        var kind = ReloadableSandboxProvider.NormalizeConfiguredProviderId(configuredProviderId);
        if (!string.IsNullOrEmpty(kind))
            return kind;

        if (environment.IsDevelopment())
        {
            log.LogWarning(
                "CodeyBox:SandboxProvider not set; defaulting to 'process' because environment is Development. " +
                "DO NOT do this in production.");
            return SandboxProviderKinds.Process;
        }

        throw RequiredConfigurationValidator.CreateAggregateException(
            configuration,
            environment,
            RequiredConfigurationValidator.MissingSandboxProviderMessage);
    }

    /// <summary>
    /// Enforces the workload-trust policy for one constructed provider: in
    /// non-Development, untrusted workloads require a dedicated-kernel
    /// provider, and trusted workloads on a shared-kernel provider require
    /// the explicit risk acknowledgement. Generic over the provider's
    /// advertised <see cref="SandboxIsolationLevel"/> — no provider kind is
    /// named here. Shared by the singleton selection and the registry factory
    /// so a member cannot bypass the deployment trust policy by naming a
    /// weaker provider kind than <c>CodeyBox:SandboxProvider</c>.
    /// </summary>
    internal static void ValidateWorkloadTrust(
        ISandboxProvider inner,
        CodeyBoxOptions options,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var workloadTrust = Enum.TryParse<WorkloadTrust>(options.WorkloadTrust, true, out var configuredTrust)
            ? configuredTrust
            : environment.IsDevelopment() ? WorkloadTrust.Trusted : WorkloadTrust.Untrusted;
        if (!environment.IsDevelopment()
            && workloadTrust == WorkloadTrust.Untrusted
            && inner.IsolationLevel != SandboxIsolationLevel.DedicatedKernel)
        {
            throw RequiredConfigurationValidator.CreateAggregateException(
                configuration,
                environment,
                RequiredConfigurationValidator.UntrustedWorkloadMessage(inner.Name, inner.IsolationLevel));
        }
        if (!environment.IsDevelopment()
            && workloadTrust == WorkloadTrust.Trusted
            && inner.IsolationLevel != SandboxIsolationLevel.DedicatedKernel
            && !options.AcknowledgeSharedKernelRisk)
        {
            throw RequiredConfigurationValidator.CreateAggregateException(
                configuration,
                environment,
                RequiredConfigurationValidator.TrustedSharedKernelMessage(inner.Name, inner.IsolationLevel));
        }
    }
}
