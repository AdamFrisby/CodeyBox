using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Minimal credential viability check for vibe. Returns Ok when the bundle
/// contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_VIBE_API_KEY</c> to it); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: vibe fronts many providers behind one CLI (the active model
/// selects the provider at dispatch time), so no single endpoint validates
/// "the" credential, and probing an arbitrary provider endpoint would burn
/// quota on a meter the operator may not even use. The real auth check
/// happens on first CLI call inside the sandbox, where the runner lifts the
/// terminal error (see <see cref="VibeTerminalDiagnoser"/>). Mirrors the
/// Goose/Pi presence-check probes.</para>
/// </summary>
public sealed class VibeSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<VibeSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Vibe;

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_VIBE_API_KEY</c> → sandbox-side
    /// <c>OPENROUTER_API_KEY</c>). The variable the probe requires.
    /// </summary>
    public const string PrimaryCredentialVariable = "OPENROUTER_API_KEY";

    public VibeSmokeProbe(ILogger<VibeSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Goose/Pi
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(PrimaryCredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Vibe smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Vibe credential configured (set host CODEYBOX_VIBE_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
