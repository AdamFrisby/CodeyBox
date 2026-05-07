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
