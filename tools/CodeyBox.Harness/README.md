# CodeyBox.Harness

Dev/test entrypoint for the exploratory-testing **app-launch harness**. Brings a
target web app up inside a graphical Multipass sandbox in a deterministic state,
ready to be driven by `ComputerUseBridge` (real keyboard/mouse + screenshots).

## JobTrack pilot

```bash
# One-shot smoke: launch → readiness screenshot → teardown
dotnet run --project tools/CodeyBox.Harness -- \
  jobtrack launch --source /path/to/jobtrack

# Hold the session open for manual driving; Ctrl+C tears down the VM
dotnet run --project tools/CodeyBox.Harness -- \
  jobtrack launch --source /path/to/jobtrack --interactive
```

| Flag / env | Purpose |
|---|---|
| `--source` / `JOBTRACK_SOURCE` | Host directory containing the JobTrack repo (mounted at `/work`) |
| `--screenshot-out` | PNG written when the UI is considered rendered (default: `harness-ready.png`) |
| `--interactive` | Keep sandbox alive until Ctrl+C |
| `CODEYBOX_GRAPHICAL_BRIDGE` | Host bridge for the `graphical` network profile (default: `cb-graphical`) |

## OpenStack smoke

```bash
# Acquire one openstack sandbox, run uname -a + file stage round-trip, dispose
dotnet run --project tools/CodeyBox.Harness -- \
  openstack-smoke --config /path/to/appsettings.json
```

Reads the same `CodeyBox:Plugins:codeybox.openstack-sandbox` options and the
same `OS_*` credential variables as the provider
(`OS_APPLICATION_CREDENTIAL_ID`, `OS_APPLICATION_CREDENTIAL_SECRET`, plus
`OS_AUTH_URL` / `OS_REGION_NAME` fallbacks), so a green run means the
configured cloud actually boots, answers SSH, and stages files. Prints
per-step timings; exits non-zero when any step fails. The sandbox is always
disposed, including on failure or Ctrl-C. Full operator reference lives in
`docs/extending/openstack-sandbox-plugin.md`.

**Host prerequisites:** Multipass installed, graphical sandbox bridge configured
(`scripts/setup-host-networks.sh`), egress for `apt`/`dotnet` inside the VM.

## Integration tests

Unit tests stub `ISandbox`. To exercise the real Multipass path:

```bash
CODEYBOX_HARNESS_INTEGRATION=1 dotnet test tests/CodeyBox.Tests --filter LaunchAsync_RealMultipassPath
```
