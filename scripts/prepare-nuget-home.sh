#!/usr/bin/env bash
#
# prepare-nuget-home.sh — operator/CI-run remediation for a root-owned NuGet
# home on a CodeyBox build / audit ("verify") VM.
#
# What this does:
#   Ensures the unprivileged build user owns a writable "$HOME/.nuget" so that
#   `dotnet restore` / `dotnet build` / `dotnet test` can create and read
#   "$HOME/.nuget/NuGet/NuGet.Config". If a provisioning step created
#   "$HOME/.nuget" as root before the build user ran, every dotnet command
#   aborts before compiling any source with:
#
#     error : Failed to read NuGet.Config due to unauthorized access.
#             Path: '<home>/.nuget/NuGet/NuGet.Config'.
#             Access to the path '<home>/.nuget/NuGet' is denied. Permission denied
#
# Why a script instead of a repo file: NuGet ensures its user-settings
# *directory* exists during settings load — ahead of any repository nuget.config,
# MSBuild RestoreConfigFile, or NUGET_CONFIG_FILE override — so the fix has to
# relocate the home, which no committed build input can do. See
# docs/build-environment.md §1 for the full analysis.
#
# Usage:
#   scripts/prepare-nuget-home.sh
#
# Run it as the build user, before `dotnet build ./CodeyBox.slnx`. Requires no
# sudo: renaming a root-owned "$HOME/.nuget" needs write permission on the
# parent ("$HOME"), not on the entry itself. Idempotent — a no-op when
# "$HOME/.nuget" is already writable.
#
# Exit codes:
#   0  NuGet home is writable (already, or after remediation).
#   1  NuGet home is not writable and could not be remediated without elevation
#      (e.g. "$HOME" itself is not writable) — operator must chown it as root.

set -euo pipefail

nuget_home="${HOME:?HOME must be set}/.nuget"
# NuGet loads user settings from "$nuget_home/NuGet/NuGet.Config"; the failure
# this script remediates is reported against that subdirectory
# ("Access to the path '<home>/.nuget/NuGet' is denied"), not only "$nuget_home"
# itself. Check both so a home that is writable at the top level but whose
# settings subdirectory was created root-owned is still detected and repaired.
nuget_settings_dir="$nuget_home/NuGet"

nuget_home_usable() {
    # Absent entirely: dotnet recreates it writable, so nothing to do.
    [[ ! -e "$nuget_home" ]] && return 0

    # The home must be writable so NuGet can create its settings subdirectory
    # when missing.
    [[ -w "$nuget_home" ]] || return 1

    # If the settings subdirectory already exists it must be traversable,
    # readable, and writable by this user; a root-owned one aborts settings
    # load even when "$nuget_home" is writable.
    if [[ -e "$nuget_settings_dir" ]]; then
        [[ -r "$nuget_settings_dir" && -w "$nuget_settings_dir" && -x "$nuget_settings_dir" ]] || return 1
    fi

    return 0
}

if nuget_home_usable; then
    echo "prepare-nuget-home: '$nuget_home' is usable by $(id -un); no action needed."
    exit 0
fi

if [[ ! -w "$HOME" ]]; then
    echo "prepare-nuget-home: '$nuget_home' is not usable and '$HOME' is not writable either;" >&2
    echo "  cannot relocate without elevation. Run as root: chown -R \"\$(id -un):\$(id -gn)\" '$nuget_home'" >&2
    exit 1
fi

# Renaming needs write on the parent dir ($HOME), not on the root-owned entry.
# Pick a fresh, unused sidelined name so re-runs after a partial failure are safe.
sidelined="$nuget_home.root-owned"
if [[ -e "$sidelined" ]]; then
    sidelined="$nuget_home.root-owned.$$"
fi

mv -- "$nuget_home" "$sidelined"
mkdir -p -- "$nuget_home"

# Reuse the already-populated, world-readable package cache from the sidelined
# tree so restore does not re-download every package.
sidelined_packages="$sidelined/packages"
if [[ -d "$sidelined_packages" ]]; then
    ln -s -- "$sidelined_packages" "$nuget_home/packages"
fi

echo "prepare-nuget-home: relocated not-usable '$nuget_home' to '$sidelined';" \
     "fresh build-user-owned home created (package cache reused if present)."
exit 0
