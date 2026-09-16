using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Minimal credential viability check for dotnet-opencode. Returns Ok when
/// the bundle contains a non-empty <c>DOTNETOPENCODE_CONFIG_JSON</c> blob
/// (the global <c>~/.config/opencode/opencode.json</c> provider config,
/// materialised into the sandbox by <see cref="DotNetOpencodeAgentRunner"/>);
/// returns Fail otherwise.
///
/// <para>Unlike the Claude / Codex / Gemini probes this does NOT issue a
/// network call: dotnet-opencode is a provider-agnostic BYOK front with no
/// single usage endpoint, the only local auth surface is the interactive
/// <c>auth login</c> device flow (unusable headless), and any provider call
/// would spend real quota on a meter the operator provisions themselves. Per
/// <c>feedback-vendor-api-drift</c>: ship the credential-presence check now.
/// The real auth check happens on first CLI call inside the sandbox, where
/// the runner lifts the terminal error (see
/// <see cref="DotNetOpencodeTerminalDiagnoser"/>). Mirrors the
/// Cursor presence-check probe (verbatim config-JSON mapping).</para>
/// </summary>
public sealed class DotNetOpencodeSmokeProbe : IAgentSmokeProbe
{
    private readonly ILogger<DotNetOpencodeSmokeProbe>? _log;

    public AgentKind Kind => AgentKind.DotNetOpencode;

    public DotNetOpencodeSmokeProbe(ILogger<DotNetOpencodeSmokeProbe>? log = null)
    {
        _log = log;
    }

    public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
    {
        // No I/O happens here — report TimeSpan.Zero explicitly so the value
        // is honest about what was measured, matching the
        // OpencodeSmokeProbe convention a future network-backed probe can
        // build on.
        var hasConfigJson = credential.EnvironmentVariables.TryGetValue("DOTNETOPENCODE_CONFIG_JSON", out var json)
            && !string.IsNullOrEmpty(json);
        if (!hasConfigJson)
        {
            _log?.LogDebug("DotNetOpencode smoke probe found no DOTNETOPENCODE_CONFIG_JSON in credential bundle");
            return Task.FromResult(new AgentSmokeResult(
                false,
                "no dotnet-opencode credential configured (set host CODEYBOX_DOTNETOPENCODE_CONFIG_JSON)",
                TimeSpan.Zero,
                SmokeFailureCategory.Persistent));
        }
        return Task.FromResult(new AgentSmokeResult(true, null, TimeSpan.Zero, SmokeFailureCategory.None));
    }
}
