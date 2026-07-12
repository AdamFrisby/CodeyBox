# Build environment prerequisites

The solution builds warnings-clean and its test suite passes on any correctly
provisioned .NET 10 host. This note records one **provisioning** prerequisite
that is external to the source tree, because it has recurred as a build-gate
failure when the build container is misconfigured.

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
avoid the failure on its own.)

### The `build.sh` entry point handles this itself

`build.sh` — the repository's canonical build entry point — applies the same
redirect as the gate: it points `DOTNET_CLI_HOME` at a script-owned, writable
directory (cleaned up on exit) and preserves any pre-baked `$HOME/.nuget/packages`
cache via `NUGET_PACKAGES` before running `dotnet build CodeyBox.slnx`. Running
`sh build.sh` therefore succeeds even when the image's `$HOME/.nuget` is owned by
another user.

### Raw `dotnet` invocations still need a writable home

A developer or tool invoking `dotnet build ./CodeyBox.slnx` **directly** (not via
`build.sh`) still depends on this prerequisite, because the redirect lives inside
the script rather than in tracked config files — NuGet touches the per-user
settings directory before honouring a repository `nuget.config`,
`-p:RestoreConfigFile`, or `Directory.Build.props`, each verified not to avoid
the failure on its own. For raw invocations, either provision a
build-user-writable `$HOME/.nuget` (e.g. `chown -R "$(id -un)" "$HOME/.nuget"`),
or invoke with `DOTNET_CLI_HOME=<writable dir> dotnet build ...` (NuGet resolves
the per-user settings directory relative to it).
