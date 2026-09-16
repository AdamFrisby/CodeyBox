using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Minimal credential viability check for autohand. Returns Ok when the bundle
/// contains a non-empty <c>AUTOHAND_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_AUTOHAND_API_KEY</c> to it); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: autohand is a multi-provider front with no single lightweight
/// "whoami", and any provider call would spend real quota. The real auth check
/// happens on first CLI call inside the sandbox, where the runner lifts the
/// terminal error (see <see cref="AutohandTerminalDiagnoser"/>). Mirrors the
/// Pi/Goose presence-check probes.</para>
/// </summary>
public sealed class AutohandSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<AutohandSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Autohand;

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_AUTOHAND_API_KEY</c> → sandbox-side
    /// <c>AUTOHAND_API_KEY</c>). The variable the probe requires: it gates
    /// bare mode AND its value is seeded into the guest config's provider
    /// block (the CLI reads the key only from that file).
    /// </summary>
    public const string PrimaryCredentialVariable = "AUTOHAND_API_KEY";

    public AutohandSmokeProbe(ILogger<AutohandSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Pi/Goose
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(PrimaryCredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Autohand smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Autohand credential configured (set host CODEYBOX_AUTOHAND_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
