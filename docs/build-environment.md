# Build & verify-VM environment prerequisites

The solution builds warnings-clean and its test suite passes on any correctly
provisioned .NET 10 host. This note records the **provisioning** prerequisites
that are external to the source tree — because they have recurred as build-gate
failures when the build / audit ("verify") VM is misconfigured — together with
the in-repo mitigations that make raw `dotnet build` survive those
misconfigurations where possible.

## Verify-VM provisioning checklist

Before running `dotnet build ./CodeyBox.slnx` or `dotnet test`, confirm both of
the following as the unprivileged build user (neither can be fixed from inside
this repository):

1. **Writable NuGet home** — `test -w "$HOME/.nuget"` succeeds *and* any existing
   `$HOME/.nuget/NuGet` settings subdirectory is readable/writable, or
   `$HOME/.nuget` is absent so dotnet can recreate it. A `root`-created
   `~/.nuget` (or a `root`-created `~/.nuget/NuGet` under an otherwise-writable
   home) aborts every restore/build/test before source is even compiled
   (see §1). `scripts/prepare-nuget-home.sh` checks both levels.
2. **Temp headroom** — `Path.GetTempPath()` (`$TMPDIR`, default `/tmp`) is a real
   disk with several GiB free, not a small RAM tmpfs. The parallel test suite
   needs concurrent scratch space even though it now cleans up deterministically
   (see §2).

Reproduced on this VM: with a writable NuGet home the exact gate command
`dotnet build ./CodeyBox.slnx -c Debug` reports `0 Warning(s), 0 Error(s)` and
the temp-artifact cleanup tests pass — confirming the recurring build/test gate
failures are the host prerequisites below, not a source defect.

## 1. The build user must own a writable NuGet home

`dotnet build` / `dotnet restore` / `dotnet test` unconditionally read — and,
when absent, **create** — the per-user NuGet settings directory at
`$HOME/.nuget/NuGet/` (holding `NuGet.Config`) before any project-, solution-,
or `RestoreConfigFile`-level configuration is honoured. The build user must be
able to read and write that directory.

If the container / VM image is baked such that `$HOME/.nuget` (or
`$HOME/.nuget/NuGet`) is owned by a different user (e.g. `root`, from a
provisioning step that ran privileged before the unprivileged build user did)
and not writable by the build user, every project fails restore with:

```
error : Failed to read NuGet.Config due to unauthorized access.
        Path: '$HOME/.nuget/NuGet/NuGet.Config'.
        Access to the path '$HOME/.nuget/NuGet' is denied.
```

and, because no assemblies are produced, `dotnet test --no-build` then reports
each test DLL path as an "invalid argument".

A repository `nuget.config`, an MSBuild `RestoreConfigFile`
(`Directory.Build.props`), and the `NUGET_CONFIG_FILE` environment variable
were all verified to still fail on their own, because NuGet touches the
per-user settings directory during settings load, ahead of every override.
Only relocating the user-settings home fixes it — own/`chown` `~/.nuget`, move
it aside, or point `DOTNET_CLI_HOME` at a writable directory (all three
remediations are shown below, and the gate / `build.sh` sections cover the
`DOTNET_CLI_HOME` route in particular).

### Operator remediation

Run as the account that owns `$HOME`:

```sh
# Give the build user ownership of its own NuGet home, or remove the
# root-created directory and let dotnet recreate it writable.
sudo chown -R "$(id -un):$(id -gn)" "$HOME/.nuget"
chmod -R u+rwX "$HOME/.nuget"
```

If the build user has no `sudo` (so `chown` on a root-owned `~/.nuget` is
impossible) but *does* own its home directory, the root-owned tree can still be
moved aside without elevation — renaming an entry needs write permission on the
**parent** directory, not on the entry itself. `scripts/prepare-nuget-home.sh`
performs exactly this, idempotently (a no-op when `~/.nuget` is already
usable — top level writable and any `NuGet` settings subdirectory accessible),
so it is safe to run unconditionally as a build-user pre-step:

```sh
# Runs as the unprivileged build user; no sudo required.
scripts/prepare-nuget-home.sh
dotnet build ./CodeyBox.slnx
```

The script is the executable form of these manual steps:

```sh
mv "$HOME/.nuget" "$HOME/.nuget.root-owned"          # write on $HOME suffices
mkdir -p "$HOME/.nuget"                               # fresh, build-user-owned
ln -s "$HOME/.nuget.root-owned/packages" "$HOME/.nuget/packages"  # reuse cache
```

The world-traversable `packages/` cache under the old tree stays readable, so
restore reuses it instead of re-downloading, while NuGet can now create its
`~/.nuget/NuGet/NuGet.Config` in the writable home. This was verified to make
`dotnet build ./CodeyBox.slnx` and `dotnet test` succeed on this VM. The
in-repo self-heal below automates this same rename-aside strategy.

If touching the filesystem is undesirable, the same result is achievable purely
via the environment: point `DOTNET_CLI_HOME` at a writable directory before the
build. NuGet derives its user-settings directory from the CLI home, so this
relocates `NuGet.Config` off the unreadable `~/.nuget/NuGet` without renaming or
`chown`. Verified to make restore succeed against the root-owned tree:

```sh
export DOTNET_CLI_HOME="$(mktemp -d)"   # any dir the build user can write
dotnet build ./CodeyBox.slnx
```

Note the config-file overrides do **not** help: a repo `nuget.config`, the
MSBuild `RestoreConfigFile` property, and the `NUGET_CONFIG_FILE`/`--configfile`
options were all re-verified to still fail, because NuGet ensures its
user-settings *directory* exists during settings load — before any of those
overrides apply. Only relocating the home (`DOTNET_CLI_HOME`, the `mv`, or a
`chown`) works.

Prefer provisioning `~/.nuget` owned by the build user (or leaving it absent
so `dotnet` recreates it) as part of VM baking, not at first build.

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

## 2. The temp filesystem must have real headroom

The test suite creates a per-test SQLite database (`<guid>.db` plus its `-wal`
and `-shm` siblings) and git / log / agent-stream working directories under
`Path.GetTempPath()`. The suite now cleans these up deterministically in
teardown (see `TestTempArtifacts` / `TestTempDirectory` in
`tests/CodeyBox.Tests/`), but the parallel `WebApplicationFactory` tests still
need genuine concurrent free space while running.

On a RAM-backed or undersized `/tmp`, the filesystem can fill mid-run and SQLite
fails to create tables — surfacing as `no such table: work_items` and
`test-project cannot be removed` cascades across otherwise-passing tests. Point
`TMPDIR` at a spacious real disk, or size the verify VM's `/tmp` accordingly,
before running the full suite.

## 3. Host-tool / native-runtime prerequisites for the full test suite

A handful of `CodeyBox.Tests` cases depend on host tooling or on a
correctly-executing self-contained native binary rather than on repository
source. They **fail identically on `main` (verified against base commit
`925943c2`, which predates every change on this branch)** when those external
prerequisites are missing, so they are provisioning gaps, not regressions of any
source change. They are recorded here so a misprovisioned container is diagnosed
against this list instead of a work branch's diff.

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
