# CodeyBox: ApiCompat .NET API Compatibility Auditor

Auditor plugin wrapping [`apicompat`](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/global-tool)
(`Microsoft.DotNet.ApiCompat.Tool`): it compares the public API surface of .NET
assemblies (or packages) in the audited working tree against a baseline and reports
compatibility breaks as CodeyBox audit findings.

## What it reports

- One finding per `apicompat` diagnostic, emitted as `CPnnnn:` text lines on stderr
  (the tool exposes no JSON mode). The title carries the diagnostic identifier
  (e.g. `CP0001` type removed, `CP0002` member removed, `CP0017` parameter name
  changed behind its opt-in rule); the description carries the tool, rule,
  tool-reported severity, and problem message. Diagnostics name API member
  identities, never files — findings carry no `Location`.
- **Gate behaviour: blocking.** `apicompat` reports no severity per diagnostic —
  every emitted line is a public API break — so every finding maps to
  `AuditSeverity.Error` and **fails the audit** (`Passed = false`). There is no
  advisory mode; narrow scope with `IncludedRules` / `ExcludedRules`, `NoWarn`, or
  an operator-owned `SuppressionFile` instead.

## What it cannot see

- **Source that was never built.** The tool compares compiled assemblies/packages,
  not source. An API break in source that is not covered by the configured
  `LeftAssembly` / `RightAssembly` (or `Package`) inputs is invisible; keep the
  configured inputs aligned with the libraries under audit.
- **Compatible evolution.** Added types and members are API-safe and produce no
  findings in the default (non-strict) mode — only breaks do. `StrictMode`
  additionally flags additions.
- **Path exclusion.** Findings carry member identities, never file paths, so
  `ExcludePaths` has nothing to match and is inert for this auditor. Narrow with
  `IncludedRules` / `ExcludedRules` (exact diagnostic ids such as `CP0002`) or
  `NoWarn` instead.
- **Suppression tuning in the audited repo.** Compatibility suppressions come only
  from the file `SuppressionFile` points at (or `--suppression-file` in
  `ExtraArguments`). A suppression file inside the audited repository lets a change
  set soften the check applied to it; operators who need the gate independent of
  repo-authored tuning should point `SuppressionFile` at an operator-owned file
  outside the repository. Post-hoc rule filtering through `IncludedRules` /
  `ExcludedRules` is applied by the audit host, outside the subject's reach.
- **Generated suppression files.** `--generate-suppression-file` /
  `--suppression-output-file` in `ExtraArguments` are rejected: they make the scan
  write files into the audited tree. Generate suppression files out-of-band.

## Exit codes and failure classification

`apicompat`'s exit convention (verified against 10.0.401 live runs — it is **not**
the common 0/1/2 convention; exit `1` is ambiguous):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean, no breaking changes (clean-run marker on stdout) | Verdict (Pass) |
| `1` | Ran and found breaking changes (`CPnnnn:` lines on stderr) | Verdict (`Passed = false`, findings reported) |
| `1` (no `CPnnnn` lines) | Could not run (missing assembly with an `Unhandled exception: …` dump, usage error such as `Option '…' is required`) — contradicts the output contract | Infrastructure (`AuditUnavailableException`) |
| `0` (no marker) | Silent tool with no recognizable report | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |
| anything else | Unknown convention or signal termination | Infrastructure (`AuditUnavailableException`) |

## Version pinning

The auditor is pinned to **apicompat `10.0.401`** (`DefaultExpectedVersion`).
A scanner's diagnostic catalogue and report shape evolve between releases, so
an unpinned tool would change findings across runs. The auditor probes
`apicompat --version` before scanning; any version mismatch or missing binary
fails closed as infrastructure. Operators running a different pinned
build configure `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): no
distro apt package carries a pinned `apicompat`. Provision the pinned release
in your sandbox baseline from NuGet via the .NET SDK (which must be on PATH):

```sh
# Baseline provisioning bake step
dotnet tool install --global Microsoft.DotNet.ApiCompat.Tool --version 10.0.401
apicompat --version   # must print 10.0.401
```

## Enabling

The plugin is **disabled by default**. It loads only when allowlisted and
enabled:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.apicompat"],
      "Enabled": ["codeybox.apicompat"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.apicompat" }
```

When disabled, the plugin's `apicompat` tool requirement
contributes nothing to sandbox baseline provisioning — no presence probe
and no install step mention `apicompat`.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.apicompat`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `10.0.401` | Pinned apicompat release. A different installed version fails closed as infrastructure. |
| `LeftAssembly` | — (required in assembly mode) | Baseline assembly path(s), comma-separated, passed to `-l/--left-assembly`. Sandbox paths; each value travels as its own argv entry. |
| `RightAssembly` | — (required in assembly mode) | Current assembly path(s), comma-separated, passed to `-r/--right-assembly`. |
| `Package` | — (required in package mode) | Current package path for `apicompat package <pkg>`. Setting `Package` or `BaselinePackage` selects package mode. |
| `BaselinePackage` | — (required in package mode) | Baseline package path passed to `--baseline-package`. |
| `SuppressionFile` | `null` | Compatibility suppression file(s), comma-separated, passed to `--suppression-file`. Prefer an operator-owned file outside the repository (see above). |
| `StrictMode` | `false` | Assembly-mode strict checks (`--strict-mode`): additions become findings. Not emitted in package mode (which has its own per-framework strict flags, reachable through `ExtraArguments`). |
| `RespectInternals` | `false` | Check internal APIs too (`--respect-internals`). |
| `EnableRuleCannotChangeParameterName` | `false` | Flag parameter renames (`--enable-rule-cannot-change-parameter-name`, e.g. `CP0017`). |
| `EnableRuleAttributesMustMatch` | `false` | Flag attribute mismatches (`--enable-rule-attributes-must-match`). |
| `NoWarn` | `null` | Diagnostic ids to suppress (`--noWarn`, e.g. `CP0017`). |
| `MinimumSeverity` | `info` | No-op for this auditor: every apicompat finding is `error`, so no threshold can narrow them. Use `IncludedRules` / `ExcludedRules` / `NoWarn` instead. |
| `IncludedRules` / `ExcludedRules` | — | Exact diagnostic ids to include or drop (e.g. `CP0002`). Applied by the host after the scan. |
| `ExcludePaths` | — (none) | Inert for this auditor: findings carry no file paths. Setting it changes nothing. |
| `ExtraArguments` | — | Extra CLI arguments appended to argv (e.g. `--exclude-attributes-file`, package-mode strict flags). `--generate-suppression-file` / `--suppression-output-file` are rejected; a `--suppression-file` / `--noWarn` / side flag here takes precedence over the matching scoped key. |
| `TimeoutSeconds` | `300` | Per-run execution bound. Exceeding it is an infrastructure failure. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; excess is reported as truncated. |

Assembly keys combined with package keys are a deterministic configuration
failure, as is a mode with no resolvable baseline (missing key and no
operator-supplied flag covering that slot).

## Default scope

By default (assembly mode), the auditor runs
`apicompat --left <LeftAssembly> --right <RightAssembly>` at the repository
root and compares exactly those binaries: the tool never walks the repository,
so vendored assemblies and generated trees cannot produce findings and no
`ExcludePaths` default is needed. Both assembly pointers are required with no
guessed default: which binaries constitute the public API surface varies per
repository (build outputs, checked-in reference assemblies), and any invented
default risks a silent no-op comparison. An unset baseline is a deterministic
infrastructure failure naming the key — enabling this auditor without
configuring it is a loud misconfiguration, never a silent skip.
