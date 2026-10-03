# CodeyBox Tart sandbox provider plugin

Sandbox provider plugin (`kind: tart`, plugin id `codeybox.tart-sandbox`)
that runs CodeyBox work items in [Tart](https://tart.run) VMs: macOS (and
Linux) guests on Apple Silicon hosts via Apple's Virtualization.framework,
driven over the local `tart` CLI with guest access over SSH. One project,
one plugin, contributed through the plugin trust model. **Off unless an
operator enables it.** This is the first credible path to native macOS and
Xcode workers.

## Containment posture — read this first

CodeyBox ships Linux-only precisely because the egress guarantee depends
on nftables, which macOS cannot provide. This provider therefore has
**no host-enforced egress filtering**, and the host classifies every
plugin kind `NotEnforced` — this plugin cannot promote itself, and nothing
below changes that classification. Do not claim enforced egress.

What isolation this provider **does** give:

- Each work item gets a fresh VM (separate guest kernel via Apple's
  Virtualization.framework) with no shared filesystem with the
  orchestrator host or with other VMs. Host mounts are staged into the
  guest file-by-file over SSH — never shared by reference.
- The guest SSH password travels in the SSH child-process environment
  (`SSHPASS`, via `sshpass -e`) or is replaced by key auth (`-i`); it
  never appears in argv, configuration files, logs, or the remote command
  string. Secret-bearing exec environments are staged as a root-only
  guest file over an stdin pipe and sourced-then-deleted; a staging
  failure fails the exec instead of falling back to inline transport.
- No process is ever launched through a shell string: `tart`, `ssh`,
  and `sshpass` are spawned as argv arrays, and every guest path or
  variable that reaches a remote shell is single-quote escaped.

What it **does not** give:

- No host-enforced egress filtering. Guest egress follows the Mac host's
  network, not any CodeyBox network profile. The provider refuses any
  sandbox naming a network profile, and placement never routes profiled
  work here — but an operator choosing this provider for unprofiled work
  is accepting host-default egress, not a CodeyBox allowlist.
- Suspend is stop/start: a suspended VM keeps its clone directory but
  running processes do not survive; the pipeline replays from its
  checkpoint, so this is expected, not data loss.
- No tmpfs semantics. Credential tmpfs mounts are refused outright (use
  environment variables); other tmpfs mounts need
  `AllowPersistentTmpfsDowngrade` and become persistent guest
  directories.
- No baseline bake, disk guard, cache seeding, or port publishing. The
  declared capabilities say exactly this, so placement refuses work that
  needs them instead of failing deep inside a phase.

## What an operator must configure

1. An Apple Silicon Mac with Tart installed and `sshpass` on the `PATH`
   (password auth), or an SSH key (key auth). Any other OS refuses with
   an infrastructure deferral naming `tart`.
2. Guest SSH credentials **from the host credential chain, never from
   configuration files**: `TART_SSH_PASSWORD` in the orchestrator process
   environment (the options type has no password property — configuration
   cannot carry one), or `SshPrivateKeyPath` to a host-owned key file.
3. Allowlist **and** enable the plugin (both gates must pass; changing
   `Enabled` requires a host restart):

```json
{
  "CodeyBox": {
    "Plugins": {
      "PackageDirectories": ["/etc/codeybox/plugins"],
      "Allowlist": ["codeybox.tart-sandbox"],
      "Enabled": ["codeybox.tart-sandbox"]
    },
    "Plugins:codeybox.tart-sandbox": {
      "Enabled": true,
      "DefaultImage": "ghcr.io/cirruslabs/macos-sequoia-xcode:latest"
    },
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          { "MemberId": "tart-mac", "ProviderKind": "tart", "Capacity": 2 }
        ]
      }
    ]
  }
}
```

4. Size `Capacity` for the host's RAM/CPU (1–2 per Mac): every sandbox
   is a full hardware-virtualized guest, and macOS images are tens of
   GB. The provider honours its cap, reports live load for least-loaded
   placement, and defers rather than overcommits.

### Full knob reference (`CodeyBox:Plugins:codeybox.tart-sandbox`)

All values are hot-reloadable through `IOptionsMonitor` except `Enabled`
(which requires a host restart). Operational values are config knobs,
never literals in source.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch; the provider refuses to provision until `true`. |
| `TartBinaryPath` | `tart` | Tart CLI binary (absolute path recommended). |
| `DefaultImage` | `ghcr.io/cirruslabs/macos-sequoia-base:latest` | Image cloned when the spec names none. Use an `-xcode` image for Xcode workers. |
| `NamePrefix` | `codeybox-` | VM name prefix; managed-VM filtering uses it. |
| `DefaultCpuCount` | `4` | Applied via `tart set` when the spec leaves CPU unset (1–32). |
| `DefaultMemoryGb` | `8` | Applied via `tart set` when the spec leaves memory unset (1–128 GiB). |
| `DefaultDiskGb` | `50` | Floor for `tart set --disk-size` (10–500). |
| `SshUsername` | `admin` | Guest SSH username (non-secret). |
| `SshPasswordEnvVar` | `TART_SSH_PASSWORD` | Env var holding the guest SSH password. Only the *name* is configured. |
| `SshPrivateKeyPath` | unset | Host path to an SSH private key; key auth wins over password auth. |
| `SshPort` | `22` | Guest SSH port. |
| `SshConnectTimeoutSeconds` | `10` | One SSH connect attempt. |
| `ReadyTimeoutSeconds` | `300` | Bound on waiting for guest SSH after `tart run`; refused credentials fail fast as `unauthorised`. |
| `TransitionTimeoutSeconds` | `180` | Bound on stop/delete transitions. |
| `PollIntervalMilliseconds` | `2000` | Guest-reachability poll cadence. |
| `CliTimeoutSeconds` | `120` | Per-invocation timeout for `tart` CLI calls. |
| `SetupCommands` | unset | Operator-trusted guest provisioning commands, run after boot before staging. |
| `MaxStageFileBytes` | `256 MiB` | Largest single staged file or sync-back file. |
| `AllowPersistentTmpfsDowngrade` | `false` | Downgrade non-secret tmpfs mounts to persistent guest dirs; credential tmpfs is always refused. |
| `ProvisioningRecheckSeconds` | `30` | Backoff floor on provisioning-deferred failures; throttling suggests a longer wait. |

## What it costs

Every `CreateAsync` clones a full VM image (tens of GB disk; macOS images
download ~25 GB on first pull) and runs a hardware-virtualized guest:
size `Capacity` for the host's RAM/CPU, not just disk. Stopped
(suspended) VMs keep their clone directory on disk until deleted. Apple
Silicon hosts only — there is no Linux-hosted path.

## What it cannot do

- Enforced-egress network profiles (refused at placement and at create).
- Graphical sandboxes and recovery-lease adopt (refused explicitly,
  naming `tart`, rather than provisioning something different).
- Baseline bake, cache seeding, disk guard, port publishing (not
  declared; placement refuses work requiring them).
- Live processes across suspend (stop/start; processes restart from the
  pipeline checkpoint).

## Verification

- Unit and recorded-shape tests: `dotnet test --filter
  "FullyQualifiedName~CodeyBox.Tests.Tart"` (kind resolution,
  `NotEnforced` classification, capability honesty, failure taxonomy,
  capacity accounting, `tart list` JSON/text fixtures).
- Live integration (`TartIntegrationTests`, tagged `requires_tart`,
  skipped unless `CODEYBOX_RUN_TART_INTEGRATION=1` with
  `TART_SSH_PASSWORD` on a macOS host): create → exec → file round-trip
  → suspend → resume → exec. Leaked `codeybox-live-*` VMs sweep with
  `tart list` / `tart delete`.
