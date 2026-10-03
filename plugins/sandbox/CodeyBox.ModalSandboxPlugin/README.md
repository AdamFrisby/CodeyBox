# CodeyBox.ModalSandboxPlugin

Hosted sandbox provider backed by [Modal Sandboxes](https://modal.com/docs/guide/sandboxes).
Contributes the `modal` provider kind (`ISandboxProvider`) through the plugin trust
model — off unless an operator allowlists `codeybox.modal` AND sets
`CodeyBox:Plugins:codeybox.modal:Enabled=true`.

Each sandbox is a container scheduled on Modal-run infrastructure, created from an
operator-baked custom image (or a filesystem snapshot), with per-exec streaming
output and snapshot-and-terminate teardown. High member capacity is expected:
concurrency is bounded by the member admission gate, not by local hardware. See
`docs/extending/modal-sandbox-plugin.md` for configuration, credential-chain
setup, capability coverage, failure classification, and cost/limitation notes.

Containment posture: hosted backends cannot carry the host's nftables egress
guarantee, so the host classifies this kind `NotEnforced` unconditionally. It
may only serve sandboxes with no named network profile and must never be
described as isolation.
