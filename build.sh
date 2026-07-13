#!/usr/bin/env sh
set -eu

# NuGet reads its per-user configuration from <cli-home>/.nuget/NuGet and, when
# that file is absent, tries to create it there. <cli-home> defaults to
# DOTNET_CLI_HOME, or $HOME when that is unset. In locked-down environments the
# inherited directory can be owned by another account or mounted read-only, so
# `dotnet` aborts restore before the build with an unauthorized-access error
# (e.g. "Failed to read NuGet.Config due to unauthorized access").
#
# There is no in-tree NuGet setting that suppresses that read, so when the
# inherited location is not usable, redirect the .NET CLI home to a writable
# scratch directory. NuGet then keeps its per-user config there and the build
# stays hermetic instead of failing. When the inherited location is fine we
# leave the environment untouched.
cli_home="${DOTNET_CLI_HOME:-${HOME:-}}"
if [ -n "$cli_home" ]; then
    nuget_user_dir="$cli_home/.nuget/NuGet"
    probe="$nuget_user_dir/.codeybox-writable-probe"
    # Use touch (a plain command) rather than a shell redirect for the write
    # probe: a failed redirection is a fatal shell error under `set -e`, which
    # would abort instead of taking the relocation branch.
    if mkdir -p "$nuget_user_dir" 2>/dev/null && touch "$probe" 2>/dev/null; then
        rm -f "$probe" 2>/dev/null || true
    else
        scratch_home="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-dotnet-home.XXXXXX")"
        DOTNET_CLI_HOME="$scratch_home"
        export DOTNET_CLI_HOME
    fi
fi

dotnet build CodeyBox.slnx
