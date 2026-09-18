using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Crush;

/// <summary>
/// Minimal credential viability check for Crush. Returns Ok when the bundle
/// contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_CRUSH_API_KEY</c> to it — the CLI reads
/// the key directly from the process environment, so no config file is
/// seeded); returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: Crush is a multi-provider front with no single lightweight
/// "whoami", and any provider call would spend real quota. The real auth
/// check happens on first CLI call inside the sandbox, where the runner
/// lifts the terminal error (see
/// <see cref="CrushTerminalDiagnoser"/>). Mirrors the
/// Continue/Cmd/Qwen presence-check probes, which share the same
/// OpenRouter-path mapping.</para>
/// </summary>
public sealed class CrushSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<CrushSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Crush;

    public CrushSmokeProbe(ILogger<CrushSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Continue/Cmd/Qwen
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(CrushAgentRunner.CredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Crush smoke probe found no {Variable} in credential bundle", CrushAgentRunner.CredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Crush credential configured (set host CODEYBOX_CRUSH_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
