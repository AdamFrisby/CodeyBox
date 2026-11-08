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

### In-repo self-heal: MSBuild `InitialTargets`

MSBuild aggregates `InitialTargets` from the imported
`Directory.Build.props`/`.targets` and runs them at the very start of project
evaluation — before restore resolves the NuGet user-settings directory.
That is the only committed-file hook early enough to matter. The heal is
wired at two levels so destructive repair runs exactly once, at solution
level, before restore: `Directory.Solution.props` imports
`Directory.NuGetHomeHeal.targets` and sets `_CodeyBoxNuGetHealSolutionLevel`,
marking the once-per-solution invocation that alone may run the quarantine
(`mv` of an unusable `.nuget` aside). The flag is required, not decorative:
`Directory.Build.props` is also imported during solution evaluation and sets
`_CodeyBoxInSolution` there (because `$(SolutionPath)` is non-empty), which
would otherwise suppress the very target the solution hook exists to run.
During a solution build the per-project hooks in `Directory.Build.props`
(the same shared target, resolving `${DOTNET_CLI_HOME:-$HOME}`) and
`Directory.Build.targets` (`scripts/ensure-writable-nuget-home.sh`, always
resolving `$HOME`) are suppressed via `_CodeyBoxInSolution`. Outside a
solution build (`dotnet build foo.csproj`, `dotnet test`) those per-project
hooks still run — but NON-destructively: they export
`CODEYBOX_NUGET_HEAL_NO_QUARANTINE=1`, so the shared recovery skips the
quarantine and an unusable home is instead covered by the in-process
`NuGetHomeGuard.targets` task (imported by `Directory.Build.targets` and
`Directory.Solution.targets`), which dot-sources the same heal script with
`CODEYBOX_NUGET_HEAL_REPORT=1` and applies its machine-readable verdict
inside the MSBuild process: it redirects `DOTNET_CLI_HOME`/`HOME` to
verified storage and repoints the auto-pinned `RestorePackagesPath` at the
same whole-tree-verified package cache the heal itself would use — an
operator-supplied `RestorePackagesPath` is never overridden. Destructive repair
that renames a directory is never performed from a per-project
`InitialTarget`. All paths delegate the repair to the shared
`scripts/nuget-home-heal.sh` recovery —
whose settings/runtime-dir repair is a fast-path no-op when the home is
already usable (the only writes then are the settings dir's create-if-missing,
a transient create-then-delete writability probe inside it, and the idempotent
migration-marker seed, which may also materialize its candidate homes) and
which never fails the build. The quarantine notice is written to stderr and is
deliberately NOT suppressed by the MSBuild hook, so a solution-level repair is
always visible to the operator.

Group write bits are tolerated only on entries owned by the current user UID
whose group is provably private to that account: the gid must be one the
account itself holds (`id -G`), EVERY group-db entry for it across ALL NSS
modules must name no members but possibly the user (a keyless `getent group`
enumeration filtered on the gid field — a keyed `getent group <gid>` returns
only the first NSS module's match and would miss a duplicate-gid entry in a
later module such as sss/LDAP — unioned with `/etc/group`), no other account
may hold the gid as its primary group, and the gid must have at least one
group-db entry unless it is the account's own primary gid (`id -g`, the
user-private-group shape where membership is inherent to the account rather
than enumerated). A non-empty `getent` dump is not by itself proof of
coverage: a keyless `getent group`/`getent passwd` only iterates NSS modules
that support `setgrent`/`getgrent` enumeration, and sssd/AD/LDAP commonly run
with enumeration disabled while keyed lookups still resolve — so the proof
first requires that EVERY source configured for the `group` and `passwd`
databases in `nsswitch.conf` is enumerable (`files`/`compat`/`db`/`nis`/
`nisplus`/`yp`/`systemd` implement enumeration; `sss` is covered only when
`enumerate = true` is proven for every domain it serves across
`sssd.conf` + `sssd.conf.d` drop-ins; anything else — `ldap`, `winbind`,
unknown — fails closed), unioned with the direct `/etc/*` reads. Platforms or
topologies where groups/accounts cannot be enumerated or coverage cannot be
proven (macOS, musl, `sss` without `enumerate = true`, non-enumerable NSS
modules) fail closed and treat group-write as co-tenant-writable.
On standard
Linux systems with user-private groups (default umask 002), directories are
created `drwxrwxr-x` under exactly such a group. A uid-owned directory in a
*shared* group (`users`, `staff`, an LDAP/AD group), any foreign-owned
group-writable entry, and every other-writable (`o+w`) entry are still
rejected as co-tenant-writable.

"Usable" covers the consumed side too: a present `.nuget/packages` entry must
pass the whole-tree cache admission (uid-owned or root-owned, never
other-writable, group-writable only under a provably private group), or the
home is still healed. When `$HOME/.nuget` exists but is
not usable — foreign-owned, unwritable, unreadable config, a planted link, or an
unconsumable consumed cache — the heal relocates it aside (preserving it as
`~/.nuget.codeybox-unwritable.<pid>`), recreates a writable private
`$HOME/.nuget` with a seeded readable config, and preserves a trusted
(uid- or root-owned) pre-baked `packages` cache by symlink.

Under parallel MSBuild, destructive repair is strictly race-free:
1. Destructive repair runs only from the once-per-solution hook
   (`Directory.Solution.props`, `_CodeyBoxNuGetHealSolutionLevel`) before
   restore dispatches to projects. Per-project `InitialTargets` — in
   `Directory.Build.props`/`Directory.Build.targets` — always run with
   `CODEYBOX_NUGET_HEAL_NO_QUARANTINE=1`, so they can never rename `.nuget`
   aside no matter how many project nodes evaluate in parallel; a
   non-solution build's unusable home falls back to the in-process
   `NuGetHomeGuard.targets` admission check, which applies the heal script's
   verdict in-process — redirecting `DOTNET_CLI_HOME`/`HOME` and repointing
   the auto-pinned `RestorePackagesPath` at verified storage — instead of
   renaming anything.
2. The quarantine path is guarded by an advisory lock (a `mkdir` lock whose
   owner pid is recorded and reaped once dead; a lock whose pid was never
   recorded — e.g. the holder was killed mid-acquire — is reaped by age) and
   a completion marker in
   verified private user storage (`$DOTNET_CLI_HOME` / `$HOME`). The marker
   suppresses repeat renames only while the home stays unusable — it is
   removed as soon as a usable home is observed again, so a later breakage
   quarantines afresh instead of being suppressed forever. At most one
   quarantine directory is created per invocation, and concurrent nodes never
   rename a directory another node may be reading.

A `BeforeTargets="Restore"` target is *not* early enough: solution restore
aborts during graph construction, before any project's `Restore` chain runs. Nor
can a props file re-point the home in process — MSBuild rejects
`[System.Environment]::SetEnvironmentVariable` with `MSB4185`, since env
mutation is not on the property-function allowlist.

### The `NuGet-Migrations` named mutex needs a usable runtime dir at launch

Since runtime 10.0.13, user-scoped named mutexes resolve under
`$XDG_RUNTIME_DIR`, falling back to
`$HOME/.dotnet/corefx/Runtime/SharedMemory` — and the runtime caches that answer
at process start, so no in-process env change can help. NuGet creates that mutex
in two places before/inside `dotnet build`: first-use configuration and restore
(`MigrationRunner`). When the launch environment resolves neither variable to a
usable directory the CLI aborts with
`System.IO.IOException: The system cannot open the device or file specified. : 'NuGet-Migrations'`
(dotnet/runtime#135726, #136802) — before MSBuild starts, so no `InitialTargets`
hook can observe the failure itself.

`scripts/nuget-home-heal.sh` answers this on two axes. Dot-sourced, it pins an
unusable `HOME`/`XDG_RUNTIME_DIR` to a writable dir — which only helps the
current process tree, since exports die with the exec. The durable part is
`codeybox_seed_nuget_migration_state`: `MigrationRunner` checks
`File.Exists(<user-profile>/.local/share/NuGet/Migrations/1)` (or
`$XDG_DATA_HOME/NuGet/Migrations/1`) *before* creating the mutex, so seeding
that empty marker — the exact state NuGet leaves after a no-op migration run —
under every home a later exec could resolve (`$HOME`, `$DOTNET_CLI_HOME`, the
stamped `<workdir>/.dotnet-cli-home`, the shared `codeybox-nuget-home-<uid>`
fallback, the passwd-entry home, `$XDG_DATA_HOME`) keeps first-use and restore
off the mutex with no env cooperation at all. A home that cannot even be
traversed/written remains unfixable from the repository; that is image damage
the preamble redirect, not this heal, is designed to outlive.

This is the operative remediation for auditors that invoke `dotnet` **raw**
(`process:required-build`, `csharp:build-WaE`) against a misprovisioned image:
those auditors do not go through `build.sh`, and the work branch's MSBuild
imports run even when the orchestrator binary driving them is older than the
gate's `DOTNET_CLI_HOME` redirect. A repository `nuget.config` / `-p:RestoreConfigFile`
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
`-p:RestoreConfigFile`, or `Directory.Build.props` — none of which avoids the
failure. The `Directory.Build.targets` InitialTargets repair above is the
complementary, branch-controlled path.)

### The `build.sh` entry point handles this itself

`build.sh` — the repository's canonical build entry point — dot-sources the
same `scripts/nuget-home-heal.sh` recovery before running `dotnet` (or any
forwarded gate command): it repairs a foreign-owned `$HOME/.nuget` in place
when `$HOME` is writable, and otherwise redirects `DOTNET_CLI_HOME` (and the
runtime-dir variables) to a verified per-user fallback, reusing the inherited
package cache when its owner is trusted. Running `sh build.sh` therefore
succeeds even when the image's `$HOME/.nuget` is owned by another user.

### Baseline seeding must guest-own `$HOME/.nuget`

When a package-cache seed lands under `$HOME/.nuget/packages`, Incus and
Multipass provisioning also `chown` the `$HOME/.nuget` parent directory
(not only the `packages` leaf). Root-created parents from ExtraRuncmd
`mkdir -p` otherwise leave NuGet unable to create its settings directory on
fresh clones. Prefer baking images this way; the in-repo self-heal remains the
backstop for already-baked baselines.

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
