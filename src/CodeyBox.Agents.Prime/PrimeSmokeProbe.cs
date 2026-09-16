using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Minimal credential viability check for prime-agent. Returns Ok when the
/// bundle contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped
/// credential mapping wires host <c>CODEYBOX_PRIME_API_KEY</c> to it);
/// returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: prime fronts many providers behind one CLI, so no single
/// endpoint validates "the" credential, and probing an arbitrary provider
/// endpoint would burn quota on a meter the operator may not even use. The
/// real auth check happens on first CLI call inside the sandbox, where the
/// runner lifts the terminal error (see
/// <see cref="PrimeTerminalDiagnoser"/>). Mirrors the
/// Cursor/Opencode/Pi/Aider presence-check probes.</para>
/// </summary>
public sealed class PrimeSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<PrimeSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Prime;

    public PrimeSmokeProbe(ILogger<PrimeSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the OpencodeSmokeProbe
        // convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue("OPENROUTER_API_KEY", out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Prime smoke probe found no OPENROUTER_API_KEY in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Prime credential configured (set host CODEYBOX_PRIME_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
