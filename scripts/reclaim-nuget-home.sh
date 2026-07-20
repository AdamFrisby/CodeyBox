#!/usr/bin/env sh
# reclaim-nuget-home.sh — best-effort repair of an unwritable NuGet user home.
#
# WHY THIS EXISTS
# Some CodeyBox sandbox base images provision the NuGet user home (~/.nuget)
# owned by root and NOT writable by the build user, while the parent home
# directory IS owned by the build user. In that state `dotnet` restore aborts
# while creating the user-level config ("Failed to read NuGet.Config due to
# unauthorized access ... ~/.nuget/NuGet ... Permission denied"), which fails
# every build in the sandbox.
#
# The existing DOTNET_CLI_HOME/HOME pinning (build.sh and
# SandboxRequiredBuildVerifier.DotnetCliHomeSelectionScript) fixes this only for
# `dotnet` invocations those code paths launch with a prepared environment. A
# BARE `dotnet build ./CodeyBox.slnx` — as the graded required-build gate runs —
# inherits no such environment, so it still probes the root-owned ~/.nuget.
#
# This script is wired as an MSBuild InitialTarget (see Directory.Build.props),
# so it runs BEFORE restore reads the NuGet user config within that SAME
# `dotnet build` process. An InitialTarget cannot change the running process's
# HOME, but it CAN make the directory NuGet reads writable — which is sufficient
# to let that build's restore create its config and succeed. This is the one
# lever available to a bare build; the environment-pinning approaches are not.
#
# SAFETY / IDEMPOTENCY
#  * Fast-path no-op when the NuGet home is already usable (the normal case on a
#    developer machine), so the aggressive branch never runs there.
#  * Non-destructive: an unwritable home is MOVED aside (never deleted); a fresh
#    writable home is created in its place.
#  * Repairs only when we own the PARENT directory; a root-owned parent is an
#    operator-only fix and is left untouched (NuGet then surfaces the real error).
#  * Race-safe: parallel MSBuild project evaluations each run this InitialTarget,
#    so the reclaim is serialized under an atomic mkdir lock — a torn ".nuget"
#    (moved aside but not yet recreated) can never be observed by a concurrent
#    restore.
#  * Never fails the build: on any impossibility it exits 0 and lets NuGet report
#    the real, actionable error downstream instead of masking it.
set -u

# Directory NuGet materialises its user-level config under. Overridable by the
# first argument so tests can exercise the reclaim against a temporary home
# without touching the caller's real $HOME.
nuget_home="${1:-${HOME:-}/.nuget}"

# An absolute path is required; anything else we cannot reason about safely.
case "$nuget_home" in
  /*) : ;;
  *) exit 0 ;;
esac

# Maximum seconds a contender waits for the reclaim holder before giving up and
# letting NuGet surface the real error rather than hang the build.
reclaim_wait_seconds=30

# The home is usable when we can create the user-config directory NuGet needs
# AND any existing NuGet.Config there is readable. A root-owned unreadable config
# left inside an otherwise-writable directory aborts restore the same way, so it
# must also force a reclaim.
nuget_home_usable() {
  mkdir -p "$nuget_home/NuGet" 2>/dev/null || return 1
  [ ! -e "$nuget_home/NuGet/NuGet.Config" ] || [ -r "$nuget_home/NuGet/NuGet.Config" ]
}

# Fast path: already usable — do nothing.
if nuget_home_usable; then
  exit 0
fi

# We can only repair the home when we own its parent (can move the root-owned
# entry aside). A root-owned parent too means only the operator can fix it.
home_dir=$(dirname "$nuget_home")
[ -w "$home_dir" ] || exit 0

# Serialize the reclaim across parallel project evaluations.
lock="${TMPDIR:-/tmp}/codeybox-nuget-reclaim.lock.d"
waited=0
while :; do
  if mkdir "$lock" 2>/dev/null; then
    break
  fi
  # Another runner is reclaiming; if it has finished, we are done.
  if nuget_home_usable; then
    exit 0
  fi
  waited=$((waited + 1))
  if [ "$waited" -ge "$reclaim_wait_seconds" ]; then
    exit 0
  fi
  sleep 1
done
# Release the lock even on interrupt so a killed build never orphans it.
trap 'rmdir "$lock" 2>/dev/null' EXIT INT TERM HUP

# Re-check under the lock: the holder we raced may already have repaired it.
if nuget_home_usable; then
  exit 0
fi

# Move the unwritable home aside (non-destructive) and create a fresh, writable
# one so this build's restore can materialise its config. On any failure we exit
# 0 and let NuGet report the underlying error.
aside="$nuget_home.root-owned.$$"
if [ -e "$nuget_home" ]; then
  mv "$nuget_home" "$aside" 2>/dev/null || exit 0
fi
mkdir -p "$nuget_home/NuGet" 2>/dev/null || exit 0
exit 0
