using System.Diagnostics;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Minimal credential viability check for opencode. Returns Ok when the
/// bundle contains either an <c>OPENCODE_AUTH_JSON</c> blob (subscription
/// auth, materialised into the sandbox by <see cref="OpencodeAgentRunner"/>)
/// or an explicit <c>OPENCODE_API_KEY</c>; returns Fail otherwise.
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
        var sw = Stopwatch.StartNew();
        var hasAuthJson = credential.EnvironmentVariables.TryGetValue("OPENCODE_AUTH_JSON", out var json)
            && !string.IsNullOrEmpty(json);
        var hasApiKey = credential.EnvironmentVariables.TryGetValue("OPENCODE_API_KEY", out var key)
            && !string.IsNullOrEmpty(key);
        sw.Stop();
        if (!hasAuthJson && !hasApiKey)
        {
            _log?.LogDebug("Opencode smoke probe found no credential material in bundle");
            return Task.FromResult(new AgentSmokeResult(false, "no token in credential bundle", sw.Elapsed));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, sw.Elapsed));
    }
}
