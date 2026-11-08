#!/bin/sh
# Repair an unusable per-user NuGet home so NuGet can create/read its
# per-user settings directory ($HOME/.nuget/NuGet). Sandbox images that
# pre-bake a package cache under $HOME/.nuget/packages as root often leave
# the parent directory foreign-owned or unwritable; raw `dotnet build`/
# `dotnet restore` then fail with "Failed to read NuGet.Config ...
# Permission denied" before any project-, solution-, or
# RestoreConfigFile-level config is honoured.
#
# The repair itself is the single source of truth in
# scripts/nuget-home-heal.sh — the same recovery build.sh, the audit gates,
# and the sibling InitialTargets hook all dot-source — so writability is
# never mistaken for ownership: an existing .nuget is admitted only as
# canonical storage owned by this uid or root with no co-tenant write bits
# (group-write tolerated only on uid-owned entries whose group is provably
# private to this account); a foreign-owned, co-tenant-writable, or
# symlinked entry is quarantined
# aside; a fresh private settings dir is created with a readable seeded
# config; and the package cache is relinked only when its WHOLE tree
# verifies as consumable storage — every descendant owned by this uid or
# root, never other-writable, and group-writable only under a provably
# private group, with no access-broadening ACL.
#
# When $HOME itself cannot be repaired in place, no in-place fix exists —
# the DOTNET_CLI_HOME redirect (the NuGetHomeSelfHeal preamble wrapping
# dotnet invocations, or the in-process NuGetHomeGuard.targets backstop) is
# the designed fallback, and the heal takes it inside this process too.
# Every path therefore exits 0: an impossible repair must never surface as
# an MSB3073 that masks the real path.
#
# Invoked from Directory.Build.targets (InitialTargets) so every raw
# `dotnet build` in this repository — including process:required-build and
# csharp:build-WaE — self-heals on a misprovisioned image.
set -u

codeybox_script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
codeybox_heal_script="$codeybox_script_dir/nuget-home-heal.sh"
if [ ! -f "$codeybox_heal_script" ]; then
    exit 0
fi

# CODEYBOX_NUGET_HOME_UNDER_TEST, when set, names the .nuget directory to
# repair — an override so tests can exercise the heal against a temporary
# home without touching the caller's real $HOME; the heal operates on the
# cli home above it. Either way this hook's mission is the $HOME-based home
# — an InitialTarget's exports cannot reach the already-running build
# anyway — so heal $HOME specifically, not a stamped DOTNET_CLI_HOME (that
# home is the sibling InitialTarget's job, Directory.NuGetHomeHeal.targets).
# Unset it so the shared recovery resolves HOME as the cli home to repair;
# the variable then comes back non-empty only when the heal took its
# redirect fallback.
if [ -n "${CODEYBOX_NUGET_HOME_UNDER_TEST:-}" ]; then
    HOME="$(dirname "${CODEYBOX_NUGET_HOME_UNDER_TEST%/}")"
    export HOME
fi
unset DOTNET_CLI_HOME
codeybox_home_before="${HOME:-}"
# Dot-sourcing runs the whole recovery: settings heal, runtime-dir pinning,
# and migration-marker seeding. The exports it makes die with this process
# (MSBuild already runs), but the on-disk repairs — quarantine, recreated
# private home, seeded config and markers — persist for every later exec.
. "$codeybox_heal_script" || true
if [ -n "${DOTNET_CLI_HOME:-}" ]; then
    codeybox_home_shown="${codeybox_home_before:-<unset>}"
    # HOME is launch-env input: strip control characters before it reaches
    # operator/CI logs. The helper lives in the dot-sourced recovery; fall
    # back to the raw value only if that source somehow did not define it.
    if command -v _codeybox_strip_ctrl >/dev/null 2>&1; then
        codeybox_home_shown="$(_codeybox_strip_ctrl "$codeybox_home_shown")"
    fi
    echo "ensure-writable-nuget-home: cannot repair $codeybox_home_shown/.nuget in place; relying on the DOTNET_CLI_HOME redirect" >&2
fi
exit 0
