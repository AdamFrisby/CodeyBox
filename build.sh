#!/usr/bin/env sh
set -eu

codeybox_script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
cli_home="$codeybox_script_dir/.dotnet-cli-home"

# dotnet/NuGet materialise the user-level config under $HOME/.nuget/NuGet (and,
# for some SDK/NuGet builds, $DOTNET_CLI_HOME/.nuget) on first restore. When the
# inherited home already has a writable ~/.nuget/NuGet we keep it so the caller's
# real package cache and credentials are reused (and respect an explicit
# DOTNET_CLI_HOME override so callers pinning a different home — e.g. CI caches
# — still win); otherwise (root-owned or unwritable, common in agent sandboxes)
# we pin both DOTNET_CLI_HOME and HOME to a writable repo-local home so restore
# never probes a root-owned ~/.nuget. Pinning HOME as well as DOTNET_CLI_HOME
# mirrors SandboxRequiredBuildVerifier's DotnetCliHomeSelectionScript, whose
# comment explains why DOTNET_CLI_HOME alone is insufficient for NuGet builds
# that derive the config dir from HOME.
if [ -n "${HOME:-}" ] \
  && mkdir -p "$HOME/.nuget/NuGet" 2>/dev/null \
  && [ -w "$HOME/.nuget/NuGet" ]; then
  export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$cli_home}"
else
  export DOTNET_CLI_HOME="$cli_home"
  export HOME="$cli_home"
fi

# Heal an inherited, non-writable per-user NuGet home before dotnet restore so a
# COW-inherited root-owned $HOME/.nuget cannot abort the build with "Failed to
# read NuGet.Config due to unauthorized access". The recovery is the single
# source of truth in scripts/nuget-home-heal.sh (shared with the audit
# build/test gates); source it relative to THIS script so it resolves regardless
# of the caller's working directory, and only when present so a partial checkout
# still runs the build. Runs AFTER the DOTNET_CLI_HOME assignment above so the
# heal targets the repo-local home (creating it if needed) instead of $HOME.
# The shared script subsumes the earlier inline temp-collision hardening: it
# uses `mktemp -d` for the DOTNET_CLI_HOME redirect (never a predictable /tmp
# leaf), and additionally quarantines the broken ~/.nuget in place when the
# cli-home is writable, so the whole environment is healed rather than just
# the current process.
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
