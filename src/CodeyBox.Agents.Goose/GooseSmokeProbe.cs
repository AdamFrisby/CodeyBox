using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Minimal credential viability check for goose. Returns Ok when the bundle
/// contains a non-empty <c>OPENROUTER_API_KEY</c> (the shipped credential
/// mapping wires host <c>CODEYBOX_GOOSE_API_KEY</c> to it); returns Fail
/// otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: goose fronts 30+ providers behind one CLI, so no single
/// endpoint validates "the" credential, and probing an arbitrary provider
/// endpoint would burn quota on a meter the operator may not even use. The
/// real auth check happens on first CLI call inside the sandbox, where the
/// runner lifts the terminal error (see
/// <see cref="GooseTerminalDiagnoser"/>). Mirrors the Pi presence-check
/// probe.</para>
/// </summary>
public sealed class GooseSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<GooseSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.Goose;

    /// <summary>
    /// Provider API-key variables goose honors (auth discovery in the
    /// agent-orchestrator goose adapter: <c>backend/internal/adapters/agent/goose/auth.go</c>
    /// <c>gooseAPIKeyEnvVars</c>). The probe gates on
    /// <see cref="PrimaryCredentialVariable"/>; operators fronting another
    /// provider from this list extend the credential mapping with that
    /// provider's variable.
    /// </summary>
    public static readonly IReadOnlyList<string> ProviderApiKeyEnvironmentVariables =
    [
        "GOOSE_API_KEY",
        "GOOSE_PROVIDER__API_KEY",
        "GOOSE_EDITOR_API_KEY",
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "OPENROUTER_API_KEY",
        "DEEPSEEK_API_KEY",
        "GROQ_API_KEY",
        "XAI_API_KEY",
        "MISTRAL_API_KEY",
        "COHERE_API_KEY",
    ];

    /// <summary>
    /// Credential variable the shipped mapping populates
    /// (<c>CODEYBOX_GOOSE_API_KEY</c> → sandbox-side
    /// <c>OPENROUTER_API_KEY</c>). The variable the probe requires.
    /// </summary>
    public const string PrimaryCredentialVariable = "OPENROUTER_API_KEY";

    public GooseSmokeProbe(ILogger<GooseSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the Pi/Opencode
        // presence-check convention a future network-backed probe can build on.
        var hasApiKey = credential.EnvironmentVariables.TryGetValue(PrimaryCredentialVariable, out var key)
            && !string.IsNullOrEmpty(key);
        if (!hasApiKey)
        {
            _log?.LogDebug("Goose smoke probe found no {Variable} in credential bundle", PrimaryCredentialVariable);
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no Goose credential configured (set host CODEYBOX_GOOSE_API_KEY)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
