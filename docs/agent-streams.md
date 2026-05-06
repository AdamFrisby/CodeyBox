# Agent Streams

CodeyBox captures the structured stdout event stream from agent invocations and
stores it as JSONL for later analysis. This layer does not interpret events; it
only requests the agent CLI's streaming JSON mode when available and persists
each stdout line as it arrives.

## Configuration

```json
{
  "CodeyBox": {
    "AgentStreams": {
      "Enabled": true,
      "Path": "logs/agents",
      "MaxFileSizeMb": 32,
      "RetainedDays": 14
    }
  }
}
```

| Key | Default | Description |
|---|---:|---|
| `Enabled` | `true` | When `false`, no stream flags are added and no files are opened. |
| `Path` | `logs/agents` | Root directory for captured streams. Must be writable at startup. |
| `MaxFileSizeMb` | `32` | Per-file cap. After the cap is reached, later stdout lines are dropped and a final `[...truncated by N bytes]` marker is appended. |
| `RetainedDays` | `14` | Daily sweep deletes older stream files. `0` keeps files forever. |

Startup validation rejects an empty path, `MaxFileSizeMb < 1`, or
`RetainedDays < 0`.

## File Layout

Files are written under:

```text
logs/agents/<workItemId>/<phase>-<iteration>-<short-uuid>.jsonl
```

Release-wide deep-audit LLM streams do not have a work item; those captures use
the release GUID in the same path position.

`<short-uuid>` is 6 lowercase hex characters, so retries do not overwrite prior
attempts.

Examples:

```text
logs/agents/abc12345-0000-0000-0000-000000000000/work-1-a1b2c3.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/audit-llm-security:llm-review-3-d4e5f6.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/rework-2-789012.jsonl
logs/agents/abc12345-0000-0000-0000-000000000000/merge-1-f0e1d2.jsonl
```

Captured phases are:

- `work`
- `rework`
- `merge`
- `audit-llm-<auditor-name>`

Work and merge use iteration `1`. Audit and rework use the current audit-loop
iteration.

## Capture Semantics

The stream file is stdout only. CodeyBox does not add log prefixes or analysis
records. Each line is redacted for known secret token patterns before writing,
using the same redaction pattern family as `SensitiveDataRedactionEnricher`.

Capture is best-effort. Disk errors are logged as warnings and do not fail the
agent invocation. Writers are closed when the agent run exits, including
cancellation and failure paths.

## Agent CLI Flags

Verified in this sandbox:

- Claude Code 2.1.128: `claude --print --output-format stream-json --verbose ...`
  (`claude --help` advertises `--output-format` choices including `stream-json`).
- Codex: `codex exec --json ...`

Not locally verifiable here:

- Gemini: the `gemini` binary was not installed in this sandbox. CodeyBox probes
  `gemini --help` inside the invocation sandbox and only adds `--json` when that
  help output advertises the flag. If unavailable, the run falls back to normal
  text output and no stream file is persisted for that invocation.

Unsupported:

- Copilot: the current Copilot CLI runner does not expose a structured streaming
  mode, so no stream flag is added.

## API

- `GET /workitems/{id}/agent-streams` lists captured files and metadata. The
  list is bounded (`limit`, default 100, maximum 500); `lineCount` is only
  populated when `includeLineCount=true`.
- `GET /workitems/{id}/agent-streams/{fileName}` returns the raw stream as
  `application/x-ndjson`.

See [`api.md`](api.md#agent-streams) for response shapes.

## Storage Cost

Typical streams are a few KB to about 1 MB per invocation. A five-iteration item
with three LLM auditors per iteration can produce around 20 stream files. At
roughly 10-30 MB per work item and 100 work items per week, the default 14-day
retention is about 30 GB of stream data.

`logs/` is gitignored, which covers the default `logs/agents/` path.
