#!/bin/sh
# heal-nuget-home.sh -- make $HOME/.nuget usable for a RAW `dotnet` restore.
#
# WHY THIS EXISTS
# On first restore NuGet must create and read its user-settings file at
# $HOME/.nuget/NuGet/NuGet.Config. Audit/CI sandboxes have shipped $HOME/.nuget
# owned by another uid (e.g. root, mode 755) or mode 000, so the build uid
# cannot create the .nuget/NuGet settings directory. That aborts EVERY project
# restore with "Failed to read NuGet.Config due to unauthorized access" before a
# single project builds -- unrelated to any diff under review. NuGet resolves
# the user-settings path from $HOME unconditionally, so neither a repo-committed
# nuget.config, a --configfile, nor an MSBuild property can redirect that read;
# the only reliable lever is the filesystem state of $HOME/.nuget itself.
#
# WHY IN-PLACE REPAIR (not HOME relocation)
# build.sh heals its OWN process by relocating $HOME. That cannot help a raw
# `dotnet build ./CodeyBox.slnx` (the shape CI/audit gates and this project's
# MSBuild pre-restore hook use): this script runs as a CHILD Exec of that
# dotnet process, so exporting a new HOME here would not reach the parent that
# performs the restore. Repairing the SHARED $HOME/.nuget in place is visible to
# that parent and to every sibling dotnet invocation in the sandbox. Renaming
# the foreign-owned directory aside needs only a writable $HOME (which the build
# uid owns even when .nuget itself is foreign-owned), so it works without root.
#
# CodeyBox's runtime agent-build gates carry the equivalent repair as an
# embedded shell string in NuGetHomeGuard.InPlaceRepairPreamble, because those
# run on remote/in-VM sandboxes where this repo file is not present. This script
# is the repo-file delivery of the same policy for building CodeyBox itself.
#
# Idempotent and a strict no-op when $HOME/.nuget is already usable. Usability is
# decided by PROBING the real first-restore operation (create the settings dir +
# write a file in it) rather than inferring it from permission bits, which ACLs,
# read-only mounts, overlay filesystems, and a non-directory occupying the path
# can all defeat. Never emits secret material. Safe to run under `set -u`.

set -u

nuget_home="${HOME:-/nonexistent}/.nuget"
settings_dir="$nuget_home/NuGet"
write_probe="$settings_dir/.codeybox-heal-probe.$$"

# Probe: if we can create the settings directory and write inside it, NuGet can
# too -- nothing to repair.
if mkdir -p "$settings_dir" 2>/dev/null && touch "$write_probe" 2>/dev/null; then
  rm -f "$write_probe" 2>/dev/null || true
  exit 0
fi

# Cannot heal without a $HOME to rename within.
[ -n "${HOME:-}" ] || exit 0

# Rename the foreign-owned home aside and recreate it uid-owned. Best-effort:
# any failure leaves the tree untouched (build.sh's relocation remains the
# fallback for the entrypoint path, and a raw build surfaces the original error
# rather than a corrupted tree).
if [ -e "$nuget_home" ] || [ -L "$nuget_home" ]; then
  aside="$nuget_home.codeybox-foreign-owned.$$"
  if mv "$nuget_home" "$aside" 2>/dev/null && mkdir -p "$settings_dir" 2>/dev/null; then
    echo "heal-nuget-home: renamed foreign-owned $nuget_home aside and recreated it uid-owned" >&2
    # Preserve any pre-populated package cache so the restore stays offline.
    if [ -d "$aside/packages" ] && [ ! -e "$nuget_home/packages" ]; then
      ln -s "$aside/packages" "$nuget_home/packages" 2>/dev/null || true
    fi
  fi
else
  # No .nuget at all: just create a usable settings directory.
  mkdir -p "$settings_dir" 2>/dev/null || true
fi

exit 0
