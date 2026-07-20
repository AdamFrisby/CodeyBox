#!/usr/bin/env sh
set -eu

# Heal an inherited, non-writable per-user NuGet home before dotnet restore so a
# COW-inherited root-owned $HOME/.nuget cannot abort the build with "Failed to
# read NuGet.Config due to unauthorized access". The recovery is the single
# source of truth in scripts/nuget-home-heal.sh (shared with the audit
# build/test gates); source it relative to THIS script so it resolves regardless
# of the caller's working directory, and only when present so a partial checkout
# still runs the build.
codeybox_script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ -f "$codeybox_script_dir/scripts/nuget-home-heal.sh" ]; then
    . "$codeybox_script_dir/scripts/nuget-home-heal.sh"
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
