using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Credential smoke test for Cursor.
///
/// <para>Subscription usage is probed separately by
/// <see cref="CursorQuotaProbe"/> against
/// <c>DashboardService/GetCurrentPeriodUsage</c>. This smoke probe performs a
/// cheap credential-presence check — it verifies that the credential bundle
/// carries <c>CODEYBOX_CURSOR_AUTH_JSON</c> and surfaces a human-readable
/// failure reason otherwise. This is intentionally lighter than invoking the
/// CLI: a real <c>agent --version</c> call requires a sandbox, which is too
/// heavy for the startup-time smoke gate.</para>
///
/// <para>The first real Cursor invocation in the pipeline remains an
/// authoritative credential check; <see cref="CursorQuotaFailureDetector"/>
/// classifies limit and auth failures from dispatch output.</para>
///
/// <para>This host probe deliberately does NOT verify that the <c>agent</c>
/// binary is present in the sandbox image — see the comment above re. multipass
/// cold-start cost. That in-sandbox check now lives in <c>CursorInVmSmokeProbe</c>
/// (exec'd by <c>InVmSmokeProber</c>), which catches a missing binary (exit
/// 127), auth-path drift, and workspace-trust regressions at smoke time. The
/// <b>fast-fail circuit breaker</b> in <c>AgentAvailabilityRegistry</c> remains
/// the last-resort backstop for a real dispatch that exits non-zero in under
/// <c>FastFailThresholdSeconds</c> for <c>MaxConsecutiveFastFails</c> attempts,
/// excluding the agent until a smoke probe passes or an operator calls
/// <c>/admin/agent/cursor/reset</c>.</para>
/// </summary>
public sealed class CursorSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<CursorSmokeProbe> _log;

    public AgentKind Kind => AgentKind.Cursor;

    public CursorSmokeProbe(ILogger<CursorSmokeProbe> log)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var hasAuthJson = credential.EnvironmentVariables.ContainsKey("CODEYBOX_CURSOR_AUTH_JSON");
            sw.Stop();
            if (hasAuthJson)
                return Task.FromResult(new AgentSmokeResult(true, null, sw.Elapsed));

            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Cursor credential configured (set CODEYBOX_CURSOR_AUTH_FILE or CODEYBOX_CURSOR_AUTH_JSON)",
                sw.Elapsed));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cursor smoke probe threw; treating as transient");
            sw.Stop();
            return Task.FromResult(new AgentSmokeResult(false, "transient: try later", sw.Elapsed));
        }
    }
}
