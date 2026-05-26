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
      "apt-get install -y curl ca-certificates git python3 python3-pip python3-venv nodejs npm golang rustup dotnet-sdk-10.0",
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
      "apt-get install -y dotnet-sdk-10.0"
    ]
  }
}
```

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
binaries surface as exit-127 dispatch failures only after the work item
starts (today's smoke probes verify credentials, not binary presence).
Bake them at baseline time so the first dispatch can actually run.

```json
{
  "CodeyBox": {
    "MultipassExtraRuncmd": [
      "set -eux\nexport DEBIAN_FRONTEND=noninteractive\napt-get update\napt-get install -y curl ca-certificates nodejs npm",
      "npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli",
      "curl -fsSL https://cursor.com/install | bash"
    ]
  }
}
```

Pick the subset that matches the agents you have registered. The
`@google/gemini-cli` install must be ≥ 0.1.9 if any agent-class member sets
`ReasoningMode=high`. Cursor installs the binary as `agent` (not
`cursor-agent`) — verify it lands on `$PATH` after the bake. After changing
any entry, delete cached `cb-baseline-*` images so the next sandbox launch
re-runs the bake.

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
