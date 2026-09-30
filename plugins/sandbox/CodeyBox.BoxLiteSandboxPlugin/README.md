# CodeyBox.BoxLiteSandboxPlugin

Embedded/local microVM sandbox provider backed by a BoxLite daemon. Contributes
the `boxlite` provider kind (`ISandboxProvider`) through the plugin trust
model — off unless an operator allowlists `codeybox.boxlite-sandbox` AND sets
`CodeyBox:Plugins:codeybox.boxlite-sandbox:Enabled=true`.

Each sandbox is a hardware-isolated microVM with its own guest kernel booted
from an OCI image on the local host. The guest network restriction is
daemon-side defence in depth, so this kind is always classified `NotEnforced`
by the host: acquisitions requiring enforced network egress (a named network
profile) are refused at placement. See
`docs/extending/boxlite-sandbox-plugin.md` for configuration, credential-chain
setup, capability coverage, failure classification, and cost/limitation notes.
