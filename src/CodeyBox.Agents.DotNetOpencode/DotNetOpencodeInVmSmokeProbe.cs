using CodeyBox.Core;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// In-VM smoke check for the dotnet-opencode CLI:
/// <list type="number">
/// <item><c>dotnet-opencode --version</c> — binary present on PATH with a
/// working .NET 11 preview runtime (exit 127 when the shim is absent; the
/// host's "You must install or update .NET" failure when the runtime is
/// missing). This is the step that turns a broken install into a benched
/// member naming the cause instead of an empty run recorded as "no
/// changes".</item>
/// <item><c>dotnet-opencode run --help</c> must advertise
/// <c>--format</c>/<c>json</c> — the runner's only transport. A build that
/// dropped the JSON event stream would otherwise dispatch into an
/// unparseable run and fail late.</item>
/// <item>ripgrep must be on PATH (or <c>OPENCODE_DOTNET_RIPGREP</c> set to an
/// absolute executable path) — the CLI fails fast without it (verified
/// live: <c>Error: Ripgrep is unavailable on PATH…</c>, exit 1).</item>
/// </list>
///
/// <para>When the provider config credential is absent the probe returns only
/// these environment checks (still exec'd by the prober): a binary missing
/// from PATH is caught without a false auth-failure exclusion, and no step
/// spends provider quota. See <see cref="IInVmSmokeProbe"/>.</para>
/// </summary>
public sealed class DotNetOpencodeInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.DotNetOpencode;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        _ = credential;
        return
        [
            new(
                [DotNetOpencodeAgentRunner.DefaultBinary, "--version"],
                FailureHint: "dotnet-opencode binary not runnable on sandbox PATH (missing tool install or .NET 11 preview runtime)"),
            // Exit-code-only by InVmSmokeStep contract, so assert --format/json
            // support through grep's exit code: the runner's only transport is
            // the JSON event stream, and a build that dropped it must bench
            // here rather than dispatch into an unparseable run.
            new(
                ["bash", "-c", $"{DotNetOpencodeAgentRunner.DefaultBinary} run --help | grep -q -- --format"],
                FailureHint: "dotnet-opencode run --help does not advertise --format; --format json support could not be verified"),
            // The CLI fails fast without ripgrep (verified live, exit 1), so
            // assert its presence the same exit-code-only way rather than
            // discovering it at first dispatch.
            new(
                ["bash", "-c", "command -v rg >/dev/null 2>&1 || test -n \"$OPENCODE_DOTNET_RIPGREP\""],
                FailureHint: "ripgrep not on sandbox PATH and OPENCODE_DOTNET_RIPGREP unset; dotnet-opencode run fails fast without it"),
        ];
    }
}
