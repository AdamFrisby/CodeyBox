#!/usr/bin/env bash
#
# ensure-nuget-writable.sh — make the current user's NuGet home usable for
# `dotnet restore`/`dotnet build` when the build host provisioned `~/.nuget`
# owned by another user (typically root) with no `sudo` available to chown it.
#
# Why this exists:
#   `dotnet restore` unconditionally reads (and, on first run, creates) NuGet's
#   user-level config under `$HOME/.nuget/NuGet/NuGet.Config`. If `~/.nuget` is
#   owned by another user or is otherwise unreadable, every project fails to
#   restore with "Failed to read NuGet.Config due to unauthorized access", which
#   then cascades into a build that produces no assemblies and a `dotnet test`
#   that rejects the missing test DLLs. There is no in-repo/MSBuild remedy: NuGet
#   loads the user config before any user-controllable MSBuild target runs, and
#   `--configfile`/`RestoreConfigFile` do not suppress the user-config read
#   (verified). The only fix is a writable NuGet home. When you own your home
#   directory you can provide one without root by relocating the inaccessible
#   tree aside and recreating a user-owned `~/.nuget`, preserving the existing
#   package cache via a symlink so restore reuses it instead of re-downloading.
#
# What this does (idempotent — a no-op when `~/.nuget` is already writable):
#   1. Probes whether `$HOME/.nuget/NuGet` is writable. If so, exits 0.
#   2. Otherwise moves the inaccessible `~/.nuget` aside to
#      `~/.nuget-inaccessible.<pid>`, recreates a user-owned `~/.nuget/NuGet`,
#      and symlinks the preserved `packages/` cache back in if one was present.
#   3. Re-probes and fails loudly if the NuGet home is still not writable.
#
# Usage: scripts/ensure-nuget-writable.sh   (run once before restore/build)
set -euo pipefail

nuget_home="${HOME:?HOME must be set}/.nuget"
cfg_dir="${nuget_home}/NuGet"

# A directory is usable if we can create and remove a probe file inside it.
probe_writable() {
  local dir="$1"
  [ -d "$dir" ] || return 1
  local probe="${dir}/.codeybox-write-probe.$$"
  if ( : > "$probe" ) 2>/dev/null; then
    rm -f "$probe"
    return 0
  fi
  return 1
}

if probe_writable "$cfg_dir"; then
  echo "ensure-nuget-writable: ${cfg_dir} is already writable; nothing to do."
  exit 0
fi

echo "ensure-nuget-writable: ${cfg_dir} is not writable; relocating ${nuget_home} aside."

if [ ! -w "$HOME" ]; then
  echo "ensure-nuget-writable: cannot repair — HOME (${HOME}) is not writable by this user." >&2
  exit 1
fi

aside="${nuget_home}-inaccessible.$$"
if [ -e "$nuget_home" ] || [ -L "$nuget_home" ]; then
  mv "$nuget_home" "$aside"
fi

mkdir -p "$cfg_dir"

# Reuse the preserved package cache if the relocated tree had one, so restore
# does not re-download every package. Only link a real directory (not a dangling
# or already-symlinked entry) to avoid creating a self-referential loop.
if [ -d "${aside}/packages" ] && [ ! -L "${aside}/packages" ]; then
  ln -s "${aside}/packages" "${nuget_home}/packages"
fi

if ! probe_writable "$cfg_dir"; then
  echo "ensure-nuget-writable: repair failed — ${cfg_dir} is still not writable." >&2
  exit 1
fi

echo "ensure-nuget-writable: ${cfg_dir} is now writable (previous tree preserved at ${aside})."
