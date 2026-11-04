#!/usr/bin/env sh
# reclaim-nuget-home.sh — best-effort repair of an unusable NuGet user home.
#
# WHY THIS EXISTS
# Some CodeyBox sandbox base images provision the NuGet user home (~/.nuget)
# owned by root and NOT writable by the build user, while the parent home
# directory IS owned by the build user. In that state `dotnet` restore aborts
# while creating the user-level config ("Failed to read NuGet.Config due to
# unauthorized access ... ~/.nuget/NuGet ... Permission denied"), which fails
# every build in the sandbox.
#
# The DOTNET_CLI_HOME/HOME pinning applied by the orchestrator fixes this
# only for `dotnet` invocations launched with a prepared environment. A
# BARE `dotnet build ./CodeyBox.slnx` — as the graded required-build gate
# runs — inherits no such environment, so it still probes the root-owned
# ~/.nuget.
#
# This script is wired as an MSBuild InitialTarget (see Directory.Build.props),
# so it runs BEFORE restore reads the NuGet user config within that SAME
# `dotnet build` process. An InitialTarget cannot change the running process's
# HOME, but it CAN make the directory NuGet reads writable — which is
# sufficient to let that build's restore create its config and succeed. This
# is the one lever available to a bare build; the environment-pinning
# approaches are not.
#
# The repair is delegated to the single source of truth in
# scripts/nuget-home-heal.sh — the same recovery build.sh, the audit gates,
# and the sibling InitialTargets hook (Directory.NuGetHomeHeal.targets) all
# dot-source — so the "is ~/.nuget usable / relocate and recreate" policy is
# no longer forked here with weaker probes: a merely-writable or
# foreign-owned .nuget is NOT admitted, a symlinked one is quarantined
# rather than descended, and a package cache is relinked only when its
# canonical dir is owned by this uid or root.
#
# SAFETY / IDEMPOTENCY
#  * Fast-path no-op when the NuGet home is already usable (the normal case
#    on a developer machine), so the aggressive branch never runs there.
#  * Non-destructive: an unusable home is MOVED aside (preserved as
#    .nuget.codeybox-unwritable.<pid>, never deleted); a fresh writable home
#    is created in its place.
#  * Repairs only when the cli home verifies as private per-user storage; a
#    foreign-controlled or unwritable parent is left untouched (NuGet then
#    surfaces the real error, or the DOTNET_CLI_HOME redirect picks up).
#  * Never fails the build: on any impossibility it exits 0 and lets NuGet
#    report the real, actionable error downstream instead of masking it.
set -u

# $1, when given, is the .nuget directory to repair — an override so tests
# can exercise the reclaim against a temporary home without touching the
# caller's real $HOME. The heal operates on the cli home above it. Either
# way this hook's mission is the $HOME-based home — an InitialTarget cannot
# repoint the running build's cli home, so a stamped DOTNET_CLI_HOME must
# not redirect the repair elsewhere; unset it so the heal resolves HOME.
if [ -n "${1:-}" ]; then
    HOME="$(dirname "${1%/}")"
    export HOME
fi
unset DOTNET_CLI_HOME

codeybox_script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [ -f "$codeybox_script_dir/nuget-home-heal.sh" ]; then
    . "$codeybox_script_dir/nuget-home-heal.sh" || true
fi
exit 0
