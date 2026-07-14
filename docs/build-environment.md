# Build & verify-VM environment prerequisites

The CodeyBox solution builds warnings-clean under .NET 10 with a normally
provisioned developer or CI account. A root-owned NuGet home — the most common
verify-VM misconfiguration — is now **self-healed by the build itself** (see §1).
The remaining host prerequisites the repository cannot fully satisfy are
temp-disk headroom (§2) and host tooling / native-runtime availability for the
full test suite (§3). Getting those wrong produces failures that look like
source defects but are purely environmental.

## Verify-VM provisioning checklist

Before running `dotnet build ./CodeyBox.slnx` or `dotnet test`, confirm the
following as the unprivileged build user:

1. **NuGet home — auto-remediated, one caveat.** The build's pre-restore
   `PrepareNuGetHome` target (`Directory.Build.targets` → `scripts/prepare-nuget-home.sh`)
   relocates a `root`-created `~/.nuget` (or a `root`-created `~/.nuget/NuGet`
   settings subdirectory) out of the way and recreates a writable one before
   NuGet reads its settings, so no manual step is normally needed (see §1). The
   **one** thing the repository still cannot do without elevation is repair a
   `~` that is itself root-owned/unwritable: the relocation needs write
   permission on `$HOME`. Provision `$HOME` owned by the build user (the usual
   case) and the build self-heals; if `$HOME` itself is not writable, an operator
   must `chown` it (see §1).
2. **Temp headroom** — `Path.GetTempPath()` (`$TMPDIR`, default `/tmp`) is a real
   disk with several GiB free, not a small RAM tmpfs. The parallel test suite
   needs concurrent scratch space even though it now cleans up deterministically
   (see §2).

Reproduced on this VM: the exact gate command `dotnet build ./CodeyBox.slnx -c
Debug` reports `0 Warning(s), 0 Error(s)` — both with a healthy NuGet home and
against a deliberately `root`-owned/unreadable `~/.nuget/NuGet` (the self-heal
relocates it and restore reuses the cached packages) — and the temp-artifact
cleanup tests pass.

## 1. A root-owned NuGet home is self-healed before restore

`dotnet build` (and every `dotnet restore` / `dotnet test`) loads the
user-global NuGet settings from `$HOME/.nuget/NuGet/NuGet.Config` **before** it
consults any repository-level configuration. If that path exists but is not
readable by the build user, NuGet aborts settings loading with:

```
error : Failed to read NuGet.Config due to unauthorized access.
        Path: '<home>/.nuget/NuGet/NuGet.Config'.
        Access to the path '<home>/.nuget/NuGet' is denied. Permission denied
```

and, because no assemblies are produced, `dotnet test --no-build` then reports
each test DLL path as an "invalid argument".

This happens when a provisioning step runs as `root` and creates `~/.nuget/`
before the unprivileged build user runs. No committed build **input** can
redirect that read — a repo `nuget.config`, an MSBuild `RestoreConfigFile`
(`Directory.Build.props`), and the `NUGET_CONFIG_FILE`/`--configfile` options
were all verified to still fail, because NuGet ensures its user-settings
*directory* exists during settings load, ahead of every such override.

A committed **target**, however, runs before the read. `Directory.Build.targets`
defines `PrepareNuGetHome`, wired `BeforeTargets` the restore/settings targets,
which invokes `scripts/prepare-nuget-home.sh` to relocate an unusable home and
recreate a writable one — so an ordinarily-provisioned verify VM builds without
any manual pre-step. The helper is idempotent and a fast no-op when `~/.nuget`
is already usable (the normal developer / CI case), and the invocation is
best-effort: if the home is genuinely unremediable (e.g. `$HOME` itself is not
writable, so the relocation cannot proceed without elevation) the build falls
through to NuGet's own clear error above rather than masking it.

The remaining operator remediations below are only needed for that
unremediable case (root-owned `$HOME`), or to repair a home outside a build.

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
in-repo `PrepareNuGetHome` target described above automates this same
rename-aside strategy as an unconditional pre-restore step.

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

`Directory.Build.targets` defines the `PrepareNuGetHome` target and wires it
`BeforeTargets="Restore;_GenerateRestoreGraph;_GenerateRestoreGraphProjectEntry;_GetRestoreSettings;CollectPackageReferences"`
so the repair runs before NuGet reads user settings. The target invokes
`scripts/prepare-nuget-home.sh`, which relocates an unusable `$HOME/.nuget`
(or its `NuGet` settings subdirectory) to a PID-unique sidelined path,
recreates a writable `$HOME/.nuget`, and preserves any pre-baked `packages`
cache via symlink. Because a solution restore fans the target out across
projects that MSBuild may run in parallel, the script tolerates a lost `mv`
race (only one run can move the single source; losers skip) and closes with an
idempotent `mkdir -p`, so a writable home always exists afterwards regardless
of which run won. The invocation itself is best-effort (`|| true`) rather than
using MSBuild `ContinueOnError`, so no `/warnaserror`-promoted warning is
emitted on a genuinely unremediable host — the build simply falls through to
NuGet's own "unauthorized access" error instead of masking it.

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
avoid the failure on its own. The `Directory.Build.targets` `PrepareNuGetHome`
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
