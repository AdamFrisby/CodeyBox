# shellcheck shell=sh
# Single source of truth for the per-user NuGet-home recovery.
#
# NuGet reads its per-user configuration from <cli-home>/.nuget/NuGet and, when
# that file is absent, tries to create it there. <cli-home> defaults to
# DOTNET_CLI_HOME, or $HOME when that is unset. In locked-down / COW-inherited
# sandboxes the inherited directory can be owned by another account or mounted
# read-only, so `dotnet` aborts restore before the build with an
# unauthorized-access error ("Failed to read NuGet.Config due to unauthorized
# access"). NuGet performs that user-config read during restore, so neither an
# in-tree NuGet.Config / RestoreConfigFile (which only redirect package sources,
# not the fatal user-config read) nor a late MSBuild BeforeTargets="Restore" hook
# can heal it — the home must be repaired on disk before that read. An
# InitialTargets hook runs early enough (see below); every call site repairs the
# home, none merely reconfigures sources.
#
# DOT-SOURCE this file (`. scripts/nuget-home-heal.sh`) BEFORE invoking dotnet.
# It never fails the caller (safe under `set -e`), and — because it is
# dot-sourced — can export a fallback DOTNET_CLI_HOME into the caller's
# environment. The settings heal and runtime-dir pinning are strict no-ops on
# an already-usable home; the only write in that case is the idempotent
# migration-marker seeding described below. build.sh and the audit build/test
# gates all source this rather than re-implementing the recovery.
#
# It is ALSO run from MSBuild via an InitialTargets hook (see
# Directory.NuGetHomeHeal.targets, wired into Directory.Build.props and
# Directory.Solution.props). InitialTargets fires at the very start of every
# MSBuild invocation, before NuGet's user-config read, so a plain `dotnet build`
# / `dotnet test` / `dotnet build <solution>` heals itself without any wrapper
# script or orchestrator change — verified before both solution- and
# project-level restore. That MSBuild path relies only on the on-disk repair (an
# exported DOTNET_CLI_HOME would not survive back to the parent `dotnet`), which
# the in-place quarantine below performs whenever the cli-home is writable.

# Single shell-side definition of the two path leaves whose C# constants this
# script must mirror: NuGetHomeSelfHeal.WritableHomeLeaf (the per-user fallback
# home under ${TMPDIR:-/tmp}) and DotnetCliHomeConventions.DirectoryName (the
# stamped repo-local CLI home). Shell cannot read the C# constants, so renaming
# one without updating its mirror silently desyncs the fallback selection and
# the migration-marker seeding — BuildScriptNuGetHealTests pins both pairs.
_codeybox_home_leaf="codeybox-nuget-home"
_codeybox_repo_home_leaf=".dotnet-cli-home"

# Writability is necessary but not sufficient: an inherited NuGet.Config that is
# present but unreadable (e.g. baked mode 0600 under another account, inside a
# directory we can still write) reproduces the same fatal read, so treat that as
# unusable too and let the heal recreate it. Probe with touch (a real command)
# rather than a shell redirect so a failure cannot abort a `set -e` caller.
_codeybox_nuget_home_usable() {
    mkdir -p "$_codeybox_nuget_user_dir" 2>/dev/null || return 1
    if [ -e "$_codeybox_nuget_user_dir/NuGet.Config" ] \
        && [ ! -r "$_codeybox_nuget_user_dir/NuGet.Config" ]; then
        return 1
    fi
    touch "$_codeybox_nuget_probe" 2>/dev/null
}

# Make a freshly created per-user NuGet home usable for the next `dotnet`: link
# in an inherited package cache (so restore stays offline-safe without a
# re-download) and seed a minimal readable user config (so the fatal user-config
# *read* succeeds without depending on first-run creation). $1 is the
# ".../.nuget/NuGet" directory; $2, when non-empty and existing, is the package
# cache to reuse as the sibling global-packages folder. Every step is
# best-effort so a failure can never abort a `set -e` caller. Shared by the
# in-place and DOTNET_CLI_HOME-redirect recovery paths so both stay offline-safe.
_codeybox_seed_nuget_home() {
    _codeybox_seed_user_dir="$1"
    _codeybox_seed_cache_src="$2"
    mkdir -p "$_codeybox_seed_user_dir" 2>/dev/null || return 0
    # Strip the trailing "/NuGet" to address the .nuget root that owns the
    # global-packages folder alongside the user config directory.
    _codeybox_seed_root="${_codeybox_seed_user_dir%/NuGet}"
    # NuGet only READS an already-extracted package (it checks the
    # .nupkg.metadata marker and skips extraction), so a read-only inherited
    # cache satisfies every restore; a writable one additionally lets NuGet add
    # packages the baseline lacks.
    if [ -n "$_codeybox_seed_cache_src" ] \
        && [ -e "$_codeybox_seed_cache_src" ] \
        && [ ! -e "$_codeybox_seed_root/packages" ]; then
        ln -s "$_codeybox_seed_cache_src" "$_codeybox_seed_root/packages" 2>/dev/null || true
    fi
    # Repository package sources come from RestoreConfigFile, so an empty user
    # config adds no overrides and clears no sources. Two execs can share the
    # per-user fallback home and race this write, so render to a sibling temp
    # and install with `ln` — an atomic no-clobber create that can never leave
    # a half-written config behind or overwrite one a peer (or NuGet) wrote
    # first. The staging file comes from mktemp (an unpredictable O_EXCL
    # create): a PID-predictable `>` write would follow a co-tenant-planted
    # symlink at the temp path, turning the seed into an arbitrary-path file
    # create/truncate as this user.
    if [ ! -e "$_codeybox_seed_user_dir/NuGet.Config" ]; then
        _codeybox_seed_tmp="$(mktemp \
            "$_codeybox_seed_user_dir/.NuGet.Config.tmp.XXXXXX" 2>/dev/null)" \
            || _codeybox_seed_tmp=
        if [ -n "$_codeybox_seed_tmp" ] && printf '%s\n' \
            '<?xml version="1.0" encoding="utf-8"?>' \
            '<configuration />' \
            > "$_codeybox_seed_tmp" 2>/dev/null; then
            ln "$_codeybox_seed_tmp" "$_codeybox_seed_user_dir/NuGet.Config" 2>/dev/null || true
        fi
        rm -f "$_codeybox_seed_tmp" 2>/dev/null || true
    fi
}

codeybox_heal_nuget_home() {
    _codeybox_cli_home="${DOTNET_CLI_HOME:-${HOME:-}}"
    [ -n "$_codeybox_cli_home" ] || return 0
    _codeybox_nuget_user_dir="$_codeybox_cli_home/.nuget/NuGet"
    _codeybox_nuget_probe="$_codeybox_nuget_user_dir/.codeybox-writable-probe"

    if _codeybox_nuget_home_usable; then
        rm -f "$_codeybox_nuget_probe" 2>/dev/null || true
        return 0
    fi

    # The inherited per-user NuGet directory is not usable — e.g. an image baked
    # $HOME/.nuget owned by root. When the cli-home itself is writable, relocate
    # the broken directory aside (preserving it, not deleting) and recreate a
    # writable one, so EVERY later `dotnet` in this environment can write its
    # per-user config — not just the current process, which a DOTNET_CLI_HOME
    # override would be limited to.
    if [ -w "$_codeybox_cli_home" ]; then
        _codeybox_nuget_root="$_codeybox_cli_home/.nuget"
        _codeybox_quarantine="$_codeybox_nuget_root.codeybox-unwritable.$$"
        if [ ! -e "$_codeybox_nuget_root" ] \
            || mv "$_codeybox_nuget_root" "$_codeybox_quarantine" 2>/dev/null; then
            if _codeybox_nuget_home_usable; then
                rm -f "$_codeybox_nuget_probe" 2>/dev/null || true
                # Reuse the quarantined package cache and seed a readable user
                # config so the next `dotnet`'s user-config *read* (the exact
                # operation the broken home fails) succeeds and restore stays
                # offline-safe.
                _codeybox_seed_nuget_home "$_codeybox_nuget_user_dir" \
                    "$_codeybox_quarantine/packages"
                return 0
            fi
        fi
    fi

    # The cli-home itself is not writable (e.g. an inherited read-only mount that
    # cannot be relocated aside): keep the build hermetic by redirecting the .NET
    # CLI home to a writable per-user directory for this process tree, chosen by
    # the shared _codeybox_pick_user_dir chain — the shared per-user dir, else a
    # private mktemp dir; same chain as the C# preamble
    # (NuGetHomeSelfHeal.Preamble). Seed the chosen home the same way as the
    # in-place path — a readable user config so the fatal read succeeds, and a
    # symlink to the inherited (still readable but unwritable) package cache so
    # restore stays offline-safe there too.
    _codeybox_scratch_home="$(_codeybox_pick_user_dir)" || return 0
    DOTNET_CLI_HOME="$_codeybox_scratch_home"
    export DOTNET_CLI_HOME
    _codeybox_seed_nuget_home "$_codeybox_scratch_home/.nuget/NuGet" \
        "$_codeybox_cli_home/.nuget/packages"
    return 0
}

# True when $1 names an existing directory the current user can write — the
# probe applied to HOME / XDG_RUNTIME_DIR below.
_codeybox_dir_usable() {
    [ -n "${1:-}" ] && [ -d "$1" ] && [ -w "$1" ]
}

# Print a writable per-user home dir for the heal's redirected-home and
# runtime-dir fallbacks; returns nonzero when no candidate could be obtained.
# $1, when non-empty, is the preferred candidate (the possibly just-redirected
# DOTNET_CLI_HOME). It is canonicalized so a legitimately symlinked home
# resolves, then admitted only when the RESOLVED directory is owned by this
# uid and writable — DOTNET_CLI_HOME is environment input, and a co-tenant- or
# work-branch-planted symlink (or a foreign-owned dir) must not redirect the
# seeding below and dotnet's user-scoped mutex/runtime storage into
# foreign-controlled directories. Otherwise the shared per-user dir — kept
# shared so one healed home, its seeded cache, and the mutex location are
# stable across execs — under the same ownership requirements (a squatted or
# root-left directory in the world-writable temp is refused), else a private
# mktemp dir. Same chain as the C# preamble's _codeybox_pick_user_dir.
_codeybox_pick_user_dir() {
    _codeybox_pick="${1:-}"
    if [ -n "$_codeybox_pick" ]; then
        mkdir -p "$_codeybox_pick" 2>/dev/null || true
        # The `||` fallback keeps the assignment's status at zero: a bare
        # `var=$(cd ...)` would propagate the failed substitution's status and
        # abort a `set -e` caller merely because the candidate is absent.
        _codeybox_pick="$(cd "$_codeybox_pick" 2>/dev/null && pwd -P)" \
            || _codeybox_pick=
    fi
    if [ -z "$_codeybox_pick" ] || [ ! -O "$_codeybox_pick" ] \
        || [ ! -w "$_codeybox_pick" ]; then
        _codeybox_pick="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-$(id -u)"
        mkdir -p "$_codeybox_pick" 2>/dev/null || true
        if [ ! -d "$_codeybox_pick" ] || [ ! -O "$_codeybox_pick" ] \
            || [ ! -w "$_codeybox_pick" ]; then
            _codeybox_pick="$(mktemp -d \
                "${TMPDIR:-/tmp}/${_codeybox_home_leaf}.XXXXXX")" || return 1
        fi
    fi
    printf '%s\n' "$_codeybox_pick"
}

# The NuGet-settings heal above does not cover dotnet's OTHER per-user runtime
# needs. Since runtime 10.0.13, user-scoped named mutexes — NuGet's first-use
# "NuGet-Migrations" among them — resolve under $XDG_RUNTIME_DIR, falling back
# to $HOME/.dotnet/corefx/Runtime/SharedMemory, and NuGet builds that key user
# paths off $HOME ignore DOTNET_CLI_HOME and hit the same broken home. On
# images whose $HOME is unavailable (the same broken-home images this file
# heals), the CLI then aborts before MSBuild with "The system cannot open the
# device or file specified: 'NuGet-Migrations'" (dotnet/runtime#135726,
# #136802). Pin each unusable variable to a writable dir for this dot-sourced
# process tree. The fallback chain matches the C# preamble so both copies pick
# the same dir: the (possibly just-redirected) DOTNET_CLI_HOME, else the shared
# per-user dir — kept shared so the user-scoped mutex location is stable across
# execs — else a private mktemp dir. Strict no-op when both are usable.
codeybox_ensure_dotnet_runtime_dir() {
    if _codeybox_dir_usable "${HOME:-}" \
        && _codeybox_dir_usable "${XDG_RUNTIME_DIR:-}"; then
        return 0
    fi

    # The env-provided DOTNET_CLI_HOME is only the preferred candidate — the
    # helper canonicalizes it and refuses anything not owned by this uid before
    # falling through to the shared per-user dir, else a private mktemp dir.
    _codeybox_user_dir="$(_codeybox_pick_user_dir "${DOTNET_CLI_HOME:-}")" \
        || return 0
    if ! _codeybox_dir_usable "${HOME:-}"; then
        # Redirecting HOME would orphan a pre-baked package cache under the
        # original $HOME/.nuget — seed the replacement home (readable user
        # config + linked cache) before the redirect, exactly like the
        # scratch-home path above. The C# preamble exports NUGET_PACKAGES
        # instead: equivalent for the single process it wraps, while this
        # script materializes the cache inside the home so a child that
        # resolves packages off $HOME without inheriting NUGET_PACKAGES is
        # covered too.
        _codeybox_seed_nuget_home "$_codeybox_user_dir/.nuget/NuGet" \
            "${HOME:-}/.nuget/packages"
        HOME="$_codeybox_user_dir"
        export HOME
    fi
    if ! _codeybox_dir_usable "${XDG_RUNTIME_DIR:-}"; then
        XDG_RUNTIME_DIR="$_codeybox_user_dir"
        export XDG_RUNTIME_DIR
    fi
    return 0
}

# The runtime-dir pinning above only fixes THIS process tree: exported values
# die with the exec that sourced this file. Audit gates run as separate execs
# whose environment we do not control — the `sh -c` preamble wrapping them is
# baked into the orchestrator binary and can predate the HOME/XDG pinning —
# and the named-mutex directory is resolved from the launch environment, so an
# MSBuild-time `Environment.SetEnvironmentVariable` cannot help either. What a
# broken runtime dir actually breaks is narrower than dotnet in general: the
# "NuGet-Migrations" mutex is created only while NuGet's applied-migration
# marker is ABSENT. MigrationRunner checks
# File.Exists(<user-profile>/.local/share/NuGet/Migrations/1) — or
# $XDG_DATA_HOME/NuGet/Migrations/1 — BEFORE creating the mutex
# (NuGet.Common.Migrations.MigrationRunner), so when the marker is present,
# first-use and restore skip the mutex outright and the launch env's runtime
# dir is never consulted.
#
# Seed that marker under every home a later exec in this sandbox could resolve:
# the stamped repo-local CLI home (DotnetCliHomeConventions pins HOME/
# DOTNET_CLI_HOME to <workdir>/.dotnet-cli-home for raw dotnet execs), the
# ambient $HOME/$DOTNET_CLI_HOME, the shared per-user fallback both preamble
# versions redirect to, the passwd-entry home (the UserProfile fallback when
# $HOME is unset), and $XDG_DATA_HOME's marker layout when that is set. The
# marker is the exact state NuGet leaves after a no-op migration run — an empty
# file — and File.Exists only needs the path to be readable, which is precisely
# what distinguishes it from the SharedMemory dir the mutex would have needed
# to CREATE. Seeding is strictly best-effort: homes that cannot be written are
# skipped (a marker cannot be planted there either), and an existing marker is
# never touched so real migration state is preserved.
#
# The candidates are less-trusted-influenceable paths — a predictable name in
# the world-writable ${TMPDIR:-/tmp} can be squatted by a co-tenant, and any of
# them can contain a planted symlink — so seeding must never become a
# confused-deputy write into an attacker-chosen directory: `mkdir -p` descends
# through symlinked intermediate components and `: >` writes through a symlink
# leaf. Every candidate is therefore canonicalized and required to be a real
# directory owned by this uid before anything is written under it, each
# Migrations-tree component is created level by level while refusing to
# descend through a symlink, and a symlink at the marker leaf is never
# written through.
#
# Materialize the missing leaf of a heal-controlled candidate home. Single
# `mkdir`, never `-p`: the usual parent (${TMPDIR:-/tmp} or the checkout root)
# already exists; a missing one (an env-provided DOTNET_CLI_HOME can point
# anywhere) simply fails the mkdir and the seeder's checks then skip the
# candidate. A squatted or planted path at that leaf likewise fails the mkdir
# and the seeder's -O re-check alike. Owner-only mode so no co-tenant can
# plant symlinks inside between creation and use.
_codeybox_ensure_owned_home() {
    [ -n "${1:-}" ] || return 0
    if [ ! -e "$1" ] && [ ! -L "$1" ]; then
        mkdir -m 700 "$1" 2>/dev/null || true
    fi
}

# Seed NuGet's applied-migration marker under candidate home $1, at relative
# path $2 (".local/share/NuGet/Migrations" for a home, "NuGet/Migrations" for
# $XDG_DATA_HOME). Safe to call with anything: a missing, non-directory,
# foreign-owned, or foreign-owned-resolving candidate is skipped, as is any
# symlink found while descending.
_codeybox_seed_migration_marker() {
    [ -n "${1:-}" ] || return 0
    # Canonicalize so a legitimately symlinked home still resolves; then
    # require the resolved directory be owned by this uid — the guard the
    # preamble's WritableHomeLeaf comment already promises for a squatted or
    # root-left directory in a world-writable temp.
    # The `||` fallback keeps the assignment's status at zero: a bare
    # `var=$(cd ...)` would propagate the failed substitution's status and
    # abort a `set -e` caller merely because a candidate is absent.
    _codeybox_marker_base="$(cd "$1" 2>/dev/null && pwd -P)" \
        || _codeybox_marker_base=
    if [ -z "$_codeybox_marker_base" ] || [ ! -O "$_codeybox_marker_base" ]; then
        return 0
    fi
    _codeybox_marker_dir="$_codeybox_marker_base/$2"
    case " ${_codeybox_seen_marker_dirs:-} " in
        *" $_codeybox_marker_dir "*) return 0 ;;
    esac
    _codeybox_seen_marker_dirs="${_codeybox_seen_marker_dirs:-}$_codeybox_marker_dir "

    # Descend one component at a time rather than `mkdir -p`: a planted
    # symlink at any intermediate level would otherwise be followed, planting
    # the marker tree under a directory the candidate's owner never chose.
    _codeybox_marker_walk="$_codeybox_marker_base"
    _codeybox_marker_rel="$2"
    while [ -n "$_codeybox_marker_rel" ]; do
        case $_codeybox_marker_rel in
            */*)
                _codeybox_marker_part=${_codeybox_marker_rel%%/*}
                _codeybox_marker_rel=${_codeybox_marker_rel#*/}
                ;;
            *)
                _codeybox_marker_part=$_codeybox_marker_rel
                _codeybox_marker_rel=
                ;;
        esac
        _codeybox_marker_walk="$_codeybox_marker_walk/$_codeybox_marker_part"
        [ -L "$_codeybox_marker_walk" ] && return 0
        if [ ! -d "$_codeybox_marker_walk" ]; then
            mkdir "$_codeybox_marker_walk" 2>/dev/null || return 0
        fi
    done

    _codeybox_marker="$_codeybox_marker_walk/1"
    # An existing marker (or a symlink placeholder, which `-e` alone cannot
    # see when dangling) is real migration state — never overwrite it and
    # never write through a symlink.
    { [ -e "$_codeybox_marker" ] || [ -L "$_codeybox_marker" ]; } && return 0
    : > "$_codeybox_marker" 2>/dev/null || true
    return 0
}

_codeybox_seed_home_migration_marker() {
    _codeybox_seed_migration_marker "$1" ".local/share/NuGet/Migrations"
}

codeybox_seed_nuget_migration_state() {
    _codeybox_seen_marker_dirs=" "
    _codeybox_seed_home_migration_marker "${HOME:-}"
    # Stamped/redirected homes the heal itself materializes may not exist yet;
    # create just the leaf, then let the seeder's ownership checks re-verify.
    _codeybox_ensure_owned_home "${DOTNET_CLI_HOME:-}"
    _codeybox_seed_home_migration_marker "${DOTNET_CLI_HOME:-}"
    # DotnetCliHomeConventions.ResolvePath(WorkingDirectory) — the repo-local
    # home the orchestrator stamps onto raw dotnet auditor execs;
    # _codeybox_repo_home_leaf mirrors DotnetCliHomeConventions.DirectoryName
    # (see the constants block at the top of this file). Only the repository
    # root can be that working directory — an MSBuild-Exec'd run has a project
    # directory as its cwd and must not grow a stray home there.
    if [ -f "$(pwd -P)/scripts/nuget-home-heal.sh" ]; then
        _codeybox_repo_cli_home="$(pwd -P)/${_codeybox_repo_home_leaf}"
        _codeybox_ensure_owned_home "$_codeybox_repo_cli_home"
        _codeybox_seed_home_migration_marker "$_codeybox_repo_cli_home"
    fi
    # The shared per-user fallback both preamble generations redirect to.
    _codeybox_fallback_home="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-$(id -u)"
    _codeybox_ensure_owned_home "$_codeybox_fallback_home"
    _codeybox_seed_home_migration_marker "$_codeybox_fallback_home"
    # Environment.GetFolderPath(UserProfile) falls back to the passwd entry
    # when $HOME is unset — cover a gate exec that resolves no HOME at all.
    # `getent` does not exist on macOS, where this candidate silently skips.
    _codeybox_seed_home_migration_marker \
        "$(getent passwd "$(id -u)" 2>/dev/null | cut -d: -f6)"
    # $XDG_DATA_HOME relocates the whole Migrations tree (no .local/share) —
    # same owned-dir and no-symlink rules via the shared helper.
    if [ -n "${XDG_DATA_HOME:-}" ]; then
        _codeybox_seed_migration_marker "$XDG_DATA_HOME" "NuGet/Migrations"
    fi
    return 0
}

codeybox_heal_nuget_home
# Run AFTER the home heal so a just-redirected DOTNET_CLI_HOME is reused as the
# runtime dir rather than spending a second scratch dir.
codeybox_ensure_dotnet_runtime_dir
# Run LAST so the resolved (possibly just-healed) home values are among the
# seeded candidates.
codeybox_seed_nuget_migration_state
