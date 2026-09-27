# CodeyBox: detekt Kotlin Analyser

Auditor plugin wrapping [detekt](https://detekt.dev): it analyses the audited
repository with `detekt --input . --report sarif:/dev/stderr` and reports each
finding as an audit finding with the namespaced rule id (e.g.
`detekt.style.MagicNumber`, `detekt.potential-bugs.UnsafeCallOnNullableType`)
and `file:line` location. Kotlin source analysis only.

## What it reports

- One finding per detekt issue. The title carries the rule id and the first
  line of the message; the description carries the tool, rule, tool-reported
  SARIF level, location, and the full message. `Location` is
  `path:startLine` — detekt reports repository-relative artifact URIs when the
  file is under the base path.
- **Gate behaviour: blocking on findings by default — the tool's own
  semantics.** detekt's default severity for every rule is `error` (per the
  detekt docs), and its own CLI gate fails the build on any issue. The
  declared map sends SARIF `error` → `Error` (fails the audit), `warning` →
  `Warning` (advisory), `note`/`none`/`info` → `Info`. Unrecognised levels
  map to `Warning`. Raw tool levels never reach findings. To make findings
  advisory, declare `severity: warning`/`info` per rule or ruleset in the
  detekt config (the tool's designed mechanism), raise `MinimumSeverity`, or
  use `ExcludedRules` — `MinimumSeverity` only drops findings, it never
  raises them.

## What it cannot see

- **Non-Kotlin sources.** detekt analyses `.kt`/`.kts` files. A tree with no
  Kotlin sources is a clean pass — nothing to check is not an error.
- **Type-resolution rules.** The CLI runs light analysis (no classpath), so
  rules annotated `@RequiresTypeResolution` do not run. Operators pass
  `--classpath`, `--jvm-target`, `--language-version` via `ExtraArguments` for
  deeper analysis.
- **Baselines and suppressions the repository authors.** `--baseline` is
  never passed, so a checked-in `detekt-baseline.xml` is ignored (operators
  can deliberately pass one via `ExtraArguments`). detekt's own `@Suppress`
  annotations and config `excludes`/`includes` patterns are honored only when
  the repository config is trusted (below).
- **More than `MaxFindings`/`MaxOutputBytesPerStream`.** Findings beyond
  `MaxFindings` (default 1000) are dropped and reported as truncated; the
  SARIF report shares the per-stream capture cap (default 1 MiB) and an
  overrun fails the parse as infrastructure rather than silently dropping
  findings.

## Configuration the auditor reads

| Key | Effect |
|---|---|
| `ExpectedVersion` | Pinned detekt release (default `1.23.8`). The auditor probes `detekt --version` before every scan; a missing binary, unrecognised version, or mismatch is an infrastructure failure naming `detekt`. |
| `ConfigPath` | Operator-owned detekt config passed as `--config`. Should live outside the audited tree. When set it is the only config passed. |
| `TrustRepositoryConfig` | Default `true`: the first present of `detekt.yml`, `detekt.yaml`, `config/detekt/detekt.yml`, `config/detekt/detekt.yaml` is passed as `--config` (the CLI does not auto-discover it). `false` = the repo's config is never loaded. |
| `MinimumSeverity`, `IncludedRules`, `ExcludedRules`, `ExcludePaths`, `ExtraArguments`, `TimeoutSeconds`, `MaxOutputBytesPerStream`, `MaxFindings` | Shared per-auditor knobs (`ExternalToolAuditorOptions`). Rule ids are detekt-namespaced (`detekt.<ruleset>.<rule>`). |

**Trust warning:** the repo detekt config is executable configuration — it
can deactivate rules, lower severities, or exclude paths. Trusting it
(default) lints against the project's own contract; set
`TrustRepositoryConfig=false` for a fully operator-owned run.

## Exit codes and failure classification

detekt's convention (read from the 1.23.8 `AnalysisResult.exitCode()` source —
**not** the common "0 clean / 1 findings / 2 error" table; detekt inverts 1
and 2):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran; no issues, or issues under a configured `maxIssues` threshold (findings still in the report) | Verdict |
| `2` | Ran; `IssuesFound` (issue count over `build.maxIssues`, default 0) | Verdict — `Passed = false` only when an `error`-level finding survives mapping |
| `1` | `UnexpectedError` — crashes and CLI argument violations | Infrastructure (`AuditUnavailableException`) |
| `3` | `InvalidConfig` — unreadable/invalid `--config` | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |
| `0`/`2` with no SARIF on stderr | Report missing or polluted | Infrastructure |

A missing `detekt` is always an infrastructure failure naming the tool —
never a passing audit.

## Default scope and exclusions

Whole-tree scan (`--input .`); findings under `vendor/`, `third_party/`,
`node_modules/`, `dist/`, `build/`, `out/`, `coverage/` are dropped by the
default `ExcludePaths` — vendored and generated Kotlin is not the change
under audit. `buildSrc/` is deliberately **not** excluded: it is authored
build logic, not generated output. The scan writes nothing into the audited
tree.

## Provisioning

The `detekt` requirement is declared via `[CodeyBoxPluginRequiresTool]`, so
baseline provisioning installs it **only when this plugin is enabled** and the
plugin defaults to disabled. There is no apt package carrying a pinned detekt:
provision a Java runtime plus the `detekt-cli` distribution (the `bin/detekt`
script) from the GitHub releases page via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`, and set `ExpectedVersion` if the provisioned build
differs from the default pin.
