# CodeyBox: detect-secrets Secrets Auditor

Auditor plugin wrapping [detect-secrets](https://github.com/Yelp/detect-secrets)
(Yelp): it scans the audited repository's git-tracked files for leaked
credentials and reports each hit as an audit finding with the detector name
(e.g. `Secret Keyword`, `AWS Access Key`, `Private Key`) and `file:line`
location. The tool's report is its baseline document — the same auditable
`.secrets.baseline` format `detect-secrets audit` works on — carried on
stdout.

## What it reports

- One finding per `results` entry in the baseline document. `RuleId` is the
  detector's `type` string (e.g. `Base64 High Entropy String`); `Location`
  is `path:startLine` wherever the tool supplies `line_number`.
- **Secrets are never written into findings or raw output** — the baseline
  format stores `hashed_secret` (a SHA-1 of the matched value), never the
  secret itself; the finding echoes the hash so a hit can be correlated with
  an audited baseline.
- **Severity: blocking.** detect-secrets assigns no per-result severity, so
  the declared mapping sends every result — verified or not — to `Error`.
  A detected potential credential fails the audit; that is the intended
  gate. There is no advisory mode: narrow scope with `ExcludedRules` /
  `ExcludePaths`, or suppress audited false positives through `BaselineFile`.
- **Verification is off.** The scan runs `--no-verify`: detect-secrets's
  verification pass would ship each found secret to the provider's API to
  test liveness (network egress carrying credential material, out of an
  audit sandbox), and its filter would silently drop provider-rejected —
  i.e. dead but still leaked — credentials. `is_verified` is therefore
  always `false` in fresh findings.

## What it cannot see

- **Git history and untracked files.** The scan is `detect-secrets scan .` —
  worktree state of git-tracked files. A secret committed and then deleted
  leaves no trace here (the gitleaks auditor covers history), and files git
  does not track are not scanned — `--all-files` in `ExtraArguments` walks
  the filesystem instead (including untracked files and `.git` internals;
  it also disables the worktree-root precondition).
- **Non-git working directories.** Without `--all-files` the tool would scan
  *nothing* and still exit `0` with an empty `results` — a false clean pass.
  The auditor instead fails closed as infrastructure unless the working
  directory is the root of a git worktree.
- **File names git must C-quote.** detect-secrets enumerates tracked files
  via `git ls-files` and never unquotes its output, so a tracked file whose
  name contains a `"`, `\`, control byte, or byte ≥ 0x80 (e.g.
  `sëcrets.txt`) is *silently never scanned* while the run still exits `0`.
  The pre-scan probe fails closed with an infrastructure error naming the
  constraint when the listing holds any quoted entry — rename the files, or
  pass `--all-files` (filesystem walk, immune to the quoting mismatch).
- **Binary files.** detect-secrets reads text lines and skips content that
  fails UTF-8 decoding; a secret inside a `.zip`, image, or compiled blob is
  never reported.
- **Anything its detector set doesn't cover.** Findings are exactly what the
  pinned build's plugins detect. Rule ids for `IncludedRules`/`ExcludedRules`
  are the `type` strings shown in findings (e.g. `Secret Keyword`,
  `Hex High Entropy String` — *not* class names like `KeywordDetector`).
- **Secrets committed under an `ExcludePaths` prefix.** `.secrets.baseline`,
  `vendor/`, `third_party/`, and `node_modules/` are finding filters: the
  tool still scans them, but findings there are dropped — a leak committed
  under an excluded prefix never surfaces. Re-include by overriding
  `ExcludePaths`.
- **Repository-authored suppression is neutralized, not honored.** Inline
  `pragma: allowlist secret` comments are ignored by default
  (`--disable-filter detect_secrets.filters.allowlist.is_line_allowlisted`)
  because the audit subject could annotate a leaked line away. An operator
  that trusts repo-authored pragmas sets `TrustRepositorySuppression: true`.
  A committed `.secrets.baseline` is **not** loaded by `scan` (baselines
  apply only via an explicit `--baseline`), so it cannot suppress findings.

## Exit codes and failure classification

`detect-secrets scan` does **not** follow the common "1 = findings"
convention — `main()` returns `0` unconditionally, so a scan that found
secrets and a clean scan share exit `0`. The `results` object in the
baseline document is the verdict:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | scan ran; `results` is the verdict (empty or not) | pass / findings |
| `1` | unhandled exception, incl. argparse post-processing failures (e.g. unreadable `--baseline`) | infrastructure |
| `2` | argparse usage error | infrastructure |
| `126` / `127` | cannot execute / binary not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

Empty or unparseable stdout on exit `0`, an empty `results` on a non-git
tree, and a missing `--baseline` report file likewise fail closed as
infrastructure — a report the plugin cannot trust is never a pass.

## Version pinning

The auditor is pinned to **detect-secrets `1.5.0`** (`ExpectedVersion` in
scoped config). A scanner's detector set changes between releases, so the
plugin probes `detect-secrets --version` before every run and reports an
infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`:
detect-secrets ships as a Python package, not a distro package, and the pin
must hold byte-for-byte. Provision it in the sandbox baseline so the entry
point lands on PATH, e.g.:

```sh
# baseline bake step
pipx install "detect-secrets==1.5.0"      # or: pip install "detect-secrets==1.5.0"
detect-secrets --version                   # must print 1.5.0
```

via `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`. The scan invokes `git` as well; a baseline image
without git fails the worktree precondition (infrastructure, not a pass).

## Enabling

The plugin is **disabled by default** — like every plugin outside the four
grandfathered bundled ones it loads only when named in both gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.detect-secrets"],
      "Enabled": ["codeybox.detect-secrets"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.detect-secrets" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.detect-secrets`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.5.0` | Pinned detect-secrets release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `BaselineFile` | — | Absolute path (inside the sandbox) to an operator-maintained baseline file produced by `detect-secrets scan`/audit. Canonicalized per run and **rejected when it resolves inside the audited worktree** — the audit subject must not author the file that silences its own findings. The file is copied into per-run scratch before the scan because `--baseline` rewrites the file it is given; the operator's copy is never mutated. Entries the baseline marks `is_secret: false` (audited false positives) stay suppressed; everything else is reported. |
| `TrustRepositorySuppression` | `false` | When `true`, inline `pragma: allowlist secret` comments in the audited repository are honored. When `false`, the scan runs `--disable-filter detect_secrets.filters.allowlist.is_line_allowlisted` so the subject cannot annotate findings away. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact `type` strings to keep/drop (e.g. `Secret Keyword`, `AWS Access Key`). |
| `ExcludePaths` | `.secrets.baseline`, `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--all-files` (filesystem scan, disables the worktree gate), `--exclude-files <regex>`, `--base64-limit`, `--hex-limit`, `--cores`. Reserved and rejected, because they would redirect the report, load code or data files from worktree-resolvable paths, or silently reshape coverage: `--baseline` (use `BaselineFile`), `-p`/`--plugin`, `-f`/`--filter`, `--word-list`, `--gibberish-model` (file loads — install custom detectors into the baseline image instead), `-C`/`--custom-root` (retargets the scan root while the worktree gate probes the working directory), `--only-allowlisted` (repo-authored pragmas alone would decide findings). argparse's unambiguous-prefix abbreviations of a reserved flag (e.g. `--bas`, `--plug`) are rejected too. |
| `TimeoutSeconds` | `300` | Per-run bound enforced by the host around the process. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation and an oversized report fails closed rather than parsing a clipped document. |

**The audited-repository config file.** detect-secrets's conventional
in-repo config is `.secrets.baseline` at the repo root. In `scan` mode it
is inert — the tool loads a baseline only when handed one via `--baseline`,
which this auditor accepts solely through the canonicalized, outside-worktree
`BaselineFile` knob. The committed file is nevertheless dropped from findings
by default (`ExcludePaths`), because its `hashed_secret` hex entries re-trip
the entropy detectors and bury the gate in noise. Operators who want it
scanned anyway can remove the default entry.

## Default scope

Findings under `vendor/`, `third_party/`, and `node_modules/` describe
upstream code, not the change under audit; `.secrets.baseline` is the tool's
own suppression artifact whose `hashed_secret` values self-flag as hex
entropy. All four are excluded by default — the noise would teach operators
to ignore the auditor — and all four are finding filters, so a leak planted
inside them is dropped with everything else there. Re-include by overriding
`ExcludePaths`.
