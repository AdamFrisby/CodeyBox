#!/usr/bin/env sh
set -eu

# NuGet reads its per-user configuration from <cli-home>/.nuget/NuGet and, when
# that file is absent, tries to create it there. <cli-home> defaults to
# DOTNET_CLI_HOME, or $HOME when that is unset. In locked-down environments the
# inherited directory can be owned by another account or mounted read-only, so
# `dotnet` aborts restore before the build with an unauthorized-access error
# (e.g. "Failed to read NuGet.Config due to unauthorized access"). There is no
# in-tree NuGet setting that suppresses that read, so recover it here.
cli_home="${DOTNET_CLI_HOME:-${HOME:-}}"
if [ -n "$cli_home" ]; then
    nuget_user_dir="$cli_home/.nuget/NuGet"
    probe="$nuget_user_dir/.codeybox-writable-probe"

    # Probe with touch (a real command) rather than a shell redirect: a failed
    # redirection is a fatal error under `set -e` and would abort before the
    # recovery branches run. Writability is necessary but not sufficient: an
    # inherited NuGet.Config file that is present but unreadable (e.g. baked
    # mode 0600 under another account, inside a directory we can still write)
    # reproduces the exact "Failed to read NuGet.Config due to unauthorized
    # access" abort, so treat that as unusable too and let the heal recreate it.
    nuget_home_usable() {
        mkdir -p "$nuget_user_dir" 2>/dev/null || return 1
        if [ -e "$nuget_user_dir/NuGet.Config" ] \
            && [ ! -r "$nuget_user_dir/NuGet.Config" ]; then
            return 1
        fi
        touch "$probe" 2>/dev/null
    }

    if nuget_home_usable; then
        rm -f "$probe" 2>/dev/null || true
    else
        # The inherited per-user NuGet directory is not writable — e.g. an image
        # baked $HOME/.nuget owned by root. When the cli-home itself is writable,
        # relocate the broken directory aside and recreate a writable one. This
        # heals the real cli-home so EVERY later `dotnet` invocation in this
        # environment can write its per-user config, not just this script's own
        # build: a process-local DOTNET_CLI_HOME override would not, because the
        # deterministic build/test gates invoke `dotnet` directly. The rename
        # preserves the old tree instead of deleting it, and only runs when the
        # directory is genuinely unusable.
        healed=0
        if [ -w "$cli_home" ]; then
            nuget_root="$cli_home/.nuget"
            quarantine="$nuget_root.codeybox-unwritable.$$"
            if [ ! -e "$nuget_root" ] || mv "$nuget_root" "$quarantine" 2>/dev/null; then
                if nuget_home_usable; then
                    rm -f "$probe" 2>/dev/null || true
                    healed=1
                    # Reuse the quarantined package cache so the heal does not
                    # force a full re-download (which would fail in an offline or
                    # credential-free sandbox). Symlink it even when it is
                    # read-only: the account that baked $HOME/.nuget as root also
                    # left its populated cache root-owned, and NuGet only READS an
                    # already-extracted package (it checks the .nupkg.metadata
                    # marker and skips extraction), so a read-only full cache
                    # satisfies every restore. A writable cache additionally lets
                    # NuGet add any package the baseline lacks.
                    if [ -d "$quarantine/packages" ] \
                        && [ ! -e "$nuget_root/packages" ]; then
                        ln -s "$quarantine/packages" "$nuget_root/packages" 2>/dev/null || true
                    fi
                    # Seed a minimal, readable user config into the fresh home. The
                    # fatal gate error is a *read* failure ("Failed to read
                    # NuGet.Config due to unauthorized access"); leaving the healed
                    # directory empty makes the next `dotnet` create the file on
                    # first use, so a readable file guarantees the read succeeds
                    # without depending on that creation. Repository package sources
                    # come from RestoreConfigFile, so an empty user config adds no
                    # overrides and clears no sources.
                    if [ ! -e "$nuget_user_dir/NuGet.Config" ]; then
                        printf '%s\n' \
                            '<?xml version="1.0" encoding="utf-8"?>' \
                            '<configuration />' \
                            > "$nuget_user_dir/NuGet.Config" 2>/dev/null || true
                    fi
                fi
            fi
        fi

        if [ "$healed" -eq 0 ]; then
            # The cli-home itself is not writable: keep the build hermetic by
            # redirecting the .NET CLI home to a writable scratch directory.
            scratch_home="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-dotnet-home.XXXXXX")"
            DOTNET_CLI_HOME="$scratch_home"
            export DOTNET_CLI_HOME
        fi
    fi
fi

# Forward any explicit arguments straight to `dotnet` so every gate command —
# `build CodeyBox.slnx`, `build --no-incremental -warnaserror`, `test --no-build`
# — runs through the NuGet-home heal above, not just the default build. With no
# arguments, keep the historical behaviour of building the whole solution.
if [ "$#" -gt 0 ]; then
    dotnet "$@"
else
    dotnet build CodeyBox.slnx
fi
