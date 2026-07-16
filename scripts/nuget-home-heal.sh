# shellcheck shell=sh
# Single source of truth for the per-user NuGet-home recovery.
#
# NuGet reads its per-user configuration from <cli-home>/.nuget/NuGet and, when
# that file is absent, tries to create it there. <cli-home> defaults to
# DOTNET_CLI_HOME, or $HOME when that is unset. In locked-down / COW-inherited
# sandboxes the inherited directory can be owned by another account or mounted
# read-only, so `dotnet` aborts restore before the build with an
# unauthorized-access error ("Failed to read NuGet.Config due to unauthorized
# access"). NuGet performs that user-config read during restore-graph
# generation, before any in-tree Directory.Build.props / NuGet.Config /
# RestoreConfigFile / MSBuild BeforeTargets="Restore" hook can run, so the home
# has to be healed on disk before `dotnet` starts.
#
# DOT-SOURCE this file (`. scripts/nuget-home-heal.sh`) BEFORE invoking dotnet.
# It is a strict no-op when the home is already usable, never fails the caller
# (safe under `set -e`), and — because it is dot-sourced — can export a fallback
# DOTNET_CLI_HOME into the caller's environment. build.sh and the audit
# build/test gates all source this rather than re-implementing the recovery.

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
                # Reuse the quarantined package cache so the heal does not force a
                # full re-download (which would fail offline). NuGet only READS an
                # already-extracted package (it checks the .nupkg.metadata marker
                # and skips extraction), so a read-only inherited cache satisfies
                # every restore; a writable one additionally lets NuGet add
                # packages the baseline lacks.
                if [ -d "$_codeybox_quarantine/packages" ] \
                    && [ ! -e "$_codeybox_nuget_root/packages" ]; then
                    ln -s "$_codeybox_quarantine/packages" \
                        "$_codeybox_nuget_root/packages" 2>/dev/null || true
                fi
                # Seed a minimal, readable user config so the next `dotnet`'s
                # user-config *read* (the exact operation the broken home fails)
                # succeeds without depending on first-run creation. Repository
                # package sources come from RestoreConfigFile, so an empty user
                # config adds no overrides and clears no sources.
                if [ ! -e "$_codeybox_nuget_user_dir/NuGet.Config" ]; then
                    printf '%s\n' \
                        '<?xml version="1.0" encoding="utf-8"?>' \
                        '<configuration />' \
                        > "$_codeybox_nuget_user_dir/NuGet.Config" 2>/dev/null || true
                fi
                return 0
            fi
        fi
    fi

    # The cli-home itself is not writable: keep the build hermetic by redirecting
    # the .NET CLI home to a writable scratch directory for this process tree.
    _codeybox_scratch_home="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-dotnet-home.XXXXXX")" \
        || return 0
    DOTNET_CLI_HOME="$_codeybox_scratch_home"
    export DOTNET_CLI_HOME
    return 0
}

codeybox_heal_nuget_home
