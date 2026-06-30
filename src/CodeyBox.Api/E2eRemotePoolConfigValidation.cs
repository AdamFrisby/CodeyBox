using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class E2eRemotePoolConfigValidation
{
    public static IReadOnlyList<string> ValidateEnabledRemoteE2eConfig(E2eExecutionOptions e2e, CodeyBoxOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(e2e.BaselineImageRef))
        {
            failures.Add("CodeyBox:E2eExecution:BaselineImageRef is required when E2E execution is enabled on PoolKind=remote-ssh; the remote pool clones this pre-baked image per run.");
        }

        if (!string.IsNullOrWhiteSpace(e2e.NetworkProfile))
        {
            failures.Add("CodeyBox:E2eExecution:NetworkProfile is not supported by PoolKind=remote-ssh yet. Configure the remote E2E baseline networking directly or leave NetworkProfile unset.");
        }

        var e2eHosts = GetE2eRemoteHostConfigs(options);
        if (e2eHosts.Count == 0 || e2eHosts.Any(static host => string.IsNullOrWhiteSpace(host.SshTarget)))
        {
            failures.Add("CodeyBox:E2eMultipassRemoteSandbox:SshTarget or CodeyBox:E2eMultipassRemoteSandboxes[*]:SshTarget is required when E2E execution uses PoolKind=remote-ssh.");
            return failures;
        }

        foreach (var host in e2eHosts)
        {
            if (E2eRemoteHostValidation.IsLocalSshTarget(host, out var error))
            {
                failures.Add(error is null
                    ? "CodeyBox:E2eMultipassRemoteSandbox must target a dedicated remote SSH host, not localhost, loopback, or the orchestrator host; E2E replay load must stay off the local coding fleet."
                    : $"CodeyBox:E2eMultipassRemoteSandbox must target a dedicated resolvable remote SSH host; {error}.");
            }
        }

        if (options.MultipassRemoteSandbox is { SshTarget.Length: > 0 } coding)
        {
            foreach (var host in e2eHosts)
            {
                if (E2eRemoteHostValidation.IsSameRemoteHost(coding, host, out var error))
                {
                    failures.Add("CodeyBox:E2eMultipassRemoteSandbox must target a different SSH host than CodeyBox:MultipassRemoteSandbox; E2E replay load must stay off the coding fleet.");
                }
                else if (error is not null)
                {
                    failures.Add($"CodeyBox:E2eMultipassRemoteSandbox and CodeyBox:MultipassRemoteSandbox hosts must be resolvable to verify fleet isolation; {error}.");
                }
            }
        }

        return failures;
    }

    private static IReadOnlyList<MultipassRemoteSandboxConfig> GetE2eRemoteHostConfigs(CodeyBoxOptions options)
    {
        if (options.E2eMultipassRemoteSandboxes is { Count: > 0 } hosts)
            return hosts;
        return options.E2eMultipassRemoteSandbox is null
            ? []
            : [options.E2eMultipassRemoteSandbox];
    }
}
