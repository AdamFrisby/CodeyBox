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
# canonical storage owned by this uid or root with no co-tenant write bits;
# a foreign-owned, co-tenant-writable, or symlinked entry is quarantined
# aside; a fresh private settings dir is created with a readable seeded
# config; and the package cache is relinked only when its canonical dir is
# owned by this uid or root.
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

# This hook's mission is the on-disk $HOME/.nuget repair — an InitialTarget's
# exports cannot reach the already-running build anyway — so heal $HOME
# specifically, not a stamped DOTNET_CLI_HOME (that home is the sibling
# InitialTarget's job, Directory.NuGetHomeHeal.targets). Unset it so the
# shared recovery resolves HOME as the cli home to repair; the variable then
# comes back non-empty only when the heal took its redirect fallback.
unset DOTNET_CLI_HOME
codeybox_home_before="${HOME:-}"
# Dot-sourcing runs the whole recovery: settings heal, runtime-dir pinning,
# and migration-marker seeding. The exports it makes die with this process
# (MSBuild already runs), but the on-disk repairs — quarantine, recreated
# private home, seeded config and markers — persist for every later exec.
. "$codeybox_heal_script" || true
if [ -n "${DOTNET_CLI_HOME:-}" ]; then
    echo "ensure-writable-nuget-home: cannot repair $codeybox_home_before/.nuget in place; relying on the DOTNET_CLI_HOME redirect" >&2
fi
exit 0
