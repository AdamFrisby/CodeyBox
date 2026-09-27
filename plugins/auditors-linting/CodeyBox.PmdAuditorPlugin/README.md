# CodeyBox: PMD Java/Multi-Language Analyser

Auditor plugin wrapping [PMD](https://pmd-code.org/) 7: it analyses the audited
repository with `pmd check --dir . --format xml --rulesets
rulesets/java/quickstart.xml --relativize-paths-with <worktree> --no-progress
--suppress-marker <token> --show-suppressed` and reports each rule violation as
an audit finding
with the PMD rule id (e.g. `UnusedLocalVariable`, `EmptyCatchBlock`) and
`file:line` location. PMD is multi-language — Java, Kotlin, Scala, Apex,
JavaScript, PLSQL, XML, Velocity, and more — but the ruleset decides which
languages are checked; the default is PMD's curated Java starter set.

## What it reports

- One finding per PMD rule violation. The title carries the rule id and the
  first line of the violation message; the description carries the tool, rule,
  tool-reported priority, location, and the full message (with the ruleset name
  and the rule's `externalInfoUrl` reference). `Location` is `path:startLine` —
  the scan relativizes report paths against the worktree root, so locations are
  repo-relative.
- `<error>` elements (per-file processing failures such as parse errors) and
  `<configerror>` elements (rule configuration failures) are reported as
  advisory findings under the synthetic levels `processing-error` /
  `config-error`. Under the default invocation they are unreachable — a
  recoverable error exits `5` and is infrastructure before parsing — but when an
  operator passes `--no-fail-on-error` they keep partial-coverage gaps visible.
- `<suppressedviolation>` elements (violations the repository silenced via
  `@SuppressWarnings`, `// NOPMD`, or ruleset-level XPath/regex suppressors)
  are reported as advisory findings under the synthetic level
  `suppressed-violation`, carrying the file, the suppression mechanism
  (`suppressiontype`), and the violation message. The default invocation passes
  `--show-suppressed` precisely so a repo-authored suppression cannot hide a
  finding — it is downgraded to advisory, not deleted.
- **Gate behaviour: hybrid / severity-driven — not blocking on every finding.**
  PMD rule priorities 1 and 2 map to `Error` and fail the audit; priority 3 maps
  to `Warning` (advisory); priorities 4 and 5 map to `Info`. This mirrors PMD's
  own SARIF level derivation (1–2 → `error`, 3 → `warning`, 4–5 → `note`), so a
  rule keeps the same gate meaning regardless of report format.
  `MinimumSeverity` only drops findings, it never raises them.

## What it cannot see

- **Languages the ruleset does not cover.** PMD auto-detects language by file
  extension but only applies rules for languages present in the ruleset — the
  default `rulesets/java/quickstart.xml` checks `.java` sources only. For Kotlin,
  Apex, JavaScript, XML, or multi-language coverage, point `RulesetPath` (or `-R`
  in `ExtraArguments`) at a ruleset that pulls those languages' rules in.
- **Suppressed violations lose their detail.** The scan neutralizes the
  `// NOPMD` comment marker (see below), and `--show-suppressed` makes every
  remaining suppression channel — `@SuppressWarnings("PMD…")` annotations,
  ruleset-level `violationSuppressXPath`/`violationSuppressRegex` — emit a
  `<suppressedviolation>` element that surfaces as an advisory finding. What it
  cannot recover is the suppressed violation's rule id or line number: PMD's
  report carries only the file, the suppression mechanism, and the message, so
  the finding cannot tell you which rule was silenced or where in the file.
- **Type-resolution-dependent precision.** PMD runs without an auxclasspath by
  default; rules that resolve types degrade gracefully (they under-report rather
  than error). Operators can pass `--aux-classpath` in `ExtraArguments` for
  higher precision.
- **More than `MaxFindings` violations.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw output.
- **More than `MaxOutputBytesPerStream` of report.** The XML report lives on
  stdout and shares the per-stream capture cap (default 1 MiB); overruns truncate
  the stream, which fails the parse loudly as infrastructure rather than
  silently dropping findings.

## Exit codes and failure classification

PMD 7's documented convention (PMD CLI reference) — **not** the common
"0 clean / 1 findings / 2 error" table. The scan keeps the default
`--fail-on-violation` so "ran and found something" (4) is distinguishable from
"ran clean" (0); it does **not** pass `--no-fail-on-error`, so partial analysis
fails closed rather than producing a half-report.

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran, no violations | Verdict (pass) |
| `4` | Ran, at least one violation | Verdict (`Passed = false` only when a priority-1/2 rule fires) |
| `5` | At least one recoverable error — a file failed to parse or a rule threw; coverage is partial by contract | Infrastructure (`AuditUnavailableException`) |
| `1` | PMD exited with an exception | Infrastructure |
| `2` | Usage error — invalid or missing CLI parameters | Infrastructure |
| `126` / `127` | Binary not executable or not found (includes a missing `java` runtime for the launcher) | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `pmd` (or the `java` runtime it execs) is always an infrastructure
failure naming the tool — never a passing audit.

Operators who deliberately accept partial analysis pass `--no-fail-on-error` in
`ExtraArguments`: recoverable errors then exit `0`/`4` and surface as advisory
`processing-error` findings parsed from the report's `<error>` elements.

## Version pinning

The auditor is pinned to **PMD `7.26.0`** (`ExpectedVersion` in scoped config). A
scanner's rule implementations change between releases, so an unpinned tool
would change findings under you: the auditor probes `pmd --version` before every
run and reports an infrastructure failure on any other version. The banner
carries two version tokens (`PMD 7.26.0 (…)` and `Java version: …`); the pin
extracts the `PMD`-anchored one, so the JRE version can never satisfy it.

## Provisioning

The plugin declares two tool requirements, installed/verified by baseline
provisioning **only when the plugin is enabled**:

- `pmd` — **no apt package**: distros ship PMD 6, whose CLI predates the `check`
  subcommand. Provision the versioned `pmd-dist-<version>-bin.zip` from
  <https://github.com/pmd/pmd/releases> (verify the checksum) and put its `bin/`
  on PATH via `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`.
- `java` — apt package `default-jre-headless` (any Java 8+ runtime works;
  `openjdk-17-jre-headless` or newer is equally fine).

## Enabling

The plugin is **disabled by default** — it loads only when named in both gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.pmd"],
      "Enabled": ["codeybox.pmd"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.pmd" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.pmd`, resolved per run (hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `7.26.0` | Pinned PMD release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `RulesetPath` | `rulesets/java/quickstart.xml` | `-R`/`--rulesets` value: a built-in resource path (e.g. `rulesets/java/errorprone.xml`, `rulesets/ecmascript/basic.xml`) or an operator-owned file path/URL. **Do not point this into the audited repository** — a repo-authored ruleset lets the subject redefine the gate. Ignored when `ExtraArguments` supplies `-R`/`--rulesets`. |
| `TrustRepositorySuppression` | `false` | When `true`, no `--suppress-marker` override is passed (PMD's default `NOPMD` comment marker is honored) and `--show-suppressed` is dropped (repository-silenced violations leave no advisory trace). Default keeps comment markers inert and surfaces every suppressed violation as an advisory finding. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact PMD rule ids to keep/drop (e.g. `UnusedLocalVariable`, `EmptyCatchBlock`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `external/`, `node_modules/`, `target/`, `build/`, `out/`, `dist/`, `generated/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; report paths are worktree-relative so these prefixes match. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--no-fail-on-error`, `--aux-classpath <cp>`, `--threads <n>`, `--encoding <charset>`, `--use-version java-17`, an operator `-R`/`--rulesets` (replaces `RulesetPath`/default), `--dir`/`--file-list`/`--uri` (replaces the default whole-tree scan), `--suppress-marker` (replaces the neutralised marker), or a custom `--format` (changes the report shape the parser expects — the run fails closed as infrastructure) or `--report-file` (redirects the report off stdout — same effect). |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. Raise toward the 3600 s ceiling for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a truncated XML report fails the parse as infrastructure — raise the cap rather than accepting partial findings). |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and PMD lets source files suppress the analyser with
`// NOPMD` comments or `@SuppressWarnings("PMD…")` annotations. The scan passes
`--suppress-marker` set to an unguessable token generated fresh for each run —
a fixed token would be forgeable, since the audit subject can read this
plugin's source — so those comments are inert and findings surface for code
the comments would have suppressed. Expect *more* findings than a stock
`pmd check` run on repos that rely on suppression comments; that is the gate
working as intended. The annotation channel cannot be switched off (PMD offers
no flag for it), so the scan also passes `--show-suppressed`: every violation
the repository silenced — by annotation, marker, or ruleset suppressor —
surfaces as an advisory `suppressed-violation` finding instead of
disappearing. Set `TrustRepositorySuppression` to restore PMD's default marker
and drop `--show-suppressed`.

## Default scope

`pmd check --dir .` collects the whole worktree; PMD's language detection skips
anything the active rulesets do not cover, so effective scope is "the ruleset's
languages" — Java sources with the default `rulesets/java/quickstart.xml`. The
curated quickstart set is the documented starting point precisely because its
findings are high-signal; operators who want deeper coverage (errorprone,
security, codestyle categories, or non-Java languages) pin a ruleset via
`RulesetPath`. On top of that, the finding-level `ExcludePaths` backstop drops
findings under vendored (`vendor/`, `third_party/`, `external/`,
`node_modules/`) and generated (`target/`, `build/`, `out/`, `dist/`,
`generated/`, `coverage/`) prefixes: diagnostics there belong to upstream
packages or build output, not the change under audit — reporting them trains
operators to ignore the auditor. Re-include a path by overriding `ExcludePaths`;
narrow the scan itself with `--exclude`/`--exclude-file-list` or `--dir` in
`ExtraArguments` (which also saves scan time — the finding-level filter does
not). The scan writes nothing into the audited tree (no `--report-file`, no
`--cache`).
