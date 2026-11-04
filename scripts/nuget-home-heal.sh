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
# an already-usable home; the only writes in that case are the transient
# create-then-delete writability probe and the idempotent migration-marker
# seeding described below. build.sh and the audit build/test gates all source
# this rather than re-implementing the recovery.
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
#
# HOME, DOTNET_CLI_HOME, XDG_RUNTIME_DIR and friends are launch-env input:
# a merely-writable directory is not proof of ownership. Every path this file
# writes under or exports is therefore admitted only after it canonicalizes to
# storage owned by this uid and not writable by any co-tenant (the "private"
# rule below), or — for data we only consume, like an image-baked package
# cache — owned by this uid or root (the "trusted" rule). Anything else falls
# through to an owned fallback instead of being trusted because it was
# writable.

# Single shell-side definition of the two path leaves whose C# constants this
# script must mirror: NuGetHomeSelfHeal.WritableHomeLeaf (the per-user fallback
# home under ${TMPDIR:-/tmp}) and DotnetCliHomeConventions.DirectoryName (the
# stamped repo-local CLI home). Shell cannot read the C# constants, so renaming
# one without updating its mirror silently desyncs the fallback selection and
# the migration-marker seeding — BuildScriptNuGetHealTests pins both pairs.
_codeybox_home_leaf="codeybox-nuget-home"
_codeybox_repo_home_leaf=".dotnet-cli-home"
_codeybox_euid="$(id -u)"

# Populate _codeybox_stat_mode (the "drwxr-xr-x" field) and _codeybox_stat_uid
# (numeric owner uid) for directory $1 from a single `ls -ldn` lstat — the two
# attributes the ownership checks below need, without relying on find(1)
# option dialects (-perm / -uid spellings differ across GNU, BSD, and busybox).
# Fails when $1 is not a directory — including a symlink, which reports mode
# "l...": callers canonicalize first, so a link seen here is a mid-check swap
# and must not be treated as the verified directory.
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

# Canonicalize directory $1 (cd/pwd -P, so a planted or legitimate symlink
# resolves to its real target) and print the resolved path when the whole
# chain is safe to rely on. The leaf is judged by the rule named in $2:
#
#   private — owned by this uid and never group/other-writable. The only
#             acceptable state for storage we export (a healed HOME, the
#             user-scoped mutex dir), probe-write into, or seed under: a
#             merely-writable foreign dir — a co-tenant 777 dir, or a
#             stamped path resolving through a planted symlink into one —
#             otherwise leaves the NuGet.Config every restore reads, and the
#             writes below, inside attacker-controlled storage.
#   trusted — like private but root ownership is admitted too, for data we
#             only CONSUME read-only (an image-baked package cache); the
#             platform's root is already trusted, a third uid is not.
#   anchor  — owned by this uid or root, with group/other-writable tolerated
#             only under the sticky bit (the /tmp rule: co-tenants may add
#             entries but can never rename or replace ours). Also the rule
#             applied to every ANCESTOR of the leaf, so nothing along the
#             chain can be swapped out after these checks.
# $3 "writable" additionally requires the leaf be writable by this user.
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

# Print the canonical path of directory $1 when it is — or can safely become —
# private per-user storage. An existing entry is admitted only under the
# "private" rule above; no mkdir runs against an unvalidated path (a bare
# `mkdir -p` would descend planted symlinked intermediates — an attacker-chosen
# dir-create — and a leaf created under a co-tenant-writable parent could be
# swapped for a symlink before the check). A missing leaf is created only
# after the parent verifies as a writable anchor, then by ONE `mkdir -m 700`
# — never -p — so the new dir is born owned by this uid with no writable
# window for a co-tenant plant. $2 "writable" additionally requires the leaf
# be writable by us.
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

# Print a writable per-user home dir for the heal's redirected-home and
# runtime-dir fallbacks; returns nonzero when no candidate could be obtained.
# $1, when non-empty, is the preferred candidate (the possibly just-redirected
# DOTNET_CLI_HOME). Every candidate is adopted only as verified private
# storage (canonicalized, owned by this uid, writable, not
# group/other-writable, under a safe ancestor chain) — DOTNET_CLI_HOME is
# environment input, and a co-tenant- or work-branch-planted symlink, a
# foreign-owned dir, or a squatted predictable path must not redirect the
# seeding below and dotnet's user-scoped mutex/runtime storage into
# foreign-controlled directories. The shared per-user dir — kept shared so
# one healed home, its seeded cache, and the mutex location are stable across
# execs — is adopted under the same terms, else a private mktemp dir, itself
# re-verified because TMPDIR is env input too. Same chain as the C# preamble's
# _codeybox_pick_user_dir.
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

# Writability is necessary but not sufficient: an inherited NuGet.Config that
# is present but unreadable (e.g. baked mode 0600 under another account, inside
# a directory we can still write) reproduces the same fatal read, so treat that
# as unusable too and let the heal recreate it. The resolved settings dir must
# additionally be "private" per-user storage — a merely-writable or
# foreign-owned one cannot keep a NuGet.Config NuGet trusts. Probe with touch
# (a real command) rather than a shell redirect so a failure cannot abort a
# `set -e` caller. The caller sets _codeybox_nuget_root and
# _codeybox_nuget_user_dir under a cli-home it already verified private.
_codeybox_nuget_home_usable() {
    # Never descend through a symlinked .nuget or NuGet component — `mkdir -p`
    # would create the settings dir inside whatever directory the link's owner
    # chose; report unusable so the caller relocates the entry aside instead.
    { [ -L "$_codeybox_nuget_root" ] || [ -L "$_codeybox_nuget_user_dir" ]; } \
        && return 1
    # An existing .nuget must not admit co-tenant writes at all — a
    # group/other-writable one would let a co-tenant plant the packages/
    # cache or the settings dir NuGet trusts — so require it be owned by this
    # uid or root with no group/other write bits.
    if [ -d "$_codeybox_nuget_root" ]; then
        _codeybox_secure_dir "$_codeybox_nuget_root" trusted >/dev/null 2>&1 \
            || return 1
    fi
    mkdir -p "$_codeybox_nuget_user_dir" 2>/dev/null || return 1
    _codeybox_nuget_resolved="$(_codeybox_secure_dir \
        "$_codeybox_nuget_user_dir" private writable)" || return 1
    if [ -e "$_codeybox_nuget_resolved/NuGet.Config" ] \
        && [ ! -r "$_codeybox_nuget_resolved/NuGet.Config" ]; then
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
    # Never create or seed through a symlinked .nuget / NuGet component — the
    # link resolves into whatever directory its owner chose.
    { [ -L "$_codeybox_seed_user_dir" ] \
        || [ -L "${_codeybox_seed_user_dir%/NuGet}" ]; } && return 0
    mkdir -p "$_codeybox_seed_user_dir" 2>/dev/null || return 0
    # The resolved settings dir must stay private per-user storage.
    _codeybox_seed_resolved="$(_codeybox_secure_dir \
        "$_codeybox_seed_user_dir" private writable)" || return 0
    # Strip the trailing "/NuGet" to address the .nuget root that owns the
    # global-packages folder alongside the user config directory.
    _codeybox_seed_root="${_codeybox_seed_resolved%/NuGet}"
    # NuGet only READS an already-extracted package (it checks the
    # .nupkg.metadata marker and skips extraction), so a read-only inherited
    # cache satisfies every restore; a writable one additionally lets NuGet add
    # packages the baseline lacks. But readable is not trustworthy: only a
    # cache whose canonical dir is owned by this uid or root and never
    # group/other-writable may be consumed — a co-tenant-controlled cache
    # would satisfy package installs with planted or tampered extracted
    # entries, running attacker code in the gate build.
    _codeybox_seed_cache=
    if [ -n "$_codeybox_seed_cache_src" ]; then
        _codeybox_seed_cache="$(_codeybox_secure_dir \
            "$_codeybox_seed_cache_src" trusted)" || _codeybox_seed_cache=
    fi
    if [ -n "$_codeybox_seed_cache" ] \
        && [ ! -e "$_codeybox_seed_root/packages" ] \
        && [ ! -L "$_codeybox_seed_root/packages" ]; then
        ln -s "$_codeybox_seed_cache" "$_codeybox_seed_root/packages" 2>/dev/null || true
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
    if [ ! -e "$_codeybox_seed_resolved/NuGet.Config" ]; then
        _codeybox_seed_tmp="$(mktemp \
            "$_codeybox_seed_resolved/.NuGet.Config.tmp.XXXXXX" 2>/dev/null)" \
            || _codeybox_seed_tmp=
        if [ -n "$_codeybox_seed_tmp" ] && printf '%s\n' \
            '<?xml version="1.0" encoding="utf-8"?>' \
            '<configuration />' \
            > "$_codeybox_seed_tmp" 2>/dev/null; then
            ln "$_codeybox_seed_tmp" "$_codeybox_seed_resolved/NuGet.Config" 2>/dev/null || true
        fi
        rm -f "$_codeybox_seed_tmp" 2>/dev/null || true
    fi
}

codeybox_heal_nuget_home() {
    _codeybox_cli_home_env="${DOTNET_CLI_HOME:-${HOME:-}}"
    [ -n "$_codeybox_cli_home_env" ] || return 0

    # Admit the env-influenced cli home only as verified private per-user
    # storage BEFORE any write beneath it: canonicalized so a planted or
    # legitimate symlink resolves to the real directory, owned by this uid,
    # writable, and never group/other-writable — a merely-writable foreign dir
    # would otherwise keep the NuGet.Config every restore reads, the
    # writability probe, and the quarantine writes below inside
    # co-tenant-controlled storage. A missing leaf is materialized only under
    # a verified anchor parent (a stamped repo-local home is env-only until
    # first use). Anything else falls through to the owned redirect chain at
    # the bottom.
    _codeybox_cli_home="$(_codeybox_adopt_dir \
        "$_codeybox_cli_home_env" writable)" || _codeybox_cli_home=
    if [ -n "$_codeybox_cli_home" ]; then
        # Re-export the canonical path under the variable it arrived as, so a
        # symlinked env value cannot be retargeted between these checks and a
        # later dotnet resolution.
        if [ -n "${DOTNET_CLI_HOME:-}" ]; then
            DOTNET_CLI_HOME="$_codeybox_cli_home"
            export DOTNET_CLI_HOME
        else
            HOME="$_codeybox_cli_home"
            export HOME
        fi
        _codeybox_nuget_root="$_codeybox_cli_home/.nuget"
        _codeybox_nuget_user_dir="$_codeybox_nuget_root/NuGet"
        _codeybox_nuget_probe="$_codeybox_nuget_user_dir/.codeybox-writable-probe"

        if _codeybox_nuget_home_usable; then
            rm -f "$_codeybox_nuget_probe" 2>/dev/null || true
            return 0
        fi

        # The inherited per-user NuGet directory is not usable — e.g. an image
        # baked $HOME/.nuget owned by root, or a planted link. The cli home is
        # verified private storage above, so relocate the broken entry aside
        # (preserving it, not deleting) and recreate a writable one, so EVERY
        # later `dotnet` in this environment can write its per-user config —
        # not just the current process, which a DOTNET_CLI_HOME override would
        # be limited to. The -L arm matters: a dangling symlink reports -e
        # false yet must still be moved aside, never created through.
        _codeybox_quarantine="$_codeybox_nuget_root.codeybox-unwritable.$$"
        if { [ ! -e "$_codeybox_nuget_root" ] && [ ! -L "$_codeybox_nuget_root" ]; } \
            || mv "$_codeybox_nuget_root" "$_codeybox_quarantine" 2>/dev/null; then
            if _codeybox_nuget_home_usable; then
                rm -f "$_codeybox_nuget_probe" 2>/dev/null || true
                # Reuse the quarantined package cache and seed a readable user
                # config so the next `dotnet`'s user-config *read* (the exact
                # operation the broken home fails) succeeds and restore stays
                # offline-safe. The seeder links the cache only when its
                # canonical dir is owned by this uid or root.
                _codeybox_seed_nuget_home "$_codeybox_nuget_user_dir" \
                    "$_codeybox_quarantine/packages"
                return 0
            fi
        fi
    fi

    # The cli-home itself is not safely usable — e.g. an inherited read-only
    # mount that cannot be relocated aside, or foreign-controlled storage:
    # keep the build hermetic by redirecting the .NET CLI home to a verified
    # private per-user directory for this process tree, chosen by the shared
    # _codeybox_pick_user_dir chain — the shared per-user dir, else a private
    # mktemp dir; same chain as the C# preamble (NuGetHomeSelfHeal.Preamble).
    # Seed the chosen home the same way as the in-place path — a readable user
    # config so the fatal read succeeds, and a link to the inherited (trusted)
    # package cache so restore stays offline-safe there too.
    _codeybox_scratch_home="$(_codeybox_pick_user_dir)" || return 0
    DOTNET_CLI_HOME="$_codeybox_scratch_home"
    export DOTNET_CLI_HOME
    _codeybox_seed_nuget_home "$_codeybox_scratch_home/.nuget/NuGet" \
        "$_codeybox_cli_home_env/.nuget/packages"
    return 0
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
# process tree.
#
# HOME / XDG_RUNTIME_DIR are launch-env input too, so "usable" here means the
# full "private" rule — canonicalized, owned by this uid, writable, never
# group/other-writable, under a safe ancestor chain — not merely -d/-w: a
# co-tenant-writable or foreign-owned dir must not hold the mutex storage or
# the $HOME-keyed NuGet paths either. Usable values are re-exported under
# their canonical path so a symlinked env value cannot be retargeted after the
# check; unusable ones are pinned to a verified dir from the shared pick
# chain — the (possibly just-redirected) DOTNET_CLI_HOME, else the shared
# per-user dir, else a private mktemp dir — the same chain as the C# preamble,
# so both copies pick the same dir.
codeybox_ensure_dotnet_runtime_dir() {
    _codeybox_home_dir="$(_codeybox_secure_dir "${HOME:-}" private writable)" \
        || _codeybox_home_dir=
    _codeybox_xdg_dir="$(_codeybox_secure_dir "${XDG_RUNTIME_DIR:-}" private writable)" \
        || _codeybox_xdg_dir=

    if [ -n "$_codeybox_home_dir" ] && [ -n "$_codeybox_xdg_dir" ]; then
        HOME="$_codeybox_home_dir"
        export HOME
        XDG_RUNTIME_DIR="$_codeybox_xdg_dir"
        export XDG_RUNTIME_DIR
        return 0
    fi

    _codeybox_user_dir="$(_codeybox_pick_user_dir "${DOTNET_CLI_HOME:-}")" \
        || return 0
    if [ -n "$_codeybox_home_dir" ]; then
        HOME="$_codeybox_home_dir"
    else
        # Redirecting HOME would orphan a pre-baked package cache under the
        # original $HOME/.nuget — seed the replacement home (readable user
        # config + linked trusted cache) before the redirect, exactly like the
        # scratch-home path above. The C# preamble exports NUGET_PACKAGES
        # instead: equivalent for the single process it wraps, while this
        # script materializes the cache inside the home so a child that
        # resolves packages off $HOME without inheriting NUGET_PACKAGES is
        # covered too.
        _codeybox_seed_nuget_home "$_codeybox_user_dir/.nuget/NuGet" \
            "${HOME:-}/.nuget/packages"
        HOME="$_codeybox_user_dir"
    fi
    export HOME
    if [ -n "$_codeybox_xdg_dir" ]; then
        XDG_RUNTIME_DIR="$_codeybox_xdg_dir"
    else
        XDG_RUNTIME_DIR="$_codeybox_user_dir"
    fi
    export XDG_RUNTIME_DIR
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
# leaf. Every candidate is therefore admitted only as verified private
# per-user storage — canonicalized, owned by this uid, writable, never
# group/other-writable, under a safe ancestor chain (the same
# _codeybox_secure_dir check every other admission in this file uses, so a
# co-tenant cannot swap a marker-tree component for a symlink between the
# checks and the write) — each Migrations-tree component is then created level
# by level while refusing to descend through a symlink, and a symlink at the
# marker leaf is never written through.
#
# Materialize a heal-controlled candidate home leaf via the shared adopt
# chain: a missing leaf is created with ONE mode-700 `mkdir` under a verified
# anchor parent — never `-p`, which would descend planted symlinked
# intermediates — and an existing entry must already be private per-user
# storage (a squatted or planted path is refused). The seeder's private-dir
# checks re-verify either way.
_codeybox_ensure_owned_home() {
    [ -n "${1:-}" ] || return 0
    _codeybox_adopt_dir "$1" writable >/dev/null 2>&1 || true
}

# Seed NuGet's applied-migration marker under candidate home $1, at relative
# path $2 (".local/share/NuGet/Migrations" for a home, "NuGet/Migrations" for
# $XDG_DATA_HOME). Safe to call with anything: a missing, non-directory,
# foreign-owned, co-tenant-writable, or foreign-owned-resolving candidate is
# skipped, as is any symlink found while descending.
_codeybox_seed_migration_marker() {
    [ -n "${1:-}" ] || return 0
    # Admit the candidate only as verified private per-user storage — the
    # guard the preamble's WritableHomeLeaf comment already promises for a
    # squatted or root-left directory in a world-writable temp, now with the
    # full chain check so nothing above the base can be swapped for a symlink
    # between the checks and the write.
    # The `||` fallback keeps the assignment's status at zero: a bare
    # `var=$(cd ...)` would propagate the failed substitution's status and
    # abort a `set -e` caller merely because a candidate is absent.
    _codeybox_marker_base="$(_codeybox_secure_dir "$1" private writable)" \
        || _codeybox_marker_base=
    [ -n "$_codeybox_marker_base" ] || return 0
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
    # create just the leaf, then let the seeder's private-dir checks re-verify.
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
    _codeybox_fallback_home="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-${_codeybox_euid}"
    _codeybox_ensure_owned_home "$_codeybox_fallback_home"
    _codeybox_seed_home_migration_marker "$_codeybox_fallback_home"
    # Environment.GetFolderPath(UserProfile) falls back to the passwd entry
    # when $HOME is unset — cover a gate exec that resolves no HOME at all.
    # `getent` does not exist on macOS, where this candidate silently skips.
    _codeybox_seed_home_migration_marker \
        "$(getent passwd "$(id -u)" 2>/dev/null | cut -d: -f6)"
    # $XDG_DATA_HOME relocates the whole Migrations tree (no .local/share) —
    # same private-dir and no-symlink rules via the shared helper.
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
