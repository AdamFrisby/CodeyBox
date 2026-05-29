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

**Host prerequisites:** Multipass installed, graphical sandbox bridge configured
(`scripts/setup-host-networks.sh`), egress for `apt`/`dotnet` inside the VM.

## Integration tests

Unit tests stub `ISandbox`. To exercise the real Multipass path:

```bash
CODEYBOX_HARNESS_INTEGRATION=1 dotnet test tests/CodeyBox.Tests --filter LaunchAsync_RealMultipassPath
```
