using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Minimal credential viability check for cline. Returns Ok when the bundle
/// contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_CLINE_API_KEY</c> to it); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: cline is a multi-provider front with no single lightweight
/// "whoami", and any provider call would spend real quota. The real auth
/// check happens on first CLI call inside the sandbox, where the runner
/// lifts the terminal error (see <see cref="ClineTerminalDiagnoser"/>).
/// Mirrors the Pi/Goose/Autohand presence-check probes.</para>
/// </summary>
public sealed class ClineSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<ClineSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Cline;

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_CLINE_API_KEY</c> → sandbox-side
    /// <c>OPENROUTER_API_KEY</c>). The CLI reads the OpenRouter key from
    /// this variable directly (verified: no config file required), and the
    /// runner never passes it via <c>-k</c> so it stays out of argv.
    /// </summary>
    public const string PrimaryCredentialVariable = "OPENROUTER_API_KEY";

    public ClineSmokeProbe(ILogger<ClineSmokeProbe>? log = null)
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
            _log?.LogDebug("Cline smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Cline credential configured (set host CODEYBOX_CLINE_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
