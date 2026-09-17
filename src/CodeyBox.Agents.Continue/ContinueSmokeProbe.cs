using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Continue;

/// <summary>
/// Minimal credential viability check for Continue. Returns Ok when the
/// bundle contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped
/// credential mapping wires host <c>CODEYBOX_CONTINUE_API_KEY</c> to it);
/// returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: Continue is a multi-provider front with no single
/// lightweight "whoami", and any provider call would spend real quota. The
/// real auth check happens on first CLI call inside the sandbox, where the
/// runner lifts the terminal error (see
/// <see cref="ContinueTerminalDiagnoser"/>). Mirrors the
/// Pi/Kilo/Autohand presence-check probes.</para>
/// </summary>
public sealed class ContinueSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<ContinueSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Continue;

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_CONTINUE_API_KEY</c> → sandbox-side
    /// <c>OPENROUTER_API_KEY</c>). The variable the probe requires: it gates
    /// dispatch AND its value is seeded into the guest
    /// <c>~/.continue/config.yaml</c> model entry (the CLI reads the key
    /// only from that file on the config path).
    /// </summary>
    public const string PrimaryCredentialVariable = "OPENROUTER_API_KEY";

    public ContinueSmokeProbe(ILogger<ContinueSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Pi/Kilo/Autohand
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(PrimaryCredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Continue smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Continue credential configured (set host CODEYBOX_CONTINUE_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
