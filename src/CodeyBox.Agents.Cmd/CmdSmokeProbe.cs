using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Minimal credential viability check for cmd. Returns Ok when the bundle
/// contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_CMD_API_KEY</c> to it; the runner seeds
/// the guest <c>providers.json</c> with a <c>$OPENROUTER_API_KEY</c>
/// reference the CLI resolves from the sandbox environment); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: cmd fronts 150+ providers behind one CLI, so no single
/// endpoint validates "the" credential, and probing an arbitrary provider
/// endpoint would burn quota on a meter the operator may not even use. The
/// real auth check happens on first CLI call inside the sandbox, where the
/// runner lifts the terminal error (see
/// <see cref="CmdTerminalDiagnoser"/>). Mirrors the Omp/Cline/Goose
/// presence-check probes, which share the same OpenRouter-path mapping.
/// The Command Code account key (<c>auth.json</c>) is deliberately NOT
/// probed: plan-less BYOK runs satisfy the gate with a non-credential
/// placeholder under <c>--local-only</c>, so absence of a vendor account
/// is not a failure.</para>
/// </summary>
public sealed class CmdSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<CmdSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Cmd;

    public CmdSmokeProbe(ILogger<CmdSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Omp/Cline/Goose
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(CmdAgentRunner.CredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Cmd smoke probe found no {Variable} in credential bundle", CmdAgentRunner.CredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Cmd credential configured (set host CODEYBOX_CMD_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
