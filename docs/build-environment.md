# Build environment prerequisites

The solution builds warnings-clean and its test suite passes on any correctly
provisioned .NET 10 host. This note records one **provisioning** prerequisite
that is external to the source tree, because it has recurred as a build-gate
failure when the build container is misconfigured — and the in-repo mitigations
that make raw `dotnet build` survive that misconfiguration.

## Writable per-user NuGet configuration directory

`dotnet build` / `dotnet restore` unconditionally read — and, when absent,
**create** — the per-user NuGet settings directory at
`$HOME/.nuget/NuGet/` (holding `NuGet.Config`) before any project-, solution-,
or `RestoreConfigFile`-level configuration is honoured. The build user must be
able to read and write that directory.

If the container image is baked such that `$HOME/.nuget` (or `$HOME/.nuget/NuGet`)
is owned by a different user (e.g. `root`) and not writable by the build user,
every project fails restore with:

```
error : Failed to read NuGet.Config due to unauthorized access.
        Path: '$HOME/.nuget/NuGet/NuGet.Config'.
        Access to the path '$HOME/.nuget/NuGet' is denied.
```

and, because no assemblies are produced, `dotnet test --no-build` then reports
each test DLL path as an "invalid argument".

### In-repo self-heal: `Directory.Build.targets`

`Directory.Build.targets` runs `scripts/ensure-writable-nuget-home.sh` as an
`InitialTargets` step before any project targets. When `$HOME/.nuget` exists
but is not writable, the script renames it aside (the build user owns `$HOME`,
so the rename does not need root), recreates a writable `$HOME/.nuget`, and
preserves any pre-baked `packages` cache via symlink. Concurrent MSBuild nodes
share a lock so solution builds do not race.

This is the operative remediation for auditors that invoke `dotnet` **raw**
(`process:required-build`, `csharp:build-WaE`) against a misprovisioned image:
those auditors do not go through `build.sh`, and a host orchestrator binary
that predates the gate's `DOTNET_CLI_HOME` redirect still runs the work
branch's MSBuild imports. A repository `nuget.config` / `-p:RestoreConfigFile`
cannot substitute — NuGet touches the per-user settings directory before
honouring them.

### The required-build gate handles this itself

The non-skippable required-build gate (`SandboxRequiredBuildVerifier`) runs its
`dotnet build` inside a sandbox whose image provisioning it does not control, so
it **cannot assume** a writable `$HOME`. Its build script therefore redirects
the CLI/NuGet per-user home to a script-owned, writable directory
(`DOTNET_CLI_HOME`) before building, and preserves any pre-baked global-packages
cache (`NUGET_PACKAGES`) so offline images keep restoring. A root-owned
`$HOME/.nuget` no longer fails the gate. (These are set inside the script rather
than via tracked config files because NuGet touches the per-user settings
directory before honouring a repository `nuget.config`,
`-p:RestoreConfigFile`, or `Directory.Build.props` — each was verified not to
avoid the failure on its own. The `Directory.Build.targets` InitialTargets
repair above is the complementary, branch-controlled path that helps when the
running orchestrator still embeds an older build script.)

### The `build.sh` entry point handles this itself

`build.sh` — the repository's canonical build entry point — applies the same
redirect as the gate: it points `DOTNET_CLI_HOME` at a script-owned, writable
directory (cleaned up on exit) and preserves any pre-baked `$HOME/.nuget/packages`
cache via `NUGET_PACKAGES` before running `dotnet build CodeyBox.slnx`. Running
`sh build.sh` therefore succeeds even when the image's `$HOME/.nuget` is owned by
another user.

### Baseline seeding must guest-own `$HOME/.nuget`

When a package-cache seed lands under `$HOME/.nuget/packages`, Incus and
Multipass provisioning now also `chown` the `$HOME/.nuget` parent directory
(not only the `packages` leaf). Root-created parents from ExtraRuncmd
`mkdir -p` otherwise leave NuGet unable to create its settings directory on
fresh clones. Prefer baking images this way; the in-repo self-heal remains the
backstop for already-baked baselines.
