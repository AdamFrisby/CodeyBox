# CodeyBox: SpotBugs Java Bytecode Analyser

Auditor plugin wrapping [SpotBugs](https://spotbugs.github.io/): it analyses
the audited repository with `spotbugs -textui -sarif=/dev/stdout -exitcode
-noClassOk -quiet -medium -effort:default .` and reports each bug instance as
an audit finding with the SpotBugs bug pattern (e.g.
`NP_NULL_ON_SOME_PATH`, `DMI_RANDOM_USED_ONLY_ONCE`, `UWF_UNWRITTEN_FIELD`)
and `file:line` location. Java bytecode analysis only.

## What it reports

- One finding per SpotBugs bug instance. The title carries the bug pattern
  and the first line of the message (e.g. "NP_NULL_ON_SOME_PATH: Possible
  null pointer dereference"); the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine` — the source file SpotBugs resolved from the class debug
  info, so findings read in source terms even though the analysis ran on
  bytecode.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** SpotBugs' SARIF levels go through a declared map, never raw:
  `error` (scariest/scary bug ranks) → `Error` and fails the audit;
  `warning` (troubling) → `Warning` (advisory); `note` (of concern) and
  `none` → `Info`. The declared map covers exactly the level vocabulary the
  SpotBugs SARIF reporter emits (`warning`, `error`, `note`, `none` — the
  `@SerializedName` values of its `Level` enum in 4.10.4). `MinimumSeverity`
  only drops findings, it never raises them.

## What it cannot see

- **Uncompiled sources.** SpotBugs analyses `.class` files and `.jar`
  archives, not `.java` files. A source-only tree (no bytecode checked in
  and none built in the sandbox) is a clean pass — the scan passes
  `-noClassOk`, which emits an empty SARIF report instead of failing — not
  an error. Provision a compile step (`mvn package`, `gradle build`) into
  the audit sandbox baseline when the subject is sources; without it the
  auditor is a no-op pass.
- **Suppressed instances.** `@SuppressFBWarnings` annotations authored in
  the audited tree are honored by the tool with no command-line switch to
  turn them off — findings the subject annotated still surface for triage
  against the checked-in source, but the annotation text is the subject's
  own claim, not the auditor's. Repo-local `-exclude` filter files are never
  auto-read: only the operator-owned `FilterFilePath` (outside the audited
  tree) names one. Pass `-exclude <path>` in `ExtraArguments` to
  deliberately trust repo-authored filters.
- **Configuration the repository does not declare.** SpotBugs reads no
  config file from the repository — confidence (`-medium`), effort
  (`-effort:default`), and the exclusion posture below are operator-owned.
  The larger precision surface is the classpath: analysis without the
  project's dependencies reports `MISSING_CLASS_FLAG` exits (see below)
  rather than degraded findings. Operators pass dependency jars via
  `-auxclasspath` in `ExtraArguments`.
- **More than `MaxFindings` instances.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.
- **More than `MaxOutputBytesPerStream` of report.** The SARIF report is
  streamed on stdout and shares the per-stream capture cap (default 1 MiB);
  overruns truncate the stream, which fails the parse loudly as
  infrastructure rather than silently dropping findings.

## Exit codes and failure classification

SpotBugs' convention (read from the 4.10.4 source — **not** assumed from the
common "0 clean / 1 findings / 2 error" table). `FindBugs.runMain` only
calls `System.exit` when `-exitcode` was passed — without it the process
exits `0` even with bugs reported, which is why the scan passes `-exitcode`
explicitly. With it, the exit is the `ExitCodes` bit set (`1` = bugs found,
`2` = missing classes, `4` = analysis errors).

| Exit | Meaning | Classification |
|---|---|---|
| `0` with SARIF report | Analysed clean (empty `results`) | Verdict (pass) |
| `1` with SARIF report | Analysed, bugs found | Verdict (`Passed = false` only when an `error`-level instance is present — `warning`/`note` findings are advisory) |
| `0`/`1` with no SARIF on stdout | Could not produce a report (e.g. an operator `--output-format`-style override is not applicable here — overriding `-sarif` redirects the report away from stdout) | Infrastructure (`AuditUnavailableException`) |
| `2` / `3` | Ran, but classes needed for analysis were missing (`MISSING_CLASS_FLAG`) — degraded analysis, not a verdict | Infrastructure (`AuditUnavailableException`) |
| `4`–`7` | Serious analysis errors (`ERROR_FLAG`), incl. usage failures | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `spotbugs` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **SpotBugs `4.10.4`** (`ExpectedVersion` in scoped
config). A scanner's detectors change between releases, so an unpinned tool
would change findings under you: the auditor probes `spotbugs -version`
(single dash — the spelling the tool registers; it prints `SpotBugs X.Y.Z`
to stdout) before every run and reports an infrastructure failure on any
other version.

The tool requirement declares **`AptPackage: spotbugs`** — the host installs
it into the sandbox baseline via `apt-get` **only when this plugin is
enabled**, and verifies its presence at bake time. The apt package tracks the
distro release; if your baseline's distro ships a different SpotBugs, set
`ExpectedVersion` to the provisioned release instead of fighting the package
manager — or stage the upstream release archive (which also needs a Java
runtime in the baseline) via `MultipassExtraRuncmd` / `Incus:ExtraRuncmd` or
`ExecutableProvisions`.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs and verifies `spotbugs` only in
that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.spotbugs"],
      "Enabled": ["codeybox.spotbugs"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.spotbugs" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.spotbugs`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `4.10.4` | Pinned SpotBugs release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfidenceLevel` | `medium` | Tool-native reporting threshold: `high` (`-high`: high-confidence only), `medium` (`-medium`: medium and high — the tool default), `low` (`-low`: everything). Unknown values fail closed as infrastructure. Ignored when `ExtraArguments` already supplies `-low`, `-medium`, or `-high`. |
| `EffortLevel` | `default` | Analysis effort passed as `-effort:<level>` (`min`, `less`, `default`, `more`, `max`): higher effort finds more bugs at higher memory/time cost. Unknown values fail closed as infrastructure. Ignored when `ExtraArguments` supplies `-effort` in any form (`-effort`, `-effort=<level>`, `-effort:<level>`). |
| `FilterFilePath` | `null` | Path passed to `-exclude` — an operator-owned SpotBugs filter file **outside** the audited repository. Ignored when `ExtraArguments` already supplies `-exclude`. Never points into the repo: a repo-authored file would let the subject silence the gate. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Useful as `warning` to silence `note`-level (of-concern) noise. |
| `IncludedRules` / `ExcludedRules` | — | Exact SpotBugs bug patterns to keep/drop (e.g. `NP_NULL_ON_SOME_PATH`, `RV_RETURN_VALUE_IGNORED_BAD_PRACTICE`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `external/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `-auxclasspath <deps>` (analyse with the project's dependencies on the classpath), `-onlyAnalyze <pkg>.*` (narrow the scan and save time), `-effort:max`, or `-exclude <repo-filter>` (deliberately trusting repo-authored filters). Overriding `-sarif` redirects the report away from stdout and breaks the run into infrastructure failure; overriding `-exitcode` changes the exit convention the auditor classifies on — any non-`0`/`1` verdict exit becomes infrastructure. |
| `TimeoutSeconds` | `600` | Per-run bound. Exceeding it is infrastructure, not a pass. Whole-tree bytecode analysis routinely takes minutes; raise toward the 3600 s ceiling for large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a truncated SARIF report fails the parse as infrastructure — raise the cap rather than accepting partial findings). |

## Default scope

`spotbugs … .` — every class file and archive under the worktree is
analysed, including nested jars (the tool default: shaded dependencies are
not a blind spot); that is the project's own declaration of analysable
scope. The scan writes nothing into the audited tree (the SARIF report
streams to `/dev/stdout`, never to a file). On top of that, the
finding-level `ExcludePaths` backstop lists vendored prefixes (`vendor/`,
`third_party/`, `external/`, `node_modules/`): instances there belong to
upstream packages, not the change under audit — reporting them produces
noise that trains operators to ignore the auditor.

Build-output directories (`target/`, `build/`, `out/`) are deliberately
**not** excluded, unlike in the source-linter auditors: a bytecode auditor's
subject *is* the compiled output, and excluding it would blind the tool.
Re-include a vendored path by overriding `ExcludePaths`, or narrow the scan
itself with `-onlyAnalyze` in `ExtraArguments` (which also saves scan time
on large vendored trees — the finding-level filter does not).
