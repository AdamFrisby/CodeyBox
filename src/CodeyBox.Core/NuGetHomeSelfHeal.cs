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
/// cache when present) so restore can create its own settings. The preamble also
/// guarantees a usable per-user runtime location (<c>HOME</c> /
/// <c>XDG_RUNTIME_DIR</c>) for dotnet's user-scoped named mutexes — since
/// runtime 10.0.13 an unusable <c>$HOME</c> aborts first-use before MSBuild with
/// an IOException creating <c>NuGet-Migrations</c>, which a DOTNET_CLI_HOME
/// redirect alone does not cover. The required-build gate and the C#
/// build/test/format shell auditors consume the preamble this class composes;
/// the standalone <c>./build.sh</c> entrypoint and the MSBuild
/// <c>InitialTargets</c> hooks dot-source <c>scripts/nuget-home-heal.sh</c>,
/// which is the canonical source for the recovery — the helpers below are
/// lifted verbatim from that file, not mirrored by hand (see
/// <see cref="SharedHelpers"/>).</para>
/// </summary>
public static class NuGetHomeSelfHeal
{
    /// <summary>
    /// Start sentinel marking the region of <c>scripts/nuget-home-heal.sh</c>
    /// that is embedded verbatim as the head of <see cref="Preamble"/>.
    /// </summary>
    internal const string SharedHelpersBeginMarker = "# === codeybox-shared-helpers: begin ===";

    /// <summary>
    /// End sentinel marking the region of <c>scripts/nuget-home-heal.sh</c>
    /// that is embedded verbatim as the head of <see cref="Preamble"/>.
    /// </summary>
    internal const string SharedHelpersEndMarker = "# === codeybox-shared-helpers: end ===";

    private const string HealScriptResourceName = "CodeyBox.Core.nuget-home-heal.sh";

    /// <summary>
    /// The directory-admission policy helpers (<c>_codeybox_lstat_dir</c>,
    /// <c>_codeybox_secure_dir</c>, <c>_codeybox_adopt_dir</c>,
    /// <c>_codeybox_pick_user_dir</c>), the shared decision skeleton
    /// (<c>_codeybox_nuget_home_usable</c>, <c>codeybox_pin_runtime_dirs</c>),
    /// the consumed-cache admission (<c>_codeybox_secure_package_cache</c>,
    /// <c>_codeybox_reuse_nuget_packages</c>), the output-encoding helper
    /// (<c>_codeybox_strip_ctrl</c>), and the leaf constants, lifted verbatim
    /// from the marked region of
    /// <c>scripts/nuget-home-heal.sh</c> — embedded into this assembly as a
    /// manifest resource at build time — so a guard or policy change in the
    /// shell file lands in both the dot-sourced recovery and this preamble
    /// automatically. There is intentionally no second copy of either in
    /// this file to drift out of sync.
    /// </summary>
    private static readonly string SharedHelpers = LoadSharedHelpers();

    /// <summary>
    /// Base name of the writable fallback home directory created under
    /// <c>$TMPDIR</c> (or <c>/tmp</c>) when the real NuGet home is unusable. The
    /// preamble appends the current numeric UID (<c>{leaf}-$(id -u)</c>) so
    /// concurrent audits by the same user share one healed home (reusing its
    /// package cache) while different principals never collide on one predictable
    /// path. That shared per-user dir is reused only when it verifies as
    /// "private" storage — canonicalized, owned by the current uid, writable,
    /// never group/other-writable (no ACL-broadened access either), under an
    /// anchor-checked ancestor chain; otherwise the preamble falls back to a
    /// private <c>mktemp</c> dir (<c>{leaf}.XXXXXX</c>) so a squatted or
    /// root-left directory in a world-writable temp cannot make the healed
    /// home itself unusable. NuGet manages its own settings-dir creation
    /// within it.
    /// Parsed from <c>_codeybox_home_leaf</c> in the embedded
    /// <c>scripts/nuget-home-heal.sh</c>, which is the single source of the
    /// literal — a rename there updates this name automatically (pinned by
    /// <c>BuildScriptNuGetHealTests</c>).
    /// </summary>
    internal static string WritableHomeLeaf { get; } =
        ParseLeafConstant(SharedHelpers, "_codeybox_home_leaf");

    /// <summary>
    /// Name of the stamped repo-local CLI home directory
    /// (<see cref="DotnetCliHomeConventions.DirectoryName"/> backs onto this),
    /// parsed from <c>_codeybox_repo_home_leaf</c> in the embedded
    /// <c>scripts/nuget-home-heal.sh</c> — the single source of the literal.
    /// </summary>
    internal static string RepoCliHomeLeaf { get; } =
        ParseLeafConstant(SharedHelpers, "_codeybox_repo_home_leaf");

    /// <summary>
    /// POSIX shell preamble that exports a redirected <c>DOTNET_CLI_HOME</c> (and,
    /// when reusable, <c>NUGET_PACKAGES</c>) iff the current user cannot use its
    /// <c>~/.nuget</c> settings home, and pins <c>HOME</c>/<c>XDG_RUNTIME_DIR</c>
    /// to a writable per-user dir when either is unusable (required by .NET
    /// 10.0.13+ user-scoped named mutexes such as <c>NuGet-Migrations</c>).
    /// A no-op when the NuGet home, <c>$HOME</c>, and <c>$XDG_RUNTIME_DIR</c>
    /// are all usable.
    ///
    /// <para>The preamble is the embedded shared-helpers region of
    /// <c>scripts/nuget-home-heal.sh</c> followed by this class's decision
    /// fragment. "Usable" is deliberately stronger than "-d &amp;&amp; -w": the
    /// env values are untrusted input, so every admitted directory is
    /// canonicalized (cd/pwd -P) and required to be owned by this uid and never
    /// group/other-writable, under an ancestor chain a co-tenant cannot swap —
    /// a merely-writable foreign dir must never hold the NuGet.Config every
    /// restore reads or dotnet's user-scoped mutex storage.</para>
    ///
    /// <para>Assumes the enclosing script already ran <c>set -eu</c>; every
    /// helper invocation is a command substitution guarded by
    /// <c>|| var=</c> (or an <c>if</c>/<c>||</c>/<c>&amp;&amp;</c> member) so a
    /// degraded step — including a total <c>mktemp</c> failure — cannot abort
    /// the script before <c>exec "$@"</c>. When no verified private fallback
    /// can be materialized at all, a rejected env variable is <c>unset</c>
    /// rather than left exported — leaving it would hand the wrapped
    /// <c>dotnet</c> co-tenant-controlled storage, while unset falls back to
    /// the passwd entry / platform defaults. Every variable expansion uses a
    /// <c>:-</c> default so it is also <c>set -u</c>-safe.</para>
    /// </summary>
    public static string Preamble { get; } = SharedHelpers + "\n" + DecisionFragment;

    // The preamble's decision half: deliberately NOT part of the embedded
    // shared region — but only the REMEDIATION lives here. The decision
    // skeleton itself (the usable check, the pick chain, the runtime-dir
    // pin) is in the shared region so the preamble and the dot-sourced
    // script can never drift. The divergence is by design: the script
    // quarantines a broken .nuget in place, links a trusted package cache
    // into the healed home, and seeds a readable user config (on-disk
    // repairs covering later execs), while this preamble only needs to keep
    // the single wrapped `dotnet` working and so exports DOTNET_CLI_HOME /
    // NUGET_PACKAGES instead.
    private const string DecisionFragment = """
        nuget_home="${DOTNET_CLI_HOME:-${HOME:-}}"
        nuget_root="${nuget_home}/.nuget"
        nuget_home_resolved="$(_codeybox_adopt_dir "$nuget_home" writable)" \
            || nuget_home_resolved=
        nuget_home_broken=1
        if [ -n "$nuget_home_resolved" ]; then
          # This preamble cannot quarantine a broken .nuget aside and
          # recreate it the way the dot-sourced recovery can — it can only
          # redirect — so the shared usable check runs with "trusted
          # writable": an unwritable .nuget must fail up front rather than
          # after NuGet's own settings-dir create fails.
          if _codeybox_nuget_home_usable \
            "$nuget_home_resolved" trusted writable >/dev/null 2>&1; then
            nuget_home_broken=0
          fi
        fi
        if [ "$nuget_home_broken" -eq 0 ] && [ -n "${DOTNET_CLI_HOME:-}" ]; then
          # Re-export the canonical path so a symlinked env value cannot be
          # retargeted between these checks and the dotnet resolution.
          export DOTNET_CLI_HOME="$nuget_home_resolved"
        fi
        if [ "$nuget_home_broken" -eq 1 ]; then
          writable_home="$(_codeybox_pick_user_dir)" || writable_home=
          if [ -n "$writable_home" ]; then
            export DOTNET_CLI_HOME="$writable_home"
            # Only a verified cache tree is consumable — a cache a co-tenant
            # can write anywhere below its root would satisfy package installs
            # with planted or tampered extracted entries, running attacker
            # code in the wrapped build.
            _codeybox_reuse_nuget_packages "${nuget_root}/packages"
            # The reported paths are env-derived input bound for the
            # terminal/log: strip control characters so a crafted value
            # cannot spoof operator-facing output with ANSI escapes.
            _codeybox_heal_note="$(_codeybox_strip_ctrl "$nuget_root")"
            _codeybox_heal_pkgs="$(_codeybox_strip_ctrl "${NUGET_PACKAGES:-}")"
            echo "nuget-home self-heal: '${_codeybox_heal_note}' is not usable by $(id -un) (root-owned NuGet home?); redirecting DOTNET_CLI_HOME=${DOTNET_CLI_HOME}${NUGET_PACKAGES:+ (reusing package cache ${_codeybox_heal_pkgs})}" >&2
          elif [ -n "${DOTNET_CLI_HOME:-}" ]; then
            # No private fallback could be materialized: the env value this
            # check rejected must not stay exported for the wrapped dotnet —
            # unset it so the CLI falls back to $HOME / the passwd entry.
            unset DOTNET_CLI_HOME
          else
            unset HOME
          fi
        fi
        # A usable NuGet settings home is not enough by itself: since runtime
        # 10.0.13, dotnet first-use creates the user-scoped "NuGet-Migrations"
        # named mutex under $XDG_RUNTIME_DIR, falling back to
        # $HOME/.dotnet/corefx/Runtime/SharedMemory — and NuGet builds that key
        # user paths off $HOME hit the same broken home. When neither variable
        # resolves to verified private storage the CLI aborts before MSBuild
        # with "The system cannot open the device or file specified:
        # 'NuGet-Migrations'" (dotnet/runtime#135726, #136802). The shared pin
        # picks the (possibly just-healed) DOTNET_CLI_HOME, else the shared
        # per-user fallback, else a private mktemp dir; verified values are
        # re-exported under their canonical path.
        codeybox_pin_runtime_dirs
        if [ -n "$_codeybox_pin_home_redirected" ]; then
          # Redirecting HOME would orphan a pre-baked package cache under the
          # original home's .nuget — preserve it for restore through the same
          # shared admission the broken-home branch above uses. The
          # dot-sourced recovery makes the same move material on disk (linked
          # cache + seeded config) because it must also cover children that
          # resolve packages off $HOME without inheriting exports, while this
          # preamble only has to keep the single wrapped `dotnet` working.
          _codeybox_reuse_nuget_packages "${nuget_root}/packages"
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

    // Reads the embedded copy of scripts/nuget-home-heal.sh (embedded by the
    // csproj alongside this class so the shipped text is always the committed
    // file) and returns the text between the shared-helpers sentinels. Missing
    // sentinels are a source-level contract break, not a runtime condition —
    // fail loudly rather than compose a preamble without its guards.
    private static string LoadSharedHelpers()
    {
        var assembly = typeof(NuGetHomeSelfHeal).Assembly;
        using var stream = assembly.GetManifestResourceStream(HealScriptResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{HealScriptResourceName}' is missing; " +
                "CodeyBox.Core.csproj must embed scripts/nuget-home-heal.sh");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var begin = script.IndexOf(SharedHelpersBeginMarker, StringComparison.Ordinal);
        var end = script.IndexOf(SharedHelpersEndMarker, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
        {
            throw new InvalidOperationException(
                $"scripts/nuget-home-heal.sh must keep '{SharedHelpersBeginMarker}' / " +
                $"'{SharedHelpersEndMarker}' sentinels around the shared helper block");
        }

        var contentStart = script.IndexOf('\n', begin) + 1;
        return script[contentStart..end].TrimEnd('\n');
    }

    // Reads the quoted literal of a `_codeybox_*="..."` leaf assignment out of
    // the shared-helpers text, keeping the shell file the single source for
    // the C#-facing directory names. Only simple literals are expected;
    // anything else (command substitution, concatenation) fails loudly.
    private static string ParseLeafConstant(string helpers, string variableName)
    {
        var prefix = variableName + "=\"";
        // Anchor the match at a line start so a prose mention of the variable
        // earlier in the region cannot parse as the real assignment.
        var start = helpers.IndexOf("\n" + prefix, StringComparison.Ordinal);
        if (start >= 0)
            start += 1;
        else if (helpers.StartsWith(prefix, StringComparison.Ordinal))
            start = 0;
        else
            start = -1;
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"scripts/nuget-home-heal.sh shared region must assign {variableName}=\"...\"");
        }

        var valueStart = start + prefix.Length;
        var end = helpers.IndexOf('"', valueStart);
        if (end <= valueStart)
        {
            throw new InvalidOperationException(
                $"{variableName} in scripts/nuget-home-heal.sh must be a simple quoted literal");
        }

        return helpers[valueStart..end];
    }
}
