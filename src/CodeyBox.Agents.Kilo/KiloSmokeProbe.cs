using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Minimal credential viability check for kilo. Returns Ok when the bundle
/// contains a non-empty <c>KILO_API_KEY</c> (the shipped credential mapping
/// wires host <c>CODEYBOX_KILO_API_KEY</c> to it); returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: kilo is a multi-provider front with no single lightweight
/// "whoami", and any provider call would spend real quota. The real auth
/// check happens on first CLI call inside the sandbox, where the runner
/// lifts the terminal error (see <see cref="KiloTerminalDiagnoser"/>).
/// Mirrors the Pi/Goose/Autohand presence-check probes.</para>
/// </summary>
public sealed class KiloSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<KiloSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Kilo;

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_KILO_API_KEY</c> → sandbox-side <c>KILO_API_KEY</c>).
    /// The variable the probe requires: it gates dispatch AND its value is
    /// seeded into the guest <c>kilo.jsonc</c> provider block (the CLI reads
    /// the key only from that file on the openai-compatible path).
    /// </summary>
    public const string PrimaryCredentialVariable = "KILO_API_KEY";

    public KiloSmokeProbe(ILogger<KiloSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Pi/Goose/Autohand
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(PrimaryCredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Kilo smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Kilo credential configured (set host CODEYBOX_KILO_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
