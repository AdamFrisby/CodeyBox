# CodeyBox: ast-grep Structural Pattern Auditor

Auditor plugin wrapping [ast-grep](https://ast-grep.github.io): it scans the
audited repository with ast-grep's structural AST pattern rules (the
`scan` subcommand, `--json=compact` report) and reports each diagnostic as
an audit finding.

## What it reports

- One finding per rule diagnostic: the title carries the rule id
  (`ruleId`) and the first line of the rule message; the description
  carries the tool, rule id, tool severity, location, and the rule's
  `message` plus its `note` when set.
- `Location` is `file:line` — the path ast-grep reports relative to the
  scan root and the 1-based start line (ast-grep's JSON positions are
  0-based; the auditor converts them).
- Two built-in rule ids are emitted in addition to repo/operator rules:
  `unused-suppression` (a stale `ast-grep-ignore` directive that suppresses
  nothing — ast-grep reports it at `hint` on full-project scans) and
  `no-suppress-all` (see below). One synthesized rule id is added by the
  auditor itself: `ast-grep/suppression-site` marks each file carrying an
  `ast-grep-ignore` directive (see below).
- **Severity: severity-driven, not blocking on every finding.** The
  declared mapping is `error → Error`, `warning → Warning`,
  `info`/`hint`/`off → Info`, unknown → Warning. Only `error` diagnostics
  fail the audit — the same semantics ast-grep encodes in its exit code.
  ast-grep's default rule severity is `hint`, so rules written without an
  explicit severity are advisory until the ruleset or the operator raises
  them. To make every match blocking, set `severity: error` in the rules or
  add a bare `--error` to `ExtraArguments`.

## Repository-authored suppression

The audit subject can write `ast-grep-ignore` comments into source files,
and ast-grep has **no flag to disable suppression outright**. The auditor
makes every suppression surface visible instead:

- **Bare `ast-grep-ignore`** (suppress every rule on the node): the auditor
  passes `--error=no-suppress-all`, so each one surfaces as an `Error`
  finding and fails the audit. Downgrade via
  `--warning=no-suppress-all` or `--off=no-suppress-all` in
  `ExtraArguments` (ast-grep resolves the severity flags in a fixed
  `error → warning → info → hint → off` order, so a later flag *kind* wins
  regardless of argv position).
- **Rule-scoped `ast-grep-ignore: rule-id`**: still honored by ast-grep,
  but no longer invisible — a bounded pre-scan `grep` sweep over the scan
  targets emits a `Warning` finding (rule id `ast-grep/suppression-site`)
  per file containing the marker, so a clean report cannot silently mean
  "suppressed".

Setting `TrustRepositorySuppression` to true opts back in to
repository-authored suppression wholesale: it drops the `--no-ignore`
flags, the `no-suppress-all` escalation, and the marker sweep.

## Repository config and dynamic languages

`scan` needs a ruleset, and by default it comes from the audited
repository: ast-grep auto-loads `sgconfig.yml` at the worktree root. That
file is more than rules — a `customLanguages`/`libraryPath` entry makes
ast-grep **dlopen a repository-pathed native library into the scanner
process**: code execution the audit subject controls, which could also
write a clean report and forge a pass.

By default the auditor fails closed as deterministic infrastructure when
the repo-root `sgconfig.yml` declares those keys. The opt-ins, in order of
preference:

- pin an operator-owned `sgconfig.yml` via `ConfigFile` (`-c` replaces the
  repository's config entirely — the gate then does not run);
- set `TrustRepositoryCustomLanguages` to true when the repository's
  sgconfig is already trusted to execute code inside the audit sandbox.

A repo `sgconfig.yml` without dynamic loading is used as-is — its rules,
severities, and `util` definitions are the project's own structural-lint
contract.

## Walker coverage

The audit subject also owns `.gitignore`, `.ignore`, parent-directory and
global ignore files, and dot-directories — all of which shrink what
ast-grep walks. A repo `.ignore` containing `src/**`, or code hidden under
`.config/`, would otherwise produce an empty clean report. The auditor
therefore passes `--no-ignore` for all six classes (`hidden`, `dot`,
`exclude`, `global`, `parent`, `vcs`) by default: ignored and hidden code
is scanned like everything else. `TrustRepositorySuppression` opts out.

Because nothing is then keeping gitignored build output and vendored trees
out of the crawl, each directory-prefix `ExcludePaths` entry (trailing `/`)
is also emitted as a `--globs` exclusion (`vendor/` → `!vendor/**`), so
those trees are never walked. Exact-path entries and entries containing
glob metacharacters keep their findings-level filtering but are not widened
into a glob.

## What it cannot see

- **Semantics, not patterns.** ast-grep matches tree-sitter syntax
  structure; it cannot follow dataflow, resolve symbols, or reason about
  types. A rule only reports the shapes it was written to find.
- **Repositories with no ruleset.** `scan` requires `sgconfig.yml` +
  `ruleDirs` in the worktree (or `ConfigFile`/`RuleFile` from scoped
  config). With none, ast-grep exits `3` and the audit fails as
  infrastructure — loudly, never a pass. An empty `ruleDirs` is a
  legitimate (empty) ruleset and passes with no findings.
- **Languages ast-grep does not know.** Files whose extension maps to no
  configured rule's language are not parsed.
- **What a rule-scoped suppression removed.** The marker sweep shows
  *which files* carry `ast-grep-ignore` directives; it cannot reconstruct
  the diagnostics those directives suppressed — review the flagged files'
  diffs for that.
- **Rules the operator did not write.** There is no built-in ruleset —
  "SAST coverage" is exactly what the configured rules define.

## Exit codes and failure classification

ast-grep does **not** follow the common "0 = clean, 1 = findings,
2 = could not run" convention — verified against the 0.45.3 source
(`ErrorContext::exit_code`, `DiagnosticError`):

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON array (possibly empty) | ran; no error-severity diagnostics | pass / findings |
| `1` | JSON array | ran; ≥1 `error` diagnostic | findings |
| `1` | none / not JSON | unclassified anyhow failure (`Error: …` on stderr) | infrastructure |
| `2` | — | clap usage error | infrastructure |
| `3` | — | no project config and no `--rule`/`--inline-rules` | infrastructure |
| `4`–`10`, `17`, `22`, `33`, `79` | — | typed failures (config read/parse, glob, language, …) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `ast-grep` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit. Note the deliberate asymmetry: a **nonexistent scan target**
is ast-grep's silent-pass trap (`ERROR: …` on stderr, empty report, exit
`0`), so configured `Targets` are probed for existence first and a missing
one fails deterministically; a **symlinked** target fails the same way,
because ast-grep follows a symlinked scan root even without `--follow`.

## Version pinning

The auditor is pinned to **ast-grep `0.45.3`** (`ExpectedVersion` in scoped
config): rule evaluation, severity semantics, and the JSON report shape
change between releases, so an unpinned binary would change findings under
you. `ast-grep --version` is probed before every run.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries ast-grep. Provision the pinned release into the
sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (needs Node.js on the image)
npm install -g @ast-grep/cli@0.45.3
ast-grep --version   # must print the pinned version
```

or the versioned upstream GitHub release binary — verify it against the
published checksums before unpacking, since the scanner's report is the
gate evidence:

```sh
curl -fLO https://github.com/ast-grep/ast-grep/releases/download/0.45.3/app-x86_64-unknown-linux-gnu.zip
curl -fLO https://github.com/ast-grep/ast-grep/releases/download/0.45.3/checksums.txt
grep 'app-x86_64-unknown-linux-gnu.zip' checksums.txt | sha256sum -c -
unzip app-x86_64-unknown-linux-gnu.zip   # installs ast-grep; the bundled sg alias is deprecated upstream
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.ast-grep"],
      "Enabled": ["codeybox.ast-grep"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.ast-grep" }
```

Only then does the declared `ast-grep` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.ast-grep`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.45.3` | Pinned ast-grep release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `sgconfig.yml`) | `-c/--config` project config path. Pin an operator-owned sgconfig provisioned into the baseline instead of trusting the audited repository's — also replaces the repo config entirely, so the `customLanguages` gate does not run. Conflicts with `RuleFile`. |
| `RuleFile` | — | `-r/--rule` single rule file. Conflicts with `ConfigFile`. Note it does not exempt the `customLanguages` gate: ast-grep still discovers a repo-root `sgconfig.yml` to register languages. |
| `Targets` | `.` | Comma-separated repository-relative files/folders scanned positionally. Must exist in the worktree — a missing target is a deterministic infrastructure failure (ast-grep would otherwise report it as an empty pass) — and must not be symlinks, which would redirect the scan root outside the worktree. |
| `TrustRepositorySuppression` | `false` | Trust repository-authored suppression surfaces: drops the `--no-ignore` flags, the `no-suppress-all` escalation, and the suppression-marker sweep. |
| `TrustRepositoryCustomLanguages` | `false` | Consent to a repo-root `sgconfig.yml` declaring `customLanguages`/`libraryPath` — repository-controlled native code executed inside the scanner, which can also forge the report. Prefer `ConfigFile` instead. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — repo rule ids plus `no-suppress-all` / `unused-suppression` / `ast-grep/suppression-site`. |
| `ExcludePaths` | `.git/`, `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/` — and kept out of the crawl via `--globs` exclusions (prefix entries only; exact-path and glob-metacharacter entries stay findings-level). Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell): severity overrides (`--error`, `--error=<id>` — note `=` is required), `--min-severity`, `--filter`, extra paths, etc. An operator-supplied `--config`/`-c` outranks `ConfigFile` and marks the project config operator-pinned; `--rule`/`-r` outranks `RuleFile`. `--no-ignore` arguments are additive to the built-in set — honor repository ignore files via `TrustRepositorySuppression` instead. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

The audited repository supplies its own ruleset through `sgconfig.yml` +
rule directories — that file is the config ast-grep expects in the
repository under audit. Rules, rule severity, and `util` definitions there
are honored as the project's own structural-lint contract; `customLanguages`
is the one carve-out, gated above. Operators who need a fully operator-owned
ruleset pin `ConfigFile` (or `RuleFile`) to a baseline-provisioned path.

## Default scope

`ast-grep scan --json=compact .` over the whole work tree: upstream file
discovery already limits the scan to files whose extension maps to a
language the ruleset uses, and the `--no-ignore` flags keep ignored and
hidden files in the crawl so the audit subject cannot shrink it. On top of
that, `.git/`, `vendor/`, `third_party/`, `node_modules/`, `dist/`,
`build/`, `out/`, and `coverage/` are excluded from the crawl via
`--globs` and dropped from findings by default — problems there describe
upstream packages, generated output, or VCS internals, not the change under
audit, and reporting them would train operators to ignore the auditor. An
operator who wants the scan narrowed further sets `Targets` or passes
`--globs` via `ExtraArguments`.
