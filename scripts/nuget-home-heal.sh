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
# It is a strict no-op when the home is already usable, never fails the caller
# (safe under `set -e`), and — because it is dot-sourced — can export a fallback
# DOTNET_CLI_HOME into the caller's environment. build.sh and the audit
# build/test gates all source this rather than re-implementing the recovery.
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
    # config adds no overrides and clears no sources.
    if [ ! -e "$_codeybox_seed_user_dir/NuGet.Config" ]; then
        printf '%s\n' \
            '<?xml version="1.0" encoding="utf-8"?>' \
            '<configuration />' \
            > "$_codeybox_seed_user_dir/NuGet.Config" 2>/dev/null || true
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
    # CLI home to a writable scratch directory for this process tree. Seed that
    # scratch home the same way as the in-place path — a readable user config so
    # the fatal read succeeds, and a symlink to the inherited (still readable but
    # unwritable) package cache so restore stays offline-safe there too.
    _codeybox_scratch_home="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-dotnet-home.XXXXXX")" \
        || return 0
    DOTNET_CLI_HOME="$_codeybox_scratch_home"
    export DOTNET_CLI_HOME
    _codeybox_seed_nuget_home "$_codeybox_scratch_home/.nuget/NuGet" \
        "$_codeybox_cli_home/.nuget/packages"
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
# #136802). Pin each unusable variable to a writable dir — preferring the
# (possibly just-redirected) DOTNET_CLI_HOME, else a private mktemp dir — for
# this dot-sourced process tree. Strict no-op when both are usable.
codeybox_ensure_dotnet_runtime_dir() {
    _codeybox_need_user_dir=0
    if [ -z "${HOME:-}" ] || [ ! -d "$HOME" ] || [ ! -w "$HOME" ]; then
        _codeybox_need_user_dir=1
    fi
    if [ -z "${XDG_RUNTIME_DIR:-}" ] || [ ! -d "$XDG_RUNTIME_DIR" ] || [ ! -w "$XDG_RUNTIME_DIR" ]; then
        _codeybox_need_user_dir=1
    fi
    [ "$_codeybox_need_user_dir" -eq 1 ] || return 0

    _codeybox_user_dir="${DOTNET_CLI_HOME:-}"
    if [ -n "$_codeybox_user_dir" ]; then
        mkdir -p "$_codeybox_user_dir" 2>/dev/null || true
    fi
    if [ -z "$_codeybox_user_dir" ] || [ ! -d "$_codeybox_user_dir" ] || [ ! -w "$_codeybox_user_dir" ]; then
        _codeybox_user_dir="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-dotnet-runtime.XXXXXX")" \
            || return 0
    fi
    if [ -z "${HOME:-}" ] || [ ! -d "$HOME" ] || [ ! -w "$HOME" ]; then
        # Redirecting HOME would orphan a pre-baked package cache under the
        # original $HOME/.nuget — seed the replacement home (readable user
        # config + linked cache) before the redirect, exactly like the
        # scratch-home path above.
        _codeybox_seed_nuget_home "$_codeybox_user_dir/.nuget/NuGet" \
            "$HOME/.nuget/packages"
        HOME="$_codeybox_user_dir"
        export HOME
    fi
    if [ -z "${XDG_RUNTIME_DIR:-}" ] || [ ! -d "$XDG_RUNTIME_DIR" ] || [ ! -w "$XDG_RUNTIME_DIR" ]; then
        XDG_RUNTIME_DIR="$_codeybox_user_dir"
        export XDG_RUNTIME_DIR
    fi
    return 0
}

codeybox_heal_nuget_home
# Run AFTER the home heal so a just-redirected DOTNET_CLI_HOME is reused as the
# runtime dir rather than spending a second scratch dir.
codeybox_ensure_dotnet_runtime_dir
