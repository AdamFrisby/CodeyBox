using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

public sealed class CodeyBoxOptionsValidator : IValidateOptions<CodeyBoxOptions>
{
    private readonly E2eRemotePoolConfigValidation _e2eRemotePoolConfigValidation;

    public CodeyBoxOptionsValidator()
        : this(E2eRemotePoolConfigValidation.Default)
    {
    }

    internal CodeyBoxOptionsValidator(E2eRemotePoolConfigValidation e2eRemotePoolConfigValidation)
    {
        _e2eRemotePoolConfigValidation = e2eRemotePoolConfigValidation;
    }

    public ValidateOptionsResult Validate(string? name, CodeyBoxOptions options)
    {
        var failures = new List<string>();

        var selectedProvider = NormalizeSandboxProviderId(options.SandboxProvider, failures);
        var retainedProviders = ValidateRetainedSandboxProviderInventory(options, failures);
        if (string.Equals(selectedProvider, SandboxProviderKinds.Incus, StringComparison.OrdinalIgnoreCase)
            || retainedProviders.Contains(SandboxProviderKinds.Incus))
        {
            // Retained providers are activated for lifecycle inventory even
            // when they are not selected for new work.
            try
            {
                failures.AddRange(
                    IncusSandboxOptions.Validate(IncusSandboxConfigMapper.Build(options))
                        .Select(static error => $"CodeyBox:Incus:{error}"));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
            {
                failures.Add(ex.Message.StartsWith("CodeyBox:", StringComparison.Ordinal)
                    ? ex.Message
                    : $"CodeyBox:Incus:{ex.Message}");
            }
        }

        if (double.IsNaN(options.PhaseAbsoluteTimeoutMultiplier)
            || double.IsInfinity(options.PhaseAbsoluteTimeoutMultiplier)
            || options.PhaseAbsoluteTimeoutMultiplier < 1.0)
        {
            failures.Add("CodeyBox:PhaseAbsoluteTimeoutMultiplier must be finite and >= 1");
        }

        if (options.DeepAuditFailurePersistence is null)
        {
            failures.Add("CodeyBox:DeepAuditFailurePersistence must not be null");
        }
        else
        {
            try
            {
                options.DeepAuditFailurePersistence.Validate();
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex.Message);
            }
        }

        if (options.MaxTemplateChecks is < 1 or > CodeyBoxOptions.MaximumMaxTemplateChecks)
        {
            failures.Add(
                $"CodeyBox:MaxTemplateChecks must be between 1 and {CodeyBoxOptions.MaximumMaxTemplateChecks}");
        }

        if (options.MaxBulkItems is < 1 or > CodeyBoxOptions.MaximumMaxBulkItems)
        {
            failures.Add(
                $"CodeyBox:MaxBulkItems must be between 1 and {CodeyBoxOptions.MaximumMaxBulkItems}");
        }

        if (options.SqliteWriteGate is null)
        {
            failures.Add("CodeyBox:SqliteWriteGate must not be null");
        }
        else
        {
            try
            {
                options.SqliteWriteGate.Validate();
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex.Message);
            }
        }

        var e2e = options.E2eExecution;
        if (e2e is not null)
        {
            if (e2e.MaxConcurrent is < E2eExecutionOptions.MinimumMaxConcurrent
                or > E2eExecutionOptions.MaximumMaxConcurrent)
            {
                failures.Add(
                    $"CodeyBox:E2eExecution:MaxConcurrent must be between {E2eExecutionOptions.MinimumMaxConcurrent} and {E2eExecutionOptions.MaximumMaxConcurrent}");
            }
            if (e2e.PollInterval < TimeSpan.Zero)
            {
                failures.Add("CodeyBox:E2eExecution:PollInterval must be non-negative");
            }
            if (e2e.PerRunTimeout <= TimeSpan.Zero)
            {
                failures.Add("CodeyBox:E2eExecution:PerRunTimeout must be positive");
            }
            if (!string.Equals(e2e.PoolKind, "local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e2e.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("CodeyBox:E2eExecution:PoolKind must be 'local' or 'remote-ssh'");
            }
            var remoteE2e = string.Equals(e2e.PoolKind, "remote-ssh", StringComparison.OrdinalIgnoreCase);
            if (e2e.Enabled && remoteE2e)
            {
                failures.AddRange(_e2eRemotePoolConfigValidation.ValidateEnabledRemoteE2eConfig(e2e, options));
            }
            else if (remoteE2e)
            {
                failures.AddRange(_e2eRemotePoolConfigValidation.ValidateConfiguredRemoteLifecycleIsolation(options));
            }
            foreach (var (host, index) in GetE2eRemoteHostConfigs(options).Select((host, index) => (host, index)))
            {
                if (host.MaxConcurrent is < E2eExecutionOptions.MinimumMaxConcurrent or > E2eExecutionOptions.MaximumMaxConcurrent)
                {
                    failures.Add(
                        $"CodeyBox:E2eMultipassRemoteSandboxes:{index}:MaxConcurrent must be between {E2eExecutionOptions.MinimumMaxConcurrent} and {E2eExecutionOptions.MaximumMaxConcurrent}");
                }
            }
            if (e2e.AllowedReadinessOrigins is null || e2e.AllowedReadinessOrigins.Count == 0)
            {
                failures.Add("CodeyBox:E2eExecution:AllowedReadinessOrigins must contain at least one origin");
            }
            foreach (var origin in e2e.AllowedReadinessOrigins ?? [])
            {
                if (string.IsNullOrWhiteSpace(origin)
                    || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
                    || !string.IsNullOrEmpty(uri.Query)
                    || !string.IsNullOrEmpty(uri.Fragment)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    failures.Add("CodeyBox:E2eExecution:AllowedReadinessOrigins entries must be http(s) origins without path, query, fragment, or userinfo");
                    break;
                }
            }
        }

        foreach (var (agent, tolerance) in options.AgentNetworkTolerance)
        {
            if (string.IsNullOrWhiteSpace(agent))
            {
                failures.Add("CodeyBox:AgentNetworkTolerance keys must not be empty");
                continue;
            }
            if (tolerance is null)
            {
                failures.Add($"CodeyBox:AgentNetworkTolerance:{agent} must not be null");
                continue;
            }

            if (string.Equals(agent, AgentNetworkToleranceOptions.CodexAgentKind, StringComparison.OrdinalIgnoreCase))
            {
                if (tolerance.RequestMaxRetries is < 0 or > AgentNetworkToleranceOptions.CodexMaximumRetries)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:RequestMaxRetries must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumRetries}");
                }
                if (tolerance.StreamMaxRetries is < 0 or > AgentNetworkToleranceOptions.CodexMaximumRetries)
                {
                    failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:StreamMaxRetries must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumRetries}");
                }
                if (tolerance.StreamIdleTimeoutMs is < 0 or > AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs)
                {
                    failures.Add(
                        $"CodeyBox:AgentNetworkTolerance:{agent}:StreamIdleTimeoutMs must be between 0 and {AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs}");
                }
                if (tolerance.Provider is not null)
                {
                    if (string.IsNullOrWhiteSpace(tolerance.Provider))
                    {
                        failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:Provider must not be empty");
                    }
                    else if (!AgentNetworkToleranceOptions.IsValidCodexProviderId(tolerance.Provider))
                    {
                        failures.Add($"CodeyBox:AgentNetworkTolerance:{agent}:Provider must match [A-Za-z0-9_-]+");
                    }
                }
            }
            else if (string.Equals(agent, AgentNetworkToleranceOptions.ClaudeAgentKind, StringComparison.OrdinalIgnoreCase))
            {
                if (tolerance.ApiTimeoutMs is < 0 or > AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs)
                {
                    failures.Add(
                        $"CodeyBox:AgentNetworkTolerance:{agent}:ApiTimeoutMs must be between 0 and {AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs}");
                }
            }
        }

        foreach (var (agent, pause) in options.AgentPauses)
        {
            if (string.IsNullOrWhiteSpace(agent))
            {
                failures.Add("CodeyBox:AgentPauses keys must not be empty");
                continue;
            }

            if (pause.Paused
                && AgentPauseValidation.ValidateRequiredReason(pause.Reason, $"CodeyBox:AgentPauses:{agent}:Reason") is { } reasonError)
                failures.Add(reasonError);
            else if (!pause.Paused
                && AgentPauseValidation.ValidateOptionalReason(pause.Reason, $"CodeyBox:AgentPauses:{agent}:Reason") is { } optionalReasonError)
                failures.Add(optionalReasonError);

            if (pause.DurationSeconds is { } seconds && seconds <= 0)
                failures.Add($"CodeyBox:AgentPauses:{agent}:DurationSeconds must be positive");
            if (pause.DurationSeconds is not null && pause.ExpiresAt is not null)
                failures.Add($"CodeyBox:AgentPauses:{agent} must provide either DurationSeconds or ExpiresAt, not both");
        }

        if (!Enum.IsDefined(options.Shutdown.SandboxResumeMode))
        {
            failures.Add("CodeyBox:Shutdown:SandboxResumeMode must be Background or Blocking");
        }

        if (!Enum.IsDefined(options.Shutdown.SandboxTeardownMode))
        {
            failures.Add("CodeyBox:Shutdown:SandboxTeardownMode must be Suspend, Stop, or Dispose");
        }

        if (options.Shutdown.SandboxResumeTimeout <= TimeSpan.Zero
            || options.Shutdown.SandboxResumeTimeout > SandboxStartupResumePolicy.MaximumResumeTimeout)
        {
            failures.Add(
                $"CodeyBox:Shutdown:SandboxResumeTimeout must be a positive TimeSpan <= {SandboxStartupResumePolicy.MaximumResumeTimeout}");
        }

        if (options.Shutdown.SandboxAdoptionDeadlineSeconds <= 0
            || options.Shutdown.SandboxAdoptionDeadlineSeconds > (int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds)
        {
            failures.Add(
                $"CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds must be > 0 and <= {(int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds}");
        }

        try
        {
            options.WorkerPoolHealthWatchdog.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (options.PipelineTuning.MaxSandboxReuses < 1)
        {
            failures.Add("CodeyBox:PipelineTuning:MaxSandboxReuses must be >= 1");
        }
        if (options.PipelineTuning.MaxRetainedAgentTurnSandboxes is < 1 or > 256)
        {
            failures.Add(
                "CodeyBox:PipelineTuning:MaxRetainedAgentTurnSandboxes must be between 1 and 256");
        }
        if (!PlanReviewIterationLimit.TryCreate(options.PipelineTuning.MaxPlanReviewIterations, out _))
        {
            failures.Add(
                $"CodeyBox:PipelineTuning:MaxPlanReviewIterations must be >= {PlanReviewIterationLimit.MinimumValue}");
        }
        if (options.PipelineTuning.MaxSandboxLifetime <= TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:MaxSandboxLifetime must be a positive TimeSpan");
        }
        if (double.IsNaN(options.PipelineTuning.SandboxPressureThreshold)
            || double.IsInfinity(options.PipelineTuning.SandboxPressureThreshold)
            || options.PipelineTuning.SandboxPressureThreshold < 0.0
            || options.PipelineTuning.SandboxPressureThreshold > 1.0)
        {
            failures.Add("CodeyBox:PipelineTuning:SandboxPressureThreshold must be between 0.0 and 1.0 inclusive");
        }
        if (options.PipelineTuning.AuditorIdleTimeout < TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:AuditorIdleTimeout must be non-negative");
        }
        if (options.PipelineTuning.EmptyReworkEscalationRetries < 0)
        {
            failures.Add("CodeyBox:PipelineTuning:EmptyReworkEscalationRetries must be non-negative");
        }

        if (options.PipelineTuning.CSharpTestPassAuditorIdleTimeout is { } cSharpTestIdle && cSharpTestIdle < TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:CSharpTestPassAuditorIdleTimeout must be non-negative when set");
        }

        if (options.PipelineTuning.CSharpTestPassBlameHangTimeout is { } cSharpTestBlameHang && cSharpTestBlameHang <= TimeSpan.Zero)
        {
            failures.Add("CodeyBox:PipelineTuning:CSharpTestPassBlameHangTimeout must be positive when set");
        }

        try
        {
            options.AgentSupervision.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        try
        {
            options.Attachments.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }

        if (options.EnableSharedUpstreamMirror && string.IsNullOrWhiteSpace(options.SharedUpstreamMirrorDirectory))
        {
            failures.Add("CodeyBox:SharedUpstreamMirrorDirectory must not be empty if EnableSharedUpstreamMirror is true");
        }
        if (options.GitCommandMaxOutputBytes <= 0)
        {
            failures.Add("CodeyBox:GitCommandMaxOutputBytes must be > 0");
        }

        if (options.AutoRequeueOnAgentRestore.Enabled)
        {
            try
            {
                _ = OrchestratorOptionsFactory.BuildAgentRestoreRetryOptions(
                    options.AutoRequeueOnAgentRestore.Enabled,
                    options.AutoRequeueOnAgentRestore.LookbackGrace,
                    options.AutoRequeueOnAgentRestore.PostRestoreMargin,
                    options.AutoRequeueOnAgentRestore.InvolvementTerminalLookback,
                    options.AutoRequeueOnAgentRestore.InvolvementTerminalClockSkew,
                    options.AutoRequeueOnAgentRestore.MaxCandidatesPerSweep,
                    options.AutoRequeueOnAgentRestore.EventQueueCapacity);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(ex.Message);
            }
        }

        ValidateMultipassRemote(options, selectedProvider, failures);

        failures.AddRange(AuditLogStartup.Validate(options.AuditLog));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string NormalizeSandboxProviderId(
        string? configuredProviderId,
        ICollection<string> failures)
    {
        try
        {
            return ReloadableSandboxProvider.NormalizeConfiguredProviderId(configuredProviderId);
        }
        catch (InvalidOperationException ex)
        {
            failures.Add($"CodeyBox:SandboxProvider is invalid: {ex.Message}");
            return string.Empty;
        }
    }

    private static HashSet<string> ValidateRetainedSandboxProviderInventory(
        CodeyBoxOptions options,
        ICollection<string> failures)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var retained = options.SandboxProviderCutover?.RetainedInventoryProviders;
        if (retained is null)
        {
            failures.Add("CodeyBox:SandboxProviderCutover:RetainedInventoryProviders must not be null");
            return seen;
        }

        var index = 0;
        foreach (var configuredProviderId in retained)
        {
            if (index == SandboxProviderCutoverConfig.MaximumRetainedInventoryProviders)
            {
                failures.Add(
                    $"CodeyBox:SandboxProviderCutover:RetainedInventoryProviders must contain at most {SandboxProviderCutoverConfig.MaximumRetainedInventoryProviders} entries");
                return seen;
            }

            string providerId;
            try
            {
                providerId = ReloadableSandboxProvider.NormalizeConfiguredProviderId(configuredProviderId);
            }
            catch (InvalidOperationException)
            {
                providerId = string.Empty;
            }
            if (!SandboxProviderKinds.SupportsHotReload(providerId))
            {
                failures.Add(
                    $"CodeyBox:SandboxProviderCutover:RetainedInventoryProviders:{index} must name a registered hot-reload provider");
                index++;
                continue;
            }
            if (!seen.Add(providerId))
            {
                failures.Add(
                    $"CodeyBox:SandboxProviderCutover:RetainedInventoryProviders:{index} duplicates an earlier provider ID");
            }
            index++;
        }
        return seen;
    }


    private static IReadOnlyList<E2eMultipassRemoteHostConfig> GetE2eRemoteHostConfigs(CodeyBoxOptions options)
    {
        if (options.E2eMultipassRemoteSandboxes is { Count: > 0 } hosts)
            return hosts;
        return options.E2eMultipassRemoteSandbox is null
            ? []
            : [options.E2eMultipassRemoteSandbox];
    }

    private static void ValidateMultipassRemote(
        CodeyBoxOptions options,
        string selectedProvider,
        List<string> failures)
    {
        var providerIsRemote = string.Equals(
            selectedProvider,
            "multipass-remote",
            StringComparison.Ordinal);
        var cfg = options.MultipassRemoteSandbox;
        if (cfg is null)
        {
            if (providerIsRemote)
                failures.Add("CodeyBox:MultipassRemoteSandbox section is required when SandboxProvider=multipass-remote");
            return;
        }

        if (cfg.MaxConcurrentSandboxes is <= 0)
            failures.Add("CodeyBox:MultipassRemoteSandbox:MaxConcurrentSandboxes must be > 0 when set");
        if (cfg.PlacementRecheckIn is { } placement && placement <= TimeSpan.Zero)
            failures.Add("CodeyBox:MultipassRemoteSandbox:PlacementRecheckIn must be positive when set");
        if (cfg.RuntimeUnhealthyBackoff is { } backoff && backoff <= TimeSpan.Zero)
            failures.Add("CodeyBox:MultipassRemoteSandbox:RuntimeUnhealthyBackoff must be positive when set");
        if (cfg.StageOutMaxArchiveBytes is <= 0)
            failures.Add("CodeyBox:MultipassRemoteSandbox:StageOutMaxArchiveBytes must be > 0 when set");
        if (cfg.StageOutMaxEntries is <= 0)
            failures.Add("CodeyBox:MultipassRemoteSandbox:StageOutMaxEntries must be > 0 when set");
        if (cfg.StageOutMaxExpansionRatio is { } ratio && (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 1.0d))
            failures.Add("CodeyBox:MultipassRemoteSandbox:StageOutMaxExpansionRatio must be >= 1 when set");
        if (cfg.RemoteInventoryMaxOutputBytes is <= 0)
            failures.Add("CodeyBox:MultipassRemoteSandbox:RemoteInventoryMaxOutputBytes must be > 0 when set");

        var hasTopLevelTarget = !string.IsNullOrWhiteSpace(cfg.SshTarget);
        var hosts = cfg.ExecutorHosts ?? [];
        if (providerIsRemote && hosts.Count == 0 && !hasTopLevelTarget)
            failures.Add("CodeyBox:MultipassRemoteSandbox:SshTarget is required when SandboxProvider=multipass-remote and ExecutorHosts is empty");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            var prefix = $"CodeyBox:MultipassRemoteSandbox:ExecutorHosts:{i}";
            if (string.IsNullOrWhiteSpace(host.Id))
                failures.Add($"{prefix}:Id is required and must be stable across hot reloads");
            if (!string.IsNullOrWhiteSpace(host.Id) && !ids.Add(host.Id.Trim()))
                failures.Add($"{prefix}:Id duplicates another executor host id ('{host.Id.Trim()}')");
            if (providerIsRemote && !hasTopLevelTarget && string.IsNullOrWhiteSpace(host.SshTarget))
                failures.Add($"{prefix}:SshTarget is required when no top-level SshTarget default is configured");
            if (host.MaxConcurrentSandboxes is <= 0)
                failures.Add($"{prefix}:MaxConcurrentSandboxes must be > 0 when set");
            if (host.ServerAliveIntervalSeconds is <= 0)
                failures.Add($"{prefix}:ServerAliveIntervalSeconds must be > 0 when set");
            if (host.ServerAliveCountMax is <= 0)
                failures.Add($"{prefix}:ServerAliveCountMax must be > 0 when set");
            if (host.ConnectTimeoutSeconds is <= 0)
                failures.Add($"{prefix}:ConnectTimeoutSeconds must be > 0 when set");
            if (host.StageOutMaxArchiveBytes is <= 0)
                failures.Add($"{prefix}:StageOutMaxArchiveBytes must be > 0 when set");
            if (host.StageOutMaxEntries is <= 0)
                failures.Add($"{prefix}:StageOutMaxEntries must be > 0 when set");
            if (host.StageOutMaxExpansionRatio is { } hostRatio && (double.IsNaN(hostRatio) || double.IsInfinity(hostRatio) || hostRatio < 1.0d))
                failures.Add($"{prefix}:StageOutMaxExpansionRatio must be >= 1 when set");
            if (host.RemoteInventoryMaxOutputBytes is <= 0)
                failures.Add($"{prefix}:RemoteInventoryMaxOutputBytes must be > 0 when set");
            if (host.VmStartTimeout is { } start && start <= TimeSpan.Zero)
                failures.Add($"{prefix}:VmStartTimeout must be positive when set");
            if (host.VmStopTimeout is { } stop && stop <= TimeSpan.Zero)
                failures.Add($"{prefix}:VmStopTimeout must be positive when set");
            if (host.VmStateCheckInterval is { } poll && poll <= TimeSpan.Zero)
                failures.Add($"{prefix}:VmStateCheckInterval must be positive when set");
        }
    }
}
