# MSBuild structured build diagnostics (CBX-NEXT-062)

The required-build gate answers "does the branch compile". When it does not,
the repair loop needs *why*: which error is the root cause, in which project
and target. This adapter supplies that causal context from MSBuild binary
logs — as **one diagnostics producer** behind the language-neutral
`IBuildDiagnosticsProducer` / `BuildDiagnosticsEvidence` contract in
`CodeyBox.Core`. It is not a success gate, not a second build, and not
.NET-specific core: toolchain specifics (binlog flags, StructuredLogger
parsing, MSBuild options) live in `CodeyBox.Build.MSBuild`; Core only knows
providers, severities, locations, projects, targets, and causal links. A
non-MSBuild producer (for example a `cargo`-style JSON emitter) plugs into
the same registry and the same repair-loop rendering with no dotnet and no
binlogs involved.

Official source for the log format and viewer: https://msbuildlog.com/.

## Activation (operator opt-in, no code change)

Disabled by default; nothing is captured, parsed, or attached until enabled:

```jsonc
// appsettings.Production.json (or any configuration source)
{
  "CodeyBox": {
    "MSBuildDiagnostics": {
      "Enabled": true
    }
  }
}
```

`MSBuildDiagnosticsOptions` (`CodeyBox:MSBuildDiagnostics`) hot-reloads via
`IOptionsMonitor`: flipping `Enabled` or tuning bounds applies without a
restart. Registration (`AddMSBuildBuildDiagnostics`) only adds the options
and the capability-based producer; the authoritative build exit/outcome is
unchanged with or without it.

## How it works

For an applicable failed required build, and only then:

1. The **same isolated sandbox session** that ran the build runs a capture
   variant of the build script. Each `dotnet build` additionally writes a
   least-data binary log (`-bl:<file>;ProjectImports=None` — no embedded
   imported-project content) to a fixed directory. No second build is
   executed for diagnostics; when disabled the executed command is
   byte-identical to the historical one. Both variants are composed from one
   shared script builder (`RequiredBuildScript`), so target discovery and
   the isolated build environment (NuGet-home redirection, offline cache
   preservation, telemetry off, heal sourcing, cleanup) are identical on
   both paths — opted-in builds never run in a diverged environment.
2. The verifier fetches at most `MaxBinlogsPerBuild` logs over bounded
   `stat`/`base64` execs (structured argv, allowlisted `target-N.binlog`
   names, per-file byte caps), records the exact commit SHA
   (`git rev-parse HEAD`) and a fresh attempt id, and parses **locally in
   this process** through the pinned StructuredLogger reader.
3. Per-file evidence is merged into one attempt-bound
   `BuildDiagnosticsEvidence`: errors-first under `MaxDiagnostics`, causes
   re-linked, artifact refs (name + SHA-256 + size — never content)
   attached, totals summed. A listed log that yields nothing usable
   (transfer loss, malformed framing, oversize payload, corrupt content,
   unsupported version, or a listing cut short by a collection bound) is
   never silently dropped: if any log survived, the merged evidence is
   marked `Truncated` with a `partial-evidence` reason naming every lost
   file, and the rendered repair-loop text carries that note; if none
   survived, the result is explicit `InsufficientDiagnostics` naming the
   lost files.
4. The gate appends the rendered root-cause failures plus project/target
   context to the rework/audit finding text (`BuildFailureSummary` and the
   persisted `AuditResult` finding). The `Failed` outcome stands regardless.

Every problem — missing/empty/corrupt/truncated/oversized log, unsupported
format version, parser failure, capture or transport error, invalid operator
configuration — yields explicit `InsufficientDiagnostics` evidence with a
reason. Missing or uncertain evidence never counts as a pass and never hides
the original build failure. Caller cancellation still propagates.

## Causality model (weak, documented)

MSBuild aborts the failing target at its first error, so later errors in the
same project execute in an already broken build. Each project's earliest
error is marked the root cause (`IsRootCause`, surfaced in
`RootCauseIds`); later same-project errors link to it (`CausedByIds`) so the
repair loop fixes earliest-first. Cross-project errors stay unlinked:
independent project builds fail independently. Warnings never link. This is
a temporal heuristic, not proven dependence — stated here so future readers
do not over-interpret the edges.

## Version support

- Reader pin: `MSBuild.StructuredLogger` **2.3.246**.
- `PinnedFileFormatVersion` (26): the highest format version verified
  readable by that pin — the .NET 10 SDK toolchain emits v26. Accepted logs
  must satisfy `MinSupportedFileFormatVersion (9) <= version <=
  MaxSupportedFileFormatVersion (26)`; anything newer is rejected as
  `unsupported-version` rather than silently misread.
- **Before bumping the package pin**: rebuild, re-run the round-trip tests
  (`MSBuildDiagnosticsTests`, which generate genuine SDK binlogs), confirm
  the emitted `FFV`, and move the bound together with the pin.

## Security posture

- **Least data**: only error/warning records plus project/target ancestry
  are retained. Command lines, environment blocks, and embedded project
  imports are never collected (`ProjectImports=None`) and never stored.
- **Binlogs are untrusted input**: they may contain plaintext secrets and
  command arguments. Every retained string is redacted
  (`RawOutputRedactor`) and length-capped before it reaches evidence; the
  repair-loop rendering is additionally output-capped. Raw binlogs are never
  uploaded anywhere, never printed, and only content-hashed refs are kept —
  and then only under the existing project ownership/retention policy.
- **No execution**: parsing deserialises the event stream into an in-memory
  tree; no MSBuild evaluation, task execution, or file write occurs.
  Failure attribution is data, never instructions — callers must not execute
  anything diagnostics contain.
- **Stale/substituted evidence is rejected**: capture and evidence are bound
  to the exact source (commit SHA), configuration, and attempt id by exact
  equality; the capture script purges the binlog directory before building
  so an earlier attempt's log can never attach. Merging refuses
  cross-binding parts with an exception.
- **Bounded everywhere, before buffering**: binlog bytes, file count,
  gzip decompression ratio, tree nodes, tree depth, diagnostic count,
  message/path/code lengths, fetch stdout, parse wall-clock. A gzip
  integrity pre-check (framing trailer: CRC32 + content length) rejects
  truncated or substituted payloads that the record reader would otherwise
  accept as partial data.

## Bounds reference (defaults)

| Knob | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | master switch |
| `BuildConfiguration` | `Debug` | `-c` value (allowlisted `^[A-Za-z0-9_.-]{1,64}$`) |
| `MaxBinlogBytes` | 8 MiB | per-file cap, enforced before buffering |
| `MaxBinlogsPerBuild` | 4 | files collected per attempt |
| `MaxDiagnostics` | 64 | diagnostics retained per evidence |
| `MaxDiagnosticMessageChars` | 2048 | per-message cap after redaction |
| `MaxNodesVisited` | 200000 | tree-walk cap |
| `MaxDepth` | 64 | tree-depth cap |
| `MaxDecompressionRatio` | 32 | gzip bomb guard |
| `ParseTimeout` | 30 s | per-file parse budget |
| `Min/MaxSupportedFileFormatVersion` | 9 / 26 | accepted binlog format range |

Invalid option values fail options validation at startup; a non-positive
parse budget at runtime yields insufficient-diagnostics rather than a hang.
