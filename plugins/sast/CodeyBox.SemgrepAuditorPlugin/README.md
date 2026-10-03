# CodeyBox: Semgrep Structural SAST Auditor

Auditor plugin wrapping the [Semgrep CLI](https://semgrep.dev) (`semgrep`):
it runs Semgrep's structural pattern-matching rules over the audited
repository (`semgrep scan --sarif --error`), reporting each match as an audit
finding with the rule id (e.g. `python.lang.security.audit.dangerous-subprocess-use`)
and `file:line` location.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, missing or invalid rules,
fatal error) is infrastructure — never a pass.

## What it reports

- One finding per Semgrep match. The title carries the rule id and the
  first line of the match message; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Semgrep severities go through a declared map, never raw:
  `ERROR` → `Error` (fails the audit); `WARNING` → `Warning` (advisory);
  `INFO` → `Info` (informational); anything unrecognised → `Warning`.
  Semgrep's SARIF carries no `level` on individual results — the rule's
  severity surfaces once per run in
  `tool.driver.rules[].defaultConfiguration.level` (`error`/`warning`/`note`),
  which the shared rule-metadata parser resolves onto each result before the
  declared map applies. `MinimumSeverity` can only drop findings, it never
  raises them. A rule's severity is set by the ruleset author, so the gate
  blocks exactly the rules the chosen ruleset marks `ERROR`.
- SARIF may embed code snippets around each location, so findings and raw
  output can contain the reported source context. That is the point of a
  SAST finding; treat audit reports accordingly.
- Rule ids are reported verbatim (`--no-rewrite-rule-ids`): the `id:` an
  author writes in their YAML is what `IncludedRules`/`ExcludedRules` and
  findings match — not Semgrep's default directory-derived rewrites.

## What it cannot see

- **Rules it was not given.** Semgrep ships no embedded rules — a scan
  checks exactly what the resolved ruleset contains. A clean run proves the
  configured rules found nothing; it is not a clean bill of health. Choose
  the ruleset deliberately.
- **Anything Semgrep cannot parse.** A source file that fails to parse is
  skipped by that file's rules (Semgrep reports the parse error on stderr
  and still exits `0`; only `--strict` makes it an exit code, and it stays
  off by default). Partial parse coverage is a normal Semgrep limitation —
  treat it as reduced coverage, not a defect of the run.
- **Suppressed matches, by default.** The scan passes `--disable-nosem` and
  `--x-ignore-semgrepignore-files`, so `nosemgrep` comments and
  `.semgrepignore` files authored in the audited repository are inert at
  every depth. The latter is a Semgrep-internal `--x-` flag: the pinned
  version keeps it honest — if a provisioned release drops it the scan
  fails loudly as infrastructure rather than silently re-honoring
  suppression files. Operators who deliberately trust repo-authored
  suppression set `TrustRepositorySuppression` in scoped config.
- **Registry rulesets without network.** `--config p/...` / `--config auto`
  pull rules from the Semgrep Registry and need network; the auditor runs
  with `AuditCapabilities.None` (no network, no agent credentials), so
  registry sources fail closed as infrastructure. Provision local rules —
  a repository `.semgrep/` or an operator-mounted path — instead.
- **Pro/interfile analysis.** The scan passes `--oss-only`: Pro Engine
  cross-file analysis needs a login the audit sandbox does not carry, and
  pinning the OSS engine keeps findings deterministic across baselines.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  Semgrep still scans those files (minus what gitignore excludes), but
  findings under `vendor/`, `third_party/`, `node_modules/` are dropped.
  Override `ExcludePaths` to re-include them.

## Ruleset resolution

Semgrep exits `7` with no configuration, so the auditor resolves rules in
order:

1. **`Config` in scoped config** — a comma-separated list where each entry
   becomes its own `--config` (Semgrep merges sources). A path outside the
   audited worktree (mounted by the sandbox baseline) is the way to run a
   ruleset the audit subject cannot edit.
2. **Repository convention** — when `Config` is unset the auditor probes the
   worktree root each run for `.semgrep` (directory or file),
   `.semgrep.yml`, and `.semgrep.yaml`, and passes every present one as its
   own `--config`. This is the config file the tool expects in the
   repository under audit; note the rules then come from the audit subject
   itself — use operator `Config` where the ruleset must not be
   subject-authored.

Neither resolving is a **deterministic infrastructure failure** naming both
options — Semgrep without rules is a scanner that did not run, never a pass.
A `.semgrep/` directory that contains no valid rules fails the same way at
scan time (exit `7`).

## Exit codes and failure classification

Semgrep's convention (from its `EXIT STATUS` documentation — not the
gitleaks/eslint default: without `--error` a scan exits `0` even with
findings). The auditor always passes `--error`, making `1` the dedicated
"ran and found problems" exit:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed, no findings | Verdict (`Passed = true`) |
| `1` | Scan completed, findings reported (with `--error`) | Verdict — findings from the SARIF document (`Passed = false` when any finding maps to `Error`) |
| `2` | Fatal error — Semgrep failed to run | Infrastructure (`AuditUnavailableException`) |
| `3` | Invalid target code (only under `--strict`, which stays off) | Infrastructure |
| `4` | Invalid pattern in a rule | Infrastructure |
| `5` | Configuration is not valid YAML | Infrastructure |
| `7` | Missing or invalid configuration | Infrastructure |
| `8` | Unknown language | Infrastructure |
| `13` | Invalid API key | Infrastructure |
| `99` | Not implemented in osemgrep | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `semgrep` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **Semgrep `1.177.0`** (`ExpectedVersion` in scoped
config). A scanner's rules and output shape change between releases, so an
unpinned tool would change findings under you: the auditor probes
`semgrep --version` before every run (the CLI prints the bare `X.Y.Z`) and
reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: Semgrep
ships as a Python package, not a distro package, and only a pinned install
carries the version this auditor was verified against. Provision the pinned
release in your sandbox baseline:

```sh
# baseline bake step (needs Python 3.10+ on the image)
SEMGREP_VERSION=1.177.0
pipx install "semgrep==${SEMGREP_VERSION}"    # or: pip install "semgrep==${SEMGREP_VERSION}"
semgrep --version                             # must print 1.177.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.semgrep"],
      "Enabled": ["codeybox.semgrep"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.semgrep" }
```

The `semgrep` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.semgrep`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.177.0` | Pinned Semgrep release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Config` | repository `.semgrep*` | Comma-separated `--config` sources (rules file/dir paths — including paths outside the worktree — or registry names needing network). Up to 64 entries. Replaces repository probing entirely. |
| `TrustRepositorySuppression` | `false` | When `true`, omits `--disable-nosem` and `--x-ignore-semgrepignore-files` so repo-authored `nosemgrep` comments and `.semgrepignore` files take effect. Default keeps the audit subject from silencing the scan. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (e.g. `python.lang.security.audit.dangerous-subprocess-use`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `semgrep scan` after the built-in args (never via a shell). Never pass `-o`/`--output`, `--json`/`--gitlab-sast`/`--emacs`/`--vim`/`--junit-xml` (would replace or divert the SARIF report the parser expects). `--severity=ERROR`, `--include`, `--exclude`, or extra `--config` entries are reasonable additions. |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. Semgrep additionally bounds each rule per file (`--timeout` 5s, `--timeout-threshold` 3 by default); raise `TimeoutSeconds` for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: matches reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore
the auditor. The exclusion is a finding filter layered on Semgrep's own
gitignore-aware target selection. Re-include them by overriding
`ExcludePaths`.
