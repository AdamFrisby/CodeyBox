using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Minimal credential viability check for qwen. Returns Ok when the bundle
/// contains a non-empty <c>OPENAI_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_QWEN_API_KEY</c> to it); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: qwen fronts many providers behind one CLI (native Qwen,
/// OpenAI-, Anthropic-, and Gemini-compatible endpoints), so no single
/// endpoint validates "the" credential, and probing an arbitrary provider
/// endpoint would burn quota on a meter the operator may not even use. The
/// real auth check happens on first CLI call inside the sandbox, where the
/// runner lifts the terminal error (see
/// <see cref="QwenTerminalDiagnoser"/>). Mirrors the Omp/Prime/Vibe/Cline
/// presence-check probes, which share the same OpenRouter-path mapping.</para>
/// </summary>
public sealed class QwenSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<QwenSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Qwen;

    public QwenSmokeProbe(ILogger<QwenSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Omp/Prime/Vibe
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue("OPENAI_API_KEY", out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Qwen smoke probe found no OPENAI_API_KEY in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Qwen credential configured (set host CODEYBOX_QWEN_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
