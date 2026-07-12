#!/usr/bin/env sh
# Self-heal an unwritable $HOME/.nuget before `dotnet restore` reads it.
#
# WHY THIS EXISTS
# ---------------
# Some build hosts provision the build user's $HOME with a root-owned,
# non-writable ~/.nuget (mode 0755 root:root) that already carries a baked
# global-packages cache. NuGet reads/creates its user-level settings directory
# ($HOME/.nuget/NuGet) unconditionally at the start of every restore; when the
# directory is not writable that read fails hard with
#   "Failed to read NuGet.Config due to unauthorized access ... Permission denied"
# and the whole build aborts before a single project compiles. No NuGet config
# property (RestoreConfigFile, RestorePackagesPath, globalPackagesFolder) avoids
# this, because the user-settings read happens regardless of them.
#
# The user *does* own $HOME, so it can rename the offending ~/.nuget aside and
# recreate a writable tree — the one repair possible without elevated rights.
# Running it here, hooked before Restore, makes the fix travel with the repo and
# survive host re-provisioning instead of relying on an out-of-band manual step.
#
# SAFETY
# ------
# - No-op when ~/.nuget is absent or already writable: a healthy developer/CI
#   environment is never touched.
# - Non-destructive: the old tree is renamed aside, never deleted.
# - Preserves the baked package cache and any existing user NuGet.Config so an
#   offline / cache-only restore keeps working.
# - Never fails the build: if it cannot repair, it exits 0 and lets NuGet emit
#   its own diagnostic rather than masking the failure.
# - Concurrency-safe: a mkdir-based lock serialises the parallel per-project
#   restore invocations so only one performs the move.
set -u

NUGET_HOME="${HOME:-}/.nuget"
[ -n "${HOME:-}" ] || exit 0

# Fast path: absent or writable => nothing to repair.
[ -e "$NUGET_HOME" ] || exit 0
[ -w "$NUGET_HOME" ] && exit 0

LOCK="${HOME}/.nuget.repair.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
    # Another restore is already repairing; wait (bounded) for it to finish so
    # we do not race NuGet's read against a half-created tree.
    i=0
    while [ ! -w "$NUGET_HOME" ] && [ "$i" -lt 200 ]; do
        i=$((i + 1))
        sleep 0.05
    done
    exit 0
fi

# Re-check under the lock: a peer may have repaired it between our fast-path
# check and acquiring the lock.
if [ -e "$NUGET_HOME" ] && [ ! -w "$NUGET_HOME" ]; then
    ASIDE="${NUGET_HOME}.unwritable.$$"
    if mv "$NUGET_HOME" "$ASIDE" 2>/dev/null; then
        mkdir -p "$NUGET_HOME/NuGet"
        # Preserve any existing user config (custom package sources, offline
        # mirrors) if it is still readable.
        if [ -r "$ASIDE/NuGet/NuGet.Config" ]; then
            cp "$ASIDE/NuGet/NuGet.Config" "$NUGET_HOME/NuGet/NuGet.Config" 2>/dev/null || true
        fi
        # Reuse the baked global-packages cache so a cache-only restore does not
        # have to re-download (or fail offline).
        if [ -e "$ASIDE/packages" ] && [ -r "$ASIDE/packages" ] && [ ! -e "$NUGET_HOME/packages" ]; then
            ln -s "$ASIDE/packages" "$NUGET_HOME/packages" 2>/dev/null || true
        fi
    fi
fi

rmdir "$LOCK" 2>/dev/null || true
exit 0
