# Baseline Bake Examples

`CodeyBox:MultipassExtraRuncmd` is operator-supplied shell that bakes tools
into the sandbox image before work starts. Install the tools your projects
need; CodeyBox does not assume one privileged language stack.

If a language auditor is enabled but its tool is missing, the auditor emits an
Info finding and skips instead of blocking the work item. Install the missing
tool and re-run audit to get enforcement.

## Polyglot Sandbox

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y curl ca-certificates git python3 python3-pip python3-venv nodejs npm golang rustup",
      "curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh",
      "bash /tmp/dotnet-install.sh --version 10.0.301 --install-dir /usr/share/dotnet",
      "ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet",
      "dotnet --version | grep -Fx 10.0.301",
      "python3 -m pip install --break-system-packages ruff mypy pytest pip-audit",
      "npm install -g prettier eslint",
      "go install golang.org/x/vuln/cmd/govulncheck@latest",
      "cargo install cargo-audit",
      "rustup component add rustfmt clippy"
    ]
  }
}
```

## C# Minimal

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y curl ca-certificates",
      "curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh",
      "bash /tmp/dotnet-install.sh --version 10.0.301 --install-dir /usr/share/dotnet",
      "ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet",
      "dotnet --version | grep -Fx 10.0.301"
    ]
  }
}
```

## Pinning .NET SDKs

Do not bake C# baselines from a floating .NET SDK channel such as
`dotnet-install.sh --channel 10.0` or an unpinned `dotnet-sdk-10.0` package.
`dotnet format` behavior can change across SDK feature bands, and audit-tool
sandboxes must use the same formatter version as the mechanical
`dotnet-format` fixer. Pin an exact SDK version in `MultipassExtraRuncmd`,
verify it with `dotnet --version`, and deliberately bump the version in both
the baseline config and any developer/CI images when you want formatter rules
to change.

## Python Minimal

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y python3 python3-pip python3-venv",
      "python3 -m pip install --break-system-packages ruff mypy pytest pip-audit"
    ]
  }
}
```

## Node Minimal

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y nodejs npm",
      "npm install -g prettier eslint"
    ]
  }
}
```

## Go Minimal

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y golang",
      "go install golang.org/x/vuln/cmd/govulncheck@latest"
    ]
  }
}
```

## Rust Minimal

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y rustup",
      "rustup default stable",
      "rustup component add rustfmt clippy",
      "cargo install cargo-audit"
    ]
  }
}
```

## Agent CLIs

Every `AgentKind` registered in an agent class needs its CLI baked into the
sandbox image. The orchestrator does not install agent binaries; missing
binaries fail the baseline bake during the post-install in-VM verification
(`--version`) checks for configured agents. Bake them at baseline time so the
first dispatch can actually run.

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y curl ca-certificates nodejs npm",
      "npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli",
      "curl -fsSL https://cursor.com/install | bash"
    ],
    "MultipassExecutableProvisions": [
      {
        "HostSourcePath": "/home/<operator>/.codeybox/agy-seed/agy",
        "VmDestPath": "/home/ubuntu/.local/bin/agy",
        "VmSymlinks": ["/usr/local/bin/agy"],
        "Label": "antigravity"
      }
    ]
  }
}
```

Pick the subset that matches the agents you have registered. For Gemini,
reasoning level is encoded in the model id (e.g. `gemini-3-flash-preview`),
not a CLI flag — there is no `--thinking` flag to pin a version against.
Cursor installs the binary as `agent` (not `cursor-agent`) — verify it lands
on `$PATH` after the bake. **Antigravity (`agy`) is provisioned via
`MultipassExecutableProvisions`, NOT a `curl … | bash` runcmd entry**: the
upstream installer URL (`https://antigravity.google/cli/install.sh`) no longer
returns a shell script — as of 2026-06-17 it serves the Antigravity landing
HTML, which fails `bash` parsing and would silently slip past a `… || true`
runcmd. Stage a vetted copy of the binary on the host (e.g. extracted from a
known-good Multipass VM) and point `HostSourcePath` at it; the provisioner
sets mode 0755 and ownership root:root deterministically via `install -m
0755 -o root -g root`, then drops a `/usr/local/bin/agy` symlink onto the
non-login sandbox PATH. The baseline bake runs the registered `--version`
checks for every configured CLI-backed agent before marking the image ready
to clone, so a missing `agy` (or a missing host-staged file) fails the bake
instead of surfacing as dispatch exit 127. The standalone Copilot
CLI (binary name
`copilot`) is operator-supplied; do **not** substitute
`gh extension install github/gh-copilot`, which produces a `gh copilot`
subcommand and will not satisfy the runner. opencode does not yet ship a
runner in this repo; operators tracking the integration can pre-stage with
`curl -fsSL https://opencode.ai/install | bash`, but the orchestrator will
not dispatch to it until a runner is registered. After changing any entry,
delete cached `cb-baseline-*` images so the next sandbox launch re-runs the
bake.

## Security Tooling

The `security:gitleaks` and `security:semgrep` auditors skip with an Info
finding when their tools are missing. Bake them into the audit sandbox so
they enforce instead of skipping:

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "apt-get update",
      "apt-get install -y curl ca-certificates python3 python3-pip",
      "curl -fsSL -o /usr/local/bin/gitleaks.tgz https://github.com/gitleaks/gitleaks/releases/latest/download/gitleaks_linux_x64.tar.gz",
      "tar -xzf /usr/local/bin/gitleaks.tgz -C /usr/local/bin gitleaks && rm /usr/local/bin/gitleaks.tgz",
      "python3 -m pip install --break-system-packages semgrep"
    ]
  }
}
```
