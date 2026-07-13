#!/usr/bin/env sh
# Reclaim an unwritable user-level NuGet home so that a `dotnet build` / `dotnet
# test` launched *directly* on the host (outside CodeyBox's own DOTNET_CLI_HOME
# seams — SandboxRequiredBuildVerifier / DotnetCliHomeConventions / build.sh) can
# materialise NuGet's user-config directory.
#
# WHY THIS EXISTS
# On first restore, dotnet/NuGet create "$HOME/.nuget/NuGet/NuGet.Config" while
# loading default settings — BEFORE RestoreConfigFile is consulted (see
# docs/audit.md "NuGet-home precondition"). In agent sandboxes "$HOME/.nuget" is
# frequently created root-owned and unwritable, so that probe aborts with
# "Failed to read NuGet.Config due to unauthorized access ... Access to the path
# '.../.nuget/NuGet' is denied". No committed repo file can redirect a
# directly-launched dotnet's home; the audit *host* must make ~/.nuget writable.
# This script is that host/operator recovery, encoded so it is discoverable and
# repeatable for the next agent rather than tribal knowledge.
#
# SAFE + IDEMPOTENT
# It acts ONLY when the NuGet user-config directory is not writable. A healthy,
# writable ~/.nuget (which may hold a real package cache or credentials) is left
# untouched, so it is safe to run unconditionally and to re-run. When it must
# reclaim, it RENAMES the unwritable directory aside to a numbered backup — it
# never deletes it, because the old contents may be root-owned and unremovable
# and must not be destroyed — then recreates a fresh writable ~/.nuget/NuGet
# owned by the current user. Renaming needs write on $HOME (the parent), not on
# the unwritable directory itself, so the unprivileged owner of $HOME can do it.
#
# EXIT CODES: 0 = home is writable (already, or after reclaim); 1 = could not
# make it writable (e.g. $HOME itself is not writable); 2 = HOME is unset.

set -eu

# Upper bound on backup-slot probing so a pathological pile of prior backups
# cannot spin unbounded.
max_backup_slots=100

home="${HOME:-}"
if [ -z "$home" ]; then
  echo "reclaim-nuget-home: HOME is unset; cannot locate the NuGet home." >&2
  exit 2
fi

nuget_home="$home/.nuget"
nuget_config_dir="$nuget_home/NuGet"
# NuGet's user-level settings file. NuGet READS this while loading default
# settings on restore, so it must be readable — not merely present under a
# writable directory. A root-owned, unreadable file here reproduces the same
# "Failed to read NuGet.Config due to unauthorized access" gate failure as an
# inaccessible directory, so the health check below treats it as unhealthy too.
nuget_config_file="$nuget_config_dir/NuGet.Config"

# Fast path: the home is healthy — and left untouched — only when the user-config
# directory exists (or can be created) AND is writable AND any existing config
# file is readable. Checking readability of the file (not just writability of the
# directory) closes a hole where a writable directory holding an unreadable
# NuGet.Config would be falsely reported healthy while restore still aborts on the
# unreadable file. This is the idempotent no-op that keeps a legitimate ~/.nuget
# intact.
if mkdir -p "$nuget_config_dir" 2>/dev/null \
  && [ -w "$nuget_config_dir" ] \
  && { [ ! -e "$nuget_config_file" ] || [ -r "$nuget_config_file" ]; }; then
  echo "reclaim-nuget-home: $nuget_config_dir is healthy; no action needed."
  exit 0
fi

# The home is unhealthy. If it does not exist at all, then $HOME itself is not
# writable and there is nothing this script can safely do.
if [ ! -e "$nuget_home" ]; then
  echo "reclaim-nuget-home: cannot create $nuget_config_dir and $home is not writable." >&2
  exit 1
fi

# The home exists but is unwritable (typically root-owned). Find a free backup
# slot and rename it aside, preserving its (possibly unremovable) contents.
i=0
backup=""
while [ "$i" -lt "$max_backup_slots" ]; do
  candidate="$nuget_home.unwritable-backup.$i"
  if [ ! -e "$candidate" ]; then
    backup="$candidate"
    break
  fi
  i=$((i + 1))
done
if [ -z "$backup" ]; then
  echo "reclaim-nuget-home: exhausted $max_backup_slots backup slots for $nuget_home." >&2
  exit 1
fi

if ! mv "$nuget_home" "$backup" 2>/dev/null; then
  echo "reclaim-nuget-home: failed to move $nuget_home aside (is $home writable?)." >&2
  exit 1
fi

if ! mkdir -p "$nuget_config_dir" 2>/dev/null || [ ! -w "$nuget_config_dir" ]; then
  echo "reclaim-nuget-home: recreated $nuget_config_dir is still not writable." >&2
  exit 1
fi

echo "reclaim-nuget-home: reclaimed $nuget_home (previous contents preserved at $backup)."
exit 0
