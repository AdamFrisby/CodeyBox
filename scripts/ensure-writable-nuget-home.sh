#!/bin/sh
# Repair a non-writable $HOME/.nuget so NuGet can create its per-user settings
# directory ($HOME/.nuget/NuGet). Sandbox images that pre-bake a package cache
# under $HOME/.nuget/packages as root often leave the parent directory
# root-owned mode 755; raw `dotnet build`/`dotnet restore` then fail with
# "Failed to read NuGet.Config ... Permission denied" before any project-,
# solution-, or RestoreConfigFile-level config is honoured.
#
# The build user owns $HOME even when .nuget does not, so renaming the entry
# aside and recreating a writable one is allowed without root. Pre-baked
# packages are preserved via symlink. Concurrent MSBuild nodes share a lock so
# InitialTargets across many projects do not race.
#
# When $HOME itself is not writable, no in-place repair is possible (creating
# or relocating $HOME/.nuget both need a writable parent). That is not a build
# failure of this script's making: the DOTNET_CLI_HOME redirect — the
# NuGetHomeSelfHeal preamble that wraps dotnet invocations, or the in-process
# NuGetHomeGuard.targets backstop — is the designed fallback for an unwritable
# home, so this script reports the skip and lets the build continue instead of
# turning an impossible repair into an MSB3073 that masks the real path.
#
# Invoked from Directory.Build.targets (InitialTargets) so every raw
# `dotnet build` in this repository — including process:required-build and
# csharp:build-WaE — self-heals on a misprovisioned image.
set -eu

home="${HOME:-}"
[ -n "$home" ] || exit 0

nuget_home="$home/.nuget"

# Fast path: absent or already writable — ensure the settings dir exists.
if [ ! -e "$nuget_home" ] || [ -w "$nuget_home" ]; then
  if mkdir -p "$nuget_home/NuGet" 2>/dev/null; then
    exit 0
  fi
  echo "ensure-writable-nuget-home: cannot create $nuget_home/NuGet under $home; relying on the DOTNET_CLI_HOME redirect" >&2
  exit 0
fi

# $HOME/.nuget exists but is not writable: repairing it means renaming it
# aside, which requires a writable $HOME. When $HOME is unwritable too there
# is no in-place repair — defer to the DOTNET_CLI_HOME redirect.
if [ ! -w "$home" ]; then
  echo "ensure-writable-nuget-home: $home is not writable; cannot repair $nuget_home in place — relying on the DOTNET_CLI_HOME redirect" >&2
  exit 0
fi

lock_dir="${TMPDIR:-/tmp}/codeybox-ensure-writable-nuget-home.lock"
tries=0
while ! mkdir "$lock_dir" 2>/dev/null; do
  tries=$((tries + 1))
  # Another node already finished the repair.
  if [ -d "$nuget_home" ] && [ -w "$nuget_home" ]; then
    if mkdir -p "$nuget_home/NuGet" 2>/dev/null; then
      exit 0
    fi
    echo "ensure-writable-nuget-home: cannot create $nuget_home/NuGet under $home; relying on the DOTNET_CLI_HOME redirect" >&2
    exit 0
  fi
  if [ "$tries" -ge 200 ]; then
    echo "error : timed out waiting to repair non-writable NuGet home at $nuget_home" >&2
    exit 1
  fi
  # Portable ~50ms pause without requiring bash/usleep.
  sleep 0.05 2>/dev/null || sleep 1
done

cleanup_lock() {
  rmdir "$lock_dir" 2>/dev/null || true
}
trap cleanup_lock EXIT INT TERM

# Re-check under the lock — a peer may have repaired while we waited.
if [ ! -e "$nuget_home" ] || [ -w "$nuget_home" ]; then
  if mkdir -p "$nuget_home/NuGet" 2>/dev/null; then
    exit 0
  fi
  echo "ensure-writable-nuget-home: cannot create $nuget_home/NuGet under $home; relying on the DOTNET_CLI_HOME redirect" >&2
  exit 0
fi

baked="$home/.nuget.rootbaked"
if [ -e "$baked" ]; then
  baked="$home/.nuget.rootbaked-$$"
fi

if ! mv "$nuget_home" "$baked" 2>/dev/null; then
  echo "ensure-writable-nuget-home: cannot relocate $nuget_home aside; relying on the DOTNET_CLI_HOME redirect" >&2
  exit 0
fi
if ! mkdir -p "$nuget_home/NuGet" 2>/dev/null; then
  echo "ensure-writable-nuget-home: relocated $nuget_home but cannot recreate it; relying on the DOTNET_CLI_HOME redirect" >&2
  exit 0
fi
if [ -d "$baked/packages" ]; then
  ln -s "$baked/packages" "$nuget_home/packages" 2>/dev/null || true
fi
