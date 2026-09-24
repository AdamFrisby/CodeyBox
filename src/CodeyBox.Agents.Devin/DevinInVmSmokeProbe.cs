using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// In-VM smoke check for the devin CLI:
/// <list type="number">
///   <item><c>devin --version</c> — binary present on PATH (exit 127 otherwise).</item>
///   <item>auth materialised via the exact script the runner uses
///   (<see cref="DevinAgentRunner.AuthMaterialiseScript"/>), then
///   <c>devin models list --format json</c> must return 0 — this subcommand
///   exits 1 when unauthenticated (verified against devin 3000.11.1), proving
///   the credentials file landed where the CLI reads it AND the api_server_url
///   inside is reachable. <c>devin auth status</c> is deliberately NOT the
///   check: it prints "Not logged in." and still exits 0.</item>
///   <item>a real ACP turn using the exact dispatch wrapper
///   (<see cref="DevinAgentRunner.BuildAcpDispatchScript"/> +
///   <see cref="DevinAcpShim.BuildDispatchStdin"/>) so the shim delivery,
///   prompt-file, session-mode, and <c>--model</c> contract is exercised
///   end to end.</item>
/// </list>
///
/// <para>When the auth credential is absent — no credential bundle, or one
/// without <c>CODEYBOX_DEVIN_AUTH_TOML</c> — the probe returns only the
/// binary-presence step (still exec'd by the prober), so a binary missing from
/// PATH is caught without a false auth-failure exclusion. See
/// <see cref="IInVmSmokeProbe"/>.</para>
/// </summary>
public sealed class DevinInVmSmokeProbe : IInVmSmokeProbe
{
    private readonly AgentDefaultsSnapshot? _defaults;

    /// <param name="defaults">
    /// Live snapshot of per-agent default model IDs (see
    /// <see cref="AgentDefaultsSnapshot"/>). The probe's real ACP turn pins
    /// the configured devin model so it exercises the same
    /// <c>--model</c> argv leg a dispatch does — and never silently bills
    /// the account's server-side default model.
    /// </param>
    public DevinInVmSmokeProbe(AgentDefaultsSnapshot? defaults = null)
    {
        _defaults = defaults;
    }

    public AgentKind Kind => AgentKind.Devin;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [DevinAgentRunner.DefaultBinary, "--version"],
                FailureHint: "devin binary not runnable on sandbox PATH"),
        };

        var hasAuth = credential is not null
            && credential.EnvironmentVariables.ContainsKey(DevinAgentRunner.AuthTomlEnvironmentVariable);
        if (hasAuth)
        {
            steps.Add(new(
                ["bash", "-c", DevinAgentRunner.AuthMaterialiseScript],
                FailureHint: "failed to materialise devin credentials.toml"));
            steps.Add(new(
                [DevinAgentRunner.DefaultBinary, "models", "list", "--format", "json"],
                FailureHint: "devin models list failed (credentials path drift or invalid token)"));
            // Runs through the runner's own ACP dispatch wrapper and framed
            // stdin, so the probe fails whenever a real dispatch would fail:
            // shim decode, prompt-file delivery, handshake, the
            // full-autonomy session mode, and the configured --model pin all
            // exercise the production path.
            steps.Add(new(
                ["bash", "-c", DevinAgentRunner.BuildAcpDispatchScript(
                    DevinAgentRunner.AcpShimArgs(
                        DevinAgentRunner.DefaultBinary,
                        _defaults?.GetDefault(Kind.Value),
                        DevinAgentRunner.FullAutonomyAcpMode))],
                Stdin: DevinAcpShim.BuildDispatchStdin("Reply with the single word: OK"),
                FailureHint: "devin acp turn failed (shim delivery, prompt-file, handshake or session-mode contract drift)"));
        }

        return steps;
    }
}
