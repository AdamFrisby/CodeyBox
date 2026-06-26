using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Minimal credential viability check for crock. Returns Ok when the
/// bundle contains a non-empty <c>CROCK_CONFIG_JSON</c> blob (JSON config,
/// materialised into the sandbox by <see cref="CrockAgentRunner"/>);
/// returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: crock's host-side API endpoint shape is not verified, and
/// doctor-testing happens in-VM via <see cref="CrockInVmSmokeProbe"/>.
/// Mirrors <see cref="OpencodeSmokeProbe"/>'s shape.</para>
/// </summary>
public sealed class CrockSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<CrockSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Crock;

    public CrockSmokeProbe(ILogger<CrockSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — a Stopwatch would report ~0. Report
        // TimeSpan.Zero explicitly so the value is honest about what was
        // measured.
        var hasConfig = credential.EnvironmentVariables.TryGetValue(CrockAgentRunner.ConfigEnvVar, out var json)
            && !string.IsNullOrEmpty(json);
        if (!hasConfig)
        {
            _log?.LogDebug("Crock smoke probe found no CROCK_CONFIG_JSON in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false, "no config in credential bundle", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
