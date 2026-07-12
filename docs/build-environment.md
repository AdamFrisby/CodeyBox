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

### This is not fixable from committed source

The following were each verified to **not** avoid the failure, because NuGet
touches the per-user settings directory before applying them:

- a repository-level `nuget.config` (at repo root or alongside the project);
- `dotnet build -p:RestoreConfigFile=<repo file>`;
- MSBuild `Directory.Build.props` properties.

The only mechanisms that avoid it operate on the environment, not on tracked
files:

- make `$HOME/.nuget` (and its `NuGet` subdirectory) owned by / writable to the
  build user — e.g. `chown -R "$(id -un)" "$HOME/.nuget"`; or
- point the CLI at a writable home before invoking the build:
  `DOTNET_CLI_HOME=<writable dir> dotnet build ...`
  (NuGet resolves the per-user settings directory relative to it).

Provisioning for build/audit containers must therefore guarantee a
build-user-writable `$HOME/.nuget`.
