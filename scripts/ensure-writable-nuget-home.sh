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
# Invoked from Directory.Build.targets (InitialTargets) so every raw
# `dotnet build` in this repository — including process:required-build and
# csharp:build-WaE — self-heals on a misprovisioned image.
set -eu

home="${HOME:-}"
[ -n "$home" ] || exit 0

nuget_home="$home/.nuget"

# Fast path: absent or already writable — ensure the settings dir exists.
if [ ! -e "$nuget_home" ] || [ -w "$nuget_home" ]; then
  mkdir -p "$nuget_home/NuGet"
  exit 0
fi

lock_dir="${TMPDIR:-/tmp}/codeybox-ensure-writable-nuget-home.lock"
tries=0
while ! mkdir "$lock_dir" 2>/dev/null; do
  tries=$((tries + 1))
  # Another node already finished the repair.
  if [ -d "$nuget_home" ] && [ -w "$nuget_home" ]; then
    mkdir -p "$nuget_home/NuGet"
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
  mkdir -p "$nuget_home/NuGet"
  exit 0
fi

baked="$home/.nuget.rootbaked"
if [ -e "$baked" ]; then
  baked="$home/.nuget.rootbaked-$$"
fi

mv "$nuget_home" "$baked"
mkdir -p "$nuget_home"
if [ -d "$baked/packages" ]; then
  ln -s "$baked/packages" "$nuget_home/packages"
fi
mkdir -p "$nuget_home/NuGet"
