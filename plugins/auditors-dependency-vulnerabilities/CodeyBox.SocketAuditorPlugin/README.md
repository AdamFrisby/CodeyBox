# CodeyBox: socket Dependency Supply-Chain Auditor

Auditor plugin wrapping [socket](https://github.com/SocketDev/socket-cli)
(Socket.dev's CLI): it scans the audited repository's worktree
(`socket scan create --report`) for dependency supply-chain risk signals —
malware, typosquats, protestware, telemetry, troll packages, deprecated or
unmaintained dependencies, CVEs, and related package alerts — evaluated
against the organisation's Socket security policy, reporting every alert as
an audit finding from the JSON policy report.

## What it reports

- One finding per alert in the JSON policy report on **stdout**. The
  report nests alerts as
  `ecosystem → package → version → file → "line:col" → alert`; the leaf's
  `type` (the Socket alert type, e.g. `telemetry`, `deprecated`, `cve`)
  becomes the finding's rule id, so `IncludedRules`/`ExcludedRules` select
  by alert type.
- **Severity: mapped, severity-driven gate.** The report path ignores each
  alert's own severity field — the organisation-policy action is the
  verdict — and the auditor maps those actions to CodeyBox severities:
  `error` → Error, `warn` → Warning, `monitor`/`ignore`/`defer` → Info,
  unknown → Warning. Error findings fail the audit; warnings are advisory.
  `MinimumSeverity` only drops findings, it never raises them. The report
  is requested with `--report-level monitor` so every mappable signal
  (error, warn, monitor) reaches the mapping; `--report-level` is reserved
  in `ExtraArguments`.
- **Location: file and line where supplied.** The nesting keys carry the
  manifest file and the `line:col` occurrence, preserved in the finding
  (e.g. `package-lock.json:12`). When the operator folds the report
  (`--fold`, allowed via `ExtraArguments`), shallower levels carry no file
  keys and the finding falls back to the alert's first manifest file.

## What it cannot see

- **Git-ignored files.** Socket's manifest discovery skips everything the
  repository's `.gitignore` lists (plus its own default ignores): a risky
  dependency reachable only through an ignored manifest produces no alert.
  Absence of findings for ignored files is a tool limitation, not evidence
  of safety.
- **Ecosystems Socket does not cover or the repo disables.** See
  `socket.json` below: a `disabled` ecosystem is skipped entirely.
- **Reachability.** Plain scans report that a risky package is present, not
  whether the audited code reaches it. Pass `--reach` via `ExtraArguments`
  for full application reachability analysis (needs build tooling and
  substantially more time/quota).
- **License policy.** License alerts are only evaluated when the scan
  requests them; this auditor scans the security policy. License posture is
  out of scope.
- **Alerts below `monitor`.** Policy actions `ignore`/`defer` are muted by
  the organisation's own policy and are not requested in the report.

## Exit codes and failure classification

Socket does **not** follow the common "0 = clean, 1 = findings"
convention — verified against the 1.4.1 binary. Exit `1` means both "the
report is unhealthy" and every operational failure, so the JSON envelope on
stdout (not the exit code) is the verdict:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | `{ok:true,…}` report, `healthy:true` | ran (clean, or only warn/monitor-or-below alerts — the report is the verdict either way) | pass / findings |
| `1` | `{ok:true,…}` report, `healthy:false` | ran, organisation-policy violation | findings |
| `1` | `{ok:false,…}` error envelope | could not run: missing API token at call time, API error, network failure | infrastructure |
| `2` | `{ok:false,…}` error envelope | could not run: incorrect usage or failed input validation (missing org, missing API token, unknown arguments) | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

An unhealthy report that carries no extractable alerts likewise fails
closed as infrastructure — a scan the tool itself flagged must never pass
silently (this is also why `--short` is reserved in `ExtraArguments`).

A missing `socket` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit.

## Version pinning and provisioning

The auditor is pinned to **socket `1.4.1`** (`ExpectedVersion` in scoped
config): the alert taxonomy, policy evaluation, and report shape change
between releases, so an unpinned binary would change findings under you.
`socket --version` is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned socket):

- `socket` — install the versioned npm release
  (`npm install -g socket@1.4.1`) through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`, authenticate once (`socket login` writes the
  default org and token, or export `SOCKET_CLI_API_TOKEN` in the
  baseline), and configure the default org.

The requirement reaches baseline provisioning only while the plugin is
enabled.

## Credentials, organisation, and quota

Every run uploads the discovered manifests to the Socket API and evaluates
them against the organisation's security policy, consuming Socket quota
(one scan plus one report per run):

- **Token:** provision `SOCKET_CLI_API_TOKEN` (or a logged-in CLI config)
  in the sandbox baseline. Without a token the CLI fails closed
  (`{ok:false}` "requires a Socket API token", exit 2) — never a pass.
- **Organisation:** set `Org` in scoped config (`--org`), or rely on the
  CLI's configured default / auto-discovery. Without any org the CLI fails
  closed (exit 2) — never a pass. `--org` is reserved in
  `ExtraArguments`; use the `Org` key.
- **Repository naming:** `--repo`/`--branch` (via `ExtraArguments`) control
  the dashboard labels; audit scans run with `--tmp`, so they stay
  temporary and never become the organisation alerts page
  (`--tmp`/`--set-as-alerts-page` are reserved).

## Policy configuration and repository-controlled files

Socket reads `socket.json` from the scan root **and** from nested
directories (a per-directory cascade) to take flag defaults for manifest
generation. Disabled ecosystems are skipped entirely, and build-tool
selections (`bin`, `gradleOpts`, `sbtOpts`, `excludeConfigs`, …) point the
CLI at executables and options from inside the audited tree — a
suppression and steering surface the audit subject could use to hide a
risky dependency, so any `socket.json` under the worktree fails the audit
as a deterministic infrastructure error by default. Operators who
deliberately trust repo-authored config set
`TrustRepositorySuppression: true`.

`--cwd` is reserved in `ExtraArguments`: it would redirect the scan
outside the audited worktree. `--dry-run`/`--read-only` (bail before
scanning/reporting — exit 0 with no verdict) and `--interactive`
(prompts hang the sandbox — the auditor pins `--no-interactive`) are
reserved for the same reason: each would turn the audit into a silent
pass.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.socket"],
      "Enabled": ["codeybox.socket"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.socket" }
```

Only then does the declared `socket` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.socket`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.4.1` | Pinned socket release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Org` | — (CLI default / auto-discovery) | `--org` — the Socket organisation slug the scan is billed and evaluated against. |
| `TrustRepositorySuppression` | `false` | Allow repository-authored `socket.json` (at any depth) instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact Socket alert types (the `type` field, e.g. `telemetry`, `cve`) to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--repo my-app`, `--branch main`, `--exclude-paths`, `--reach`, `--fold`. Reserved: `--json`, `--markdown`, `--report`, `--report-level`, `--short`, `--org`, `--dry-run`, `--read-only`, `--interactive`, `--cwd`, `--tmp`, `--no-tmp`, `--set-as-alerts-page`, `--no-set-as-alerts-page`. |
| `TimeoutSeconds` | `300` | Per-run bound — covers manifest upload, server-side analysis, and report generation; exceeding it is infrastructure, not a pass. First runs against large trees may need more. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Network egress

The auditor declares `AuditCapabilities.Network`: every scan uploads
manifests and fetches the organisation policy, so the Socket API hosts
must be in the deployment's `AuditToolAllowedHosts` list or the run fails
loudly as infrastructure.

## Default scope

`socket scan create … .` at the worktree root — the whole tree, every
manifest Socket discovers. `vendor/`, `third_party/`, and
`node_modules/` are excluded by default: findings there describe upstream
code — noise that trains operators to ignore the auditor. Re-include a
path by overriding `ExcludePaths` in scoped config. Container-image
targets are out of scope: Socket scans manifests, not images.
