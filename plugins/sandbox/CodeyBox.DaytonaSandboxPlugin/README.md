# CodeyBox.DaytonaSandboxPlugin

Hosted sandbox provider backed by [Daytona](https://www.daytona.io). Contributes
the `daytona` provider kind (`ISandboxProvider`) through the plugin trust
model — off unless an operator allowlists `codeybox.daytona-sandbox` AND sets
`CodeyBox:Plugins:codeybox.daytona-sandbox:Enabled=true`.

The guest runs on infrastructure CodeyBox does not control, so this kind is
always classified `NotEnforced` by the host: acquisitions requiring enforced
network egress (a named network profile) are refused at placement. See
`docs/extending/daytona-sandbox-plugin.md` for configuration, credential-chain
setup, capability coverage, failure classification, and cost/limitation notes.
