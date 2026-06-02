using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Minimal credential viability check for opencode. Returns Ok when the
/// bundle contains a non-empty <c>OPENCODE_AUTH_JSON</c> blob (subscription
/// auth, materialised into the sandbox by <see cref="OpencodeAgentRunner"/>);
/// returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: opencode's subscription endpoint shape has not been
/// verified, and probing the wrong endpoint would either generate spurious
/// "auth" failures (false-negative) or always pass regardless of token
/// validity (false-positive). Per <c>feedback-vendor-api-drift</c>: ship
/// the credential-presence check now and add a real probe when an endpoint
/// is confirmed.</para>
/// </summary>
public sealed class OpencodeSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<OpencodeSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Opencode;

    public OpencodeSmokeProbe(ILogger<OpencodeSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — a Stopwatch would report ~0. Report
        // TimeSpan.Zero explicitly so the value is honest about what was
        // measured, and a future real network-backed probe can swap in a
        // Stopwatch when there's actual elapsed time to surface.
        var hasAuthJson = credential.EnvironmentVariables.TryGetValue("OPENCODE_AUTH_JSON", out var json)
            && !string.IsNullOrEmpty(json);
        if (!hasAuthJson)
        {
            _log?.LogDebug("Opencode smoke probe found no OPENCODE_AUTH_JSON in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false, "no token in credential bundle", TimeSpan.Zero, SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
