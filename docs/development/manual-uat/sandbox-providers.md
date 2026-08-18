# Sandbox Providers Manual UAT

These procedures cover the Sandbox Providers section. Run them on a disposable host or disposable
project; several steps intentionally create and destroy sandboxes.

## Multipass Real VM Launch And Baseline Clone

1. Install and verify Multipass on the host, then configure
   `CodeyBox:SandboxProvider=multipass`.
   - Expected: CodeyBox starts and logs the Multipass provider selection.
2. Queue a disposable work item that runs a simple command and writes one file.
   - Expected: a `codeybox-*` VM launches, mounts `/work` and repository state,
     receives environment through `.codeybox-env`, and is deleted after the item.
3. Enable `CodeyBox:MultipassUseBaselineImages=true` with one configured network
   profile and a harmless `MultipassExtraRuncmd`.
   - Expected: the first run bakes one stopped `cb-baseline-*` VM for the
     profile; later runs clone from it instead of re-running the install step.
4. Compare the first bake time with a later clone-backed sandbox creation.
   - Expected: clone-backed creation is materially faster and still reaches the
     same command/mount/env behavior.

## Multipass Profile Egress Enforcement

1. Run `scripts/setup-host-networks.sh` for at least one restricted profile and
   map that profile in `CodeyBox:SandboxNetworkProfiles`.
   - Expected: the configured host bridge exists and nftables rules are loaded.
2. Queue a work item with `SandboxNetworkPolicy.ProfileName` set to the mapped
   profile.
   - Expected: the VM is attached to the mapped bridge and logs the selected
     host-enforced profile.
3. From inside the VM, attempt egress to an allowed host and a disallowed host.
   - Expected: allowed egress succeeds; disallowed egress is blocked by host
     firewall rules, not by an in-guest firewall.
4. Repeat with no profile configured.
   - Expected: the VM falls back to the default Multipass network, and host
     firewall policy blocks unintended egress.

## Bubblewrap Namespace Inspection

1. Install `bubblewrap` on a Linux host with user namespaces enabled and set
   `CodeyBox:SandboxProvider=bubblewrap`.
   - Expected: CodeyBox starts without requiring a daemon.
2. Queue a disposable item with an empty allowed-host list.
   - Expected: the process runs with mount, PID, IPC, UTS, user, cgroup, and
     network namespaces; `/proc/net/dev` shows only loopback.
3. Repeat with at least one allowed host configured.
   - Expected: the provider logs that host allowlists are not enforced by
     Bubblewrap and the sandbox shares the host network namespace.
4. Cancel a running command.
   - Expected: the provider kills the process tree and returns cancellation to
     the runner.

## Stale Sandbox Cleanup Drill

1. Set `CodeyBox:SandboxLeak:AutoDispose=false`, then create or preserve a
   disposable `codeybox-*` Multipass VM older than the configured leak threshold.
   - Expected: `GET /sandboxes/leaked` lists the VM after the reaper sweep.
2. Create or preserve another VM with a `.codeybox-preempt` marker younger than
   `PreemptRetention`.
   - Expected: the marked VM is not reported as leaked.
3. Call `POST /sandboxes/leaked/{name}/dispose` for the listed stale VM.
   - Expected: the VM is deleted and purged, the endpoint returns success, and a
     repeated dispose returns not found.
4. Re-enable default `CodeyBox:SandboxLeak:AutoDispose=true` on a disposable
   host.
   - Expected: eligible leaks are disposed during the sweep, and failures for one
     VM do not prevent disposal attempts for others.
