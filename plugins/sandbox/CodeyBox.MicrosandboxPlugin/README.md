# CodeyBox.MicrosandboxPlugin

Local-first microVM sandbox provider backed by [microsandbox](https://microsandbox.dev).
Contributes the `microsandbox` provider kind (`ISandboxProvider`) through the plugin
trust model — off unless an operator allowlists `codeybox.microsandbox-sandbox` AND sets
`CodeyBox:Plugins:codeybox.microsandbox-sandbox:Enabled=true`.

Microsandbox runs OCI-image microVMs on the orchestrator host with live
branching: a running sandbox forks copy-on-write into parallel variants, which
maps onto ordinary sandbox handles (see Branching below) — no new pipeline concept.

The guest runs on infrastructure CodeyBox controls locally, but this is still a
plugin-contributed kind, so the host always classifies it `NotEnforced`:
acquisitions requiring enforced network egress (a named network profile) are
refused at placement. See `docs/extending/microsandbox-sandbox-plugin.md` for
configuration, credential-chain setup, capability coverage, failure
classification, branching semantics, and cost/limitation notes.
