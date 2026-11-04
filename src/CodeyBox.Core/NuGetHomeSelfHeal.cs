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
/// redirect alone does not cover. This is the single
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
    /// Mirrored as <c>_codeybox_home_leaf</c> in <c>scripts/nuget-home-heal.sh</c>
    /// (shell cannot read this constant); renaming it without updating that
    /// mirror desyncs the fallback selection and migration-marker seeding —
    /// pinned by <c>BuildScriptNuGetHealTests</c>.
    /// </summary>
    public const string WritableHomeLeaf = "codeybox-nuget-home";

    /// <summary>
    /// POSIX shell preamble that exports a redirected <c>DOTNET_CLI_HOME</c> (and,
    /// when reusable, <c>NUGET_PACKAGES</c>) iff the current user cannot use its
    /// <c>~/.nuget</c> settings home, and pins <c>HOME</c>/<c>XDG_RUNTIME_DIR</c>
    /// to a writable per-user dir when either is unusable (required by .NET
    /// 10.0.13+ user-scoped named mutexes such as <c>NuGet-Migrations</c>).
    /// A no-op when the NuGet home, <c>$HOME</c>, and <c>$XDG_RUNTIME_DIR</c>
    /// are all usable.
    ///
    /// <para>"Usable" is deliberately stronger than "-d &amp;&amp; -w": the env
    /// values are untrusted input, so every admitted directory is canonicalized
    /// (cd/pwd -P) and required to be owned by this uid and never
    /// group/other-writable, under an ancestor chain a co-tenant cannot swap —
    /// a merely-writable foreign dir must never hold the NuGet.Config every
    /// restore reads or dotnet's user-scoped mutex storage.</para>
    ///
    /// <para>Assumes the enclosing script already ran <c>set -eu</c>; every
    /// helper invocation is a command substitution guarded by
    /// <c>|| var=</c> (or an <c>if</c>/<c>||</c>/<c>&amp;&amp;</c> member) so a
    /// degraded step — including a total <c>mktemp</c> failure — cannot abort
    /// the script before <c>exec "$@"</c>; the caller-side
    /// <c>[ -n "$var" ]</c> gates then simply skip the pin. Every variable
    /// expansion uses a <c>:-</c> default so it is also <c>set -u</c>-safe.</para>
    /// </summary>
    public const string Preamble = $$$"""
        _codeybox_euid="$(id -u)"
        _codeybox_home_leaf="{{{WritableHomeLeaf}}}"
        nuget_home="${DOTNET_CLI_HOME:-${HOME:-}}"
        nuget_root="${nuget_home}/.nuget"
        nuget_settings_dir="${nuget_root}/NuGet"
        nuget_settings_file="${nuget_settings_dir}/NuGet.Config"
        # The helpers below are spelled exactly like the ones in
        # scripts/nuget-home-heal.sh (which mirrors this whole preamble) so a
        # guard change lands identically in both copies.
        _codeybox_lstat_dir() {
            set -- $(ls -ldn "$1" 2>/dev/null)
            case ${1:-} in
                d*) ;;
                *) return 1 ;;
            esac
            _codeybox_stat_mode=$1
            _codeybox_stat_uid=${3:-}
            [ -n "$_codeybox_stat_uid" ]
        }
        _codeybox_secure_dir() {
            # Empty input must fail before the cd: `cd ""` silently stays in the
            # current directory in several shells, which would "verify" the cwd.
            [ -n "${1:-}" ] || return 1
            _codeybox_sec="$(cd "$1" 2>/dev/null && pwd -P)" || return 1
            if [ "${3:-}" = "writable" ] && [ ! -w "$_codeybox_sec" ]; then
                return 1
            fi
            _codeybox_sec_rule=${2:-private}
            _codeybox_sec_walk=$_codeybox_sec
            _codeybox_sec_leaf=1
            while :; do
                _codeybox_lstat_dir "$_codeybox_sec_walk" || return 1
                if [ "$_codeybox_sec_leaf" -eq 1 ] && [ "$_codeybox_sec_rule" = private ]; then
                    [ "$_codeybox_stat_uid" = "$_codeybox_euid" ] || return 1
                else
                    { [ "$_codeybox_stat_uid" = "$_codeybox_euid" ] \
                        || [ "$_codeybox_stat_uid" = "0" ]; } || return 1
                fi
                case $_codeybox_stat_mode in
                    ?????w*|????????w*)
                        # Leaf rules stricter than anchor never tolerate
                        # group/other write bits; an anchor tolerates them only
                        # under the sticky bit.
                        if [ "$_codeybox_sec_leaf" -eq 1 ] \
                            && [ "$_codeybox_sec_rule" != anchor ]; then
                            return 1
                        fi
                        case $_codeybox_stat_mode in
                            ?????????[tT]*) ;;
                            *) return 1 ;;
                        esac ;;
                esac
                _codeybox_sec_leaf=0
                [ "$_codeybox_sec_walk" = "/" ] && break
                _codeybox_sec_walk=${_codeybox_sec_walk%/*}
                [ -n "$_codeybox_sec_walk" ] || _codeybox_sec_walk=/
            done
            printf '%s\n' "$_codeybox_sec"
        }
        _codeybox_adopt_dir() {
            [ -n "${1:-}" ] || return 1
            if [ -e "$1" ] || [ -L "$1" ]; then
                _codeybox_secure_dir "$1" private "${2:-}"
                return
            fi
            _codeybox_adopt_parent="$(dirname "$1" 2>/dev/null)" || return 1
            _codeybox_adopt_parent="$(_codeybox_secure_dir \
                "$_codeybox_adopt_parent" anchor writable)" || return 1
            mkdir -m 700 "$1" 2>/dev/null || return 1
            _codeybox_secure_dir "$1" private "${2:-}"
        }
        _codeybox_pick_user_dir() {
            _codeybox_pick="${1:-}"
            if [ -n "$_codeybox_pick" ]; then
                _codeybox_pick="$(_codeybox_adopt_dir "$_codeybox_pick" writable)" \
                    || _codeybox_pick=
            fi
            if [ -z "$_codeybox_pick" ]; then
                _codeybox_pick="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-${_codeybox_euid}"
                _codeybox_pick="$(_codeybox_adopt_dir "$_codeybox_pick" writable)" \
                    || _codeybox_pick=
            fi
            if [ -z "$_codeybox_pick" ]; then
                _codeybox_pick="$(mktemp -d \
                    "${TMPDIR:-/tmp}/${_codeybox_home_leaf}.XXXXXX")" || return 1
                _codeybox_pick="$(_codeybox_secure_dir "$_codeybox_pick" private writable)" \
                    || return 1
            fi
            printf '%s\n' "$_codeybox_pick"
        }
        nuget_home_broken=0
        nuget_home_resolved="$(_codeybox_adopt_dir "$nuget_home" writable)" \
            || nuget_home_resolved=
        nuget_settings_resolved="$(_codeybox_secure_dir \
            "$nuget_settings_dir" private writable)" || nuget_settings_resolved=
        if [ -z "$nuget_home_resolved" ]; then
          # The cli home itself is not private per-user storage — foreign-owned,
          # unwritable, or resolving through a planted symlink — so nothing
          # beneath it can keep a NuGet.Config NuGet trusts.
          nuget_home_broken=1
        elif [ -n "$nuget_settings_resolved" ]; then
          { [ -e "$nuget_settings_file" ] && [ ! -r "$nuget_settings_file" ]; } && nuget_home_broken=1
        elif [ -e "$nuget_settings_dir" ] || [ -L "$nuget_settings_dir" ]; then
          # A settings dir that is not private per-user storage — foreign-owned,
          # co-tenant-writable, or a planted symlink — cannot keep a
          # NuGet.Config NuGet trusts either.
          nuget_home_broken=1
        elif [ -d "$nuget_root" ]; then
          # The settings dir is absent: .nuget must be a dir this user (or root)
          # owns with no co-tenant write access for NuGet to create settings in.
          _codeybox_secure_dir "$nuget_root" trusted writable >/dev/null 2>&1 \
            || nuget_home_broken=1
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
            existing_packages="${nuget_root}/packages"
            # Only an owned cache is consumable — a co-tenant-controlled cache
            # would satisfy package installs with planted or tampered extracted
            # entries, running attacker code in the wrapped build.
            _codeybox_existing_packages="$(_codeybox_secure_dir \
              "$existing_packages" trusted)" || _codeybox_existing_packages=
            if [ -z "${NUGET_PACKAGES:-}" ] && [ -n "$_codeybox_existing_packages" ]; then
              export NUGET_PACKAGES="$_codeybox_existing_packages"
            fi
            echo "nuget-home self-heal: '${nuget_root}' is not usable by $(id -un) (root-owned NuGet home?); redirecting DOTNET_CLI_HOME=${DOTNET_CLI_HOME}${NUGET_PACKAGES:+ (reusing package cache ${NUGET_PACKAGES})}" >&2
          fi
        fi
        # A usable NuGet settings home is not enough by itself: since runtime
        # 10.0.13, dotnet first-use creates the user-scoped "NuGet-Migrations"
        # named mutex under $XDG_RUNTIME_DIR, falling back to
        # $HOME/.dotnet/corefx/Runtime/SharedMemory — and NuGet builds that key
        # user paths off $HOME hit the same broken home. When neither variable
        # resolves to verified private storage the CLI aborts before MSBuild
        # with "The system cannot open the device or file specified:
        # 'NuGet-Migrations'" (dotnet/runtime#135726, #136802). Pin each
        # unusable variable to a writable dir: preferring the (possibly
        # just-healed) DOTNET_CLI_HOME, else the shared per-user fallback.
        # Usable values are re-exported under their canonical path so a
        # symlinked env value cannot be retargeted after the check.
        home_resolved="$(_codeybox_secure_dir "${HOME:-}" private writable)" \
          || home_resolved=
        xdg_resolved="$(_codeybox_secure_dir "${XDG_RUNTIME_DIR:-}" private writable)" \
          || xdg_resolved=
        if [ -z "$home_resolved" ] || [ -z "$xdg_resolved" ]; then
          user_dir="$(_codeybox_pick_user_dir "${DOTNET_CLI_HOME:-}")" || user_dir=
          if [ -n "$user_dir" ]; then
            if [ -n "$home_resolved" ]; then
              export HOME="$home_resolved"
            else
              # Redirecting HOME would orphan a pre-baked package cache under
              # the original $HOME/.nuget — preserve it for restore like the
              # broken-home branch does. scripts/nuget-home-heal.sh mirrors
              # this whole block (same DOTNET_CLI_HOME -> shared per-user ->
              # mktemp chain, same private-dir admission) with two intentional
              # differences: it links the cache into the redirected home's
              # .nuget instead of exporting NUGET_PACKAGES, and it seeds a
              # readable user config under the redirected home — both
              # materialize on disk for children that resolve packages or the
              # settings path off $HOME without inheriting the exports, while
              # this preamble only has to keep the single wrapped `dotnet`
              # working (NuGet creates the config itself in a writable home).
              _codeybox_old_packages="${nuget_root}/packages"
              _codeybox_old_resolved="$(_codeybox_secure_dir \
                "$_codeybox_old_packages" trusted)" || _codeybox_old_resolved=
              if [ -z "${NUGET_PACKAGES:-}" ] && [ -n "$_codeybox_old_resolved" ]; then
                export NUGET_PACKAGES="$_codeybox_old_resolved"
              fi
              export HOME="$user_dir"
            fi
            if [ -n "$xdg_resolved" ]; then
              export XDG_RUNTIME_DIR="$xdg_resolved"
            else
              export XDG_RUNTIME_DIR="$user_dir"
            fi
          fi
        else
          export HOME="$home_resolved"
          export XDG_RUNTIME_DIR="$xdg_resolved"
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
