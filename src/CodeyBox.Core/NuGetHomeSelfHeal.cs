namespace CodeyBox.Core;

/// <summary>
/// Shared POSIX self-heal for an unusable NuGet user-settings home.
///
/// <para>On an unprivileged build host a root-provisioned <c>~/.nuget</c> makes
/// NuGet's user settings unreadable/uncreatable, so any <c>dotnet</c> command
/// that restores (build, test, format) fails during restore for <em>every</em>
/// project with "Failed to read NuGet.Config due to unauthorized access". A repo
/// <c>NuGet.Config</c> or <c>--configfile</c> does not help, because NuGet still
/// probes the user settings directory before per-command configuration is
/// applied.</para>
///
/// <para>The remedy is environmental, not source-level: when any level of the
/// <c>~/.nuget</c> settings hierarchy is unusable by the current user, redirect
/// <c>DOTNET_CLI_HOME</c> to a writable temp dir (reusing the existing package
/// cache when present) so restore can create its own settings. This is the single
/// source of truth for that shell preamble; the required-build gate and the
/// C# build/test/format shell auditors both consume it, and the standalone
/// <c>./build.sh</c> entrypoint mirrors it (it cannot share a constant across the
/// standalone-script vs. sandboxed-command execution contexts).</para>
/// </summary>
public static class NuGetHomeSelfHeal
{
    /// <summary>
    /// Base name of the writable fallback home directory created under
    /// <c>$TMPDIR</c> (or <c>/tmp</c>) when the real NuGet home is unusable. The
    /// preamble appends the current numeric UID (<c>{leaf}-$(id -u)</c>) so
    /// concurrent audits by the same user share one healed home (reusing its
    /// package cache) while different principals never collide on one predictable
    /// path. That shared per-user dir is reused only when it already exists, is
    /// owned by the current user, and is writable; otherwise the preamble falls
    /// back to a private <c>mktemp</c> dir (<c>{leaf}.XXXXXX</c>) so a squatted or
    /// root-left directory in a world-writable temp cannot make the healed home
    /// itself unusable. NuGet manages its own settings-dir creation within it.
    /// </summary>
    public const string WritableHomeLeaf = "codeybox-nuget-home";

    /// <summary>
    /// POSIX shell preamble that exports a redirected <c>DOTNET_CLI_HOME</c> (and,
    /// when reusable, <c>NUGET_PACKAGES</c>) iff the current user cannot use its
    /// <c>~/.nuget</c> settings home. A no-op on a healthy home.
    ///
    /// <para>Assumes the enclosing script already ran <c>set -eu</c>; the
    /// <c>[ ... ] &amp;&amp; flag</c> idioms are <c>set -e</c>-safe because a
    /// failing test is a non-final AND-OR member. Every variable expansion uses a
    /// <c>:-</c> default so it is also <c>set -u</c>-safe.</para>
    /// </summary>
    public const string Preamble = $$"""
        nuget_home="${DOTNET_CLI_HOME:-$HOME}"
        nuget_root="${nuget_home}/.nuget"
        nuget_settings_dir="${nuget_root}/NuGet"
        nuget_settings_file="${nuget_settings_dir}/NuGet.Config"
        nuget_home_broken=0
        if [ -d "$nuget_settings_dir" ]; then
          { [ ! -w "$nuget_settings_dir" ]; } && nuget_home_broken=1
          { [ -e "$nuget_settings_file" ] && [ ! -r "$nuget_settings_file" ]; } && nuget_home_broken=1
        elif [ -d "$nuget_root" ] && [ ! -w "$nuget_root" ]; then
          nuget_home_broken=1
        fi
        if [ "$nuget_home_broken" -eq 1 ]; then
          writable_home="${TMPDIR:-/tmp}/{{WritableHomeLeaf}}-$(id -u)"
          mkdir -p "$writable_home" 2>/dev/null || true
          if [ ! -d "$writable_home" ] || [ ! -O "$writable_home" ] || [ ! -w "$writable_home" ]; then
            writable_home="$(mktemp -d "${TMPDIR:-/tmp}/{{WritableHomeLeaf}}.XXXXXX")"
          fi
          export DOTNET_CLI_HOME="$writable_home"
          existing_packages="${nuget_root}/packages"
          if [ -z "${NUGET_PACKAGES:-}" ] && [ -d "$existing_packages" ] && [ -w "$existing_packages" ]; then
            export NUGET_PACKAGES="$existing_packages"
          fi
          echo "nuget-home self-heal: '${nuget_root}' is not usable by $(id -un) (root-owned NuGet home?); redirecting DOTNET_CLI_HOME=${DOTNET_CLI_HOME}${NUGET_PACKAGES:+ (reusing package cache ${NUGET_PACKAGES})}" >&2
        fi
        """;

    /// <summary>
    /// Wraps a <c>dotnet</c> invocation so the <see cref="Preamble"/> runs first
    /// (healing the NuGet home if needed) and then execs the original command with
    /// its arguments preserved exactly. Any non-<c>dotnet</c> command — or an empty
    /// argv — is returned unchanged, so this is safe to apply uniformly to shell
    /// auditor commands.
    /// </summary>
    /// <param name="argv">The command and arguments to run. Not mutated.</param>
    /// <returns>
    /// The wrapped <c>sh -c</c> argv for a dotnet command, otherwise the input argv.
    /// </returns>
    public static IReadOnlyList<string> WrapDotnetInvocation(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || !string.Equals(argv[0], "dotnet", StringComparison.Ordinal))
            return argv;

        // Run the preamble under `set -eu` (matching the required-build gate) then
        // `exec "$@"`. $0 is the sh placeholder; $1.. are the original argv, passed
        // as separate arguments so no shell re-splitting or quoting can alter them.
        var script = "set -eu\n" + Preamble + "\nexec \"$@\"";
        var wrapped = new List<string>(argv.Count + 4) { "sh", "-c", script, "sh" };
        wrapped.AddRange(argv);
        return wrapped;
    }
}
