# Build environment prerequisites

The solution builds warnings-clean and its test suite passes on any correctly
provisioned .NET 10 host. Two things outside the source tree can still break it: a non-writable NuGet
home, and missing host tools. Both have repeatedly been mistaken for code
regressions, so both are recorded here with their remedies.

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
Multipass provisioning also `chown` the `$HOME/.nuget` parent directory
(not only the `packages` leaf). Root-created parents from ExtraRuncmd
`mkdir -p` otherwise leave NuGet unable to create its settings directory on
fresh clones. Prefer baking images this way; the in-repo self-heal remains the
backstop for already-baked baselines.

## Host-tool / native-runtime prerequisites for the full test suite

A handful of `CodeyBox.Tests` cases depend on host tooling or on a
correctly-executing self-contained native binary rather than on repository
source. They fail identically on `main` when those prerequisites are missing, so a
failure here is a provisioning gap, not a regression in the diff under review.
Check this list before blaming a work branch.

- **`file(1)` on `PATH`.**
  `AcpBridgeUnitTests.AcpBridge_PublishScript_RequiresMultipassWhenVmVerificationIsNotSkipped`
  (and its siblings) build a fake tool directory that hard-requires `file`,
  `bash`, `mktemp`, `cat`, … on `PATH`. A container without `file` installed
  throws `Required test tool not found on PATH: file` from `RequireExecutableOnPath`.
  Fix by installing `file` (e.g. `apt-get install -y file`).

- **A native ACP bridge binary that executes on the host.**
  `AcpBridgeUnitTests.Bridge_NativeResource_PosixSignalHandlers_*` run the
  published self-contained bridge (`src/CodeyBox.Agents.Claude/Resources/acp-bridge`).
  On a host whose CPU/libc the published binary was not built for, it emits its
  `bridge_started` envelope and then aborts with `SIGFPE` (process exit `136`)
  before the `ready` envelope, so `WaitForReadyEnvelopeAsync` observes a closed
  stdout and the `Assert.NotNull(lockPath)` fails. Fix by re-publishing the bridge
  for the host (`scripts/publish-acp-bridge.sh`) on a matching toolchain.

- **`flock(2)` release-on-close semantics on the staging filesystem.**
  `IncusBaselineProvisioningTests.ProvisioningWorkspaceRecovery_DeletesOwnedStaleTreeAndRejectsDeceptiveEntries`
  expects the advisory coordination lease (`IncusSafeFile.TryAcquireExclusiveLease`,
  `flock LOCK_EX|LOCK_NB`) taken during `IncusProvisioningWorkspace.Create` to be
  released when that `FileStream` is disposed, so a subsequent `RecoverStaleWorkspaces`
  can re-acquire it. On a staging filesystem that does not release `flock` promptly
  on close, the re-acquire returns `EWOULDBLOCK` and the test throws
  `Another CodeyBox process is creating or recovering an Incus provisioning
  workspace`. It reproduces deterministically in isolation on such a host; run the
  suite with the test temp root on a filesystem with standard `flock` semantics.

- **Headroom for timing-sensitive process/lifecycle waits.**
  `DefaultProcessRunnerCancellationTests.OutputLimitAfterRootExits_KillsOrphanedWriterProcess`
  and `IncusSandboxLifecycleTests.Dispose_WhenDeleteReportsSuccessButVmPersists_RetainsStagingUntilVerifiedRetry`
  assert on wall-clock-bounded process teardown / retry counts; they pass in
  isolation but can flake when the host is saturated under the parallel audit
  suite. Run the affected classes with spare CPU headroom.

## In-sandbox remediation when you cannot re-provision

When `chown` is unavailable — no root, `no_new_privs` set — but the build user
owns `$HOME`, rename the foreign-owned tree aside and re-expose the cache:

```bash
mv ~/.nuget ~/.nuget.foreign-owned
mkdir -p ~/.nuget/NuGet
ln -s ~/.nuget.foreign-owned/packages ~/.nuget/packages
```

A directory entry under `$HOME` can be renamed by the owner of `$HOME` even
when the entry itself belongs to another uid, so this needs no privilege. The
cache stays readable through the symlink, so restore still works offline.

This only helps a harness that reuses the *same* `$HOME` for the agent session
and the later build. An audit harness that mounts a fresh root-owned home each
iteration discards the rename and fails identically next time — that case needs
the image fixed at provisioning time, or the gate run with `DOTNET_CLI_HOME`
redirected. No repository-committed file can substitute: NuGet resolves and
reads the per-user settings path from `$HOME` before any target the repository
could hook has executed.
