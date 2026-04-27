# CodeyBox.Sandbox.Kata

Skeleton provider for Kata Containers + Firecracker. Implementation is not yet
wired up; see `docs/sandbox-providers.md` for the host-side setup checklist
and the intended runtime invocation.

The interface (`ISandboxProvider`) is stable — orchestrator code does not need
to change when this provider is filled in. Replacing the dev-only
`Sandbox.Process` provider with this one is a single DI registration swap in
`CodeyBox.Api/Program.cs`.

## Why Firecracker

Plain containers share the host kernel; a kernel-level escape from the agent
process becomes a host compromise. Firecracker microVMs have their own
kernel, so a kernel CVE in the guest does not give the attacker the host.
That is the entire reason this framework exists.
