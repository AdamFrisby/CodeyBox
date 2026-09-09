# Session-capable agent runners

Most runners are one-shot: one prompt, one process, one exit. A session-capable
runner keeps one logical conversation across turns, so the provider's prompt
cache and the CLI's own transcript survive from one phase to the next. Only
Claude has a session worker today, and it is off by default.

Start from [`../concepts/agents.md`](../concepts/agents.md) for the one-shot
contract this builds on.


`IAgentRunner.RunAsync` remains the default one-shot contract. Runners that can
preserve logical conversation context across multiple turns may additionally
implement `ISessionAgentRunner`:

- `OpenSessionAsync(...)` opens a logical session against an already-created
  sandbox/VM.
- `SendTurnAsync(handle, prompt, ...)` sends one turn and returns an
  `AgentResult`; the session remains open.
- `SuspendSessionAsync(handle)` persists enough runner state that the
  underlying VM may be stopped to free resources.
- `ResumeSessionAsync(handle)` starts or reattaches to the same VM and prepares
  the stored runner session for another turn.
- `CloseSessionAsync(handle)` ends the logical session and disposes the VM.

The durable handle is `AgentSessionHandle`. It stores the runner kind, the
runner/provider session ID, the working directory, model/reasoning settings,
and an `AgentSessionSandboxRef` naming the exact sandbox/VM. That VM reference
is load-bearing: session resume must reattach to the same stopped/resumed VM,
not create a fresh clone, because the working tree and any runner-local state
belong to that VM.

The handle is safe to serialize for orchestrator restart recovery because it
contains only durable identifiers and non-secret metadata; it never stores the
live `ISandbox` object or `AgentCredential`. Credentials must be reacquired
through the normal credential provider after a restart. A persistent Claude
implementation can therefore store a handle containing `{ sessionId:
"<claude resume id>", sandbox:
"<multipass vm name>" }`, stop the VM between phases, and later run the next
turn by resuming that VM and invoking Claude with its resume ID. The Anthropic
prompt cache is server-side, so a live process is not required between turns;
landing the next turn within the provider TTL preserves the cache benefit, and
the session ID preserves conversation context regardless of VM stop/start.

For runners that do not implement true sessions, `StatelessSessionAgentRunner`
adapts any `IAgentRunner` into the session contract by calling `RunAsync` for
each turn. This has no prompt-cache or conversation-context benefit, but lets
session-aware pipeline code treat non-session runners uniformly without
changing existing one-shot behavior.

## Claude session worker (`ClaudeSessionWorker`)

`ClaudeSessionWorker` is the resumable counterpart to the default one-shot
`ClaudeAgentRunner`. It keeps ONE logical Claude CLI session across turns
using `claude --resume <session-id>` so the Anthropic server-side prompt
cache and the on-disk conversation transcript both carry over from one turn
to the next. The cache is server-side (~5min default TTL, 1h extended), so
stopping the worker VM between turns does **not** lose it as long as the
next turn lands inside the TTL; the session JSONL persists on the stopped
VM's disk so conversation context is preserved regardless.

The worker is OFF by default — the existing one-shot `ClaudeAgentRunner`
remains the registered `IAgentRunner` for Claude until config opts an item
in. The following dispatch gates must be true before a work item
takes the session path; any one of them off keeps the one-shot
pipeline (fresh sandbox per work / rework call, no
`--resume`, no shared VM across phases):

| Gate | Where | Default | Description |
|------|-------|---------|-------------|
| Global flag | `CodeyBox:ClaudeSession:Enabled` | `false` | Master switch. When false, Claude work items always use the one-shot path. |
| Per-project flag | `CodeyBox:Projects:<id>:ClaudeSession:Enabled` | `false` | Per-project opt-in. Unset projects keep the one-shot pipeline even with the global flag on. |
| Class/member flag | `CodeyBox:AgentClasses:<idx>:ClaudeSession:Enabled` or `Members:<idx>:ClaudeSession:Enabled` | unset / `false` | Required for class-routed items. Member settings override the class setting, so one Claude member can use sessions while another stays one-shot. Direct non-class Claude items are controlled by the project flag. |
| Agent kind | item's effective `Agent` | — | Must resolve to `claude`. The session worker is Claude-only. |
| Metrics knob | `CodeyBox:ClaudeSession:EmitTurnMetrics` | `true` | Not a dispatch gate; emits per-turn cache_read vs fresh-input metrics via `IClaudeSessionMetricsSink`. |

**Transport configuration keys** (independent of the gate set above —
these select how each turn is delivered once the session path is in
play):

| Key | Default | Description |
|-----|---------|-------------|
| `CodeyBox:ClaudeSession:Transport` | `print` | Command-delivery + billing channel. `print` = today's `claude --print --resume`. `acp` = Agent Client Protocol via `claude --ide` (OFF the metered `-p` pool). Case-insensitive, hot-reloadable. Invalid values fall back to `print`. |
| `CodeyBox:ClaudeSession:TransportOverridesByAgentClassMember:<member>` | (none) | Per-agent-class-member transport override. Wins over the per-project override and the global default. |
| `CodeyBox:ClaudeSession:TransportOverridesByProject:<projectId>` | (none) | Per-project transport override. Loses to the per-class-member override. |

#### Selecting a transport

`print` is the existing path: `claude --print --dangerously-skip-permissions
[--resume <id>]` per turn. Continuity comes from the captured Claude CLI
session id (passed back via `--resume`); cache warmth follows the server-side
prompt cache TTL.

`acp` runs the in-sandbox `ClaudeSessionWorker.AcpClaudeTransport` bridge:
CodeyBox materialises a tiny **C# native bridge binary** inside the sandbox
(self-contained, statically-linked NativeAOT ELF — no Node.js dependency on
the sandbox image), the bridge hosts a WebSocket on a random local port,
writes an IDE lockfile at `~/.claude/ide/<port>.lock` carrying
`{transport:"ws", url, authToken, workspaceFolders, ...}`, and spawns
`claude --ide` so the agent connects to the bridge. ACP JSON-RPC traffic
(`initialize`, `session/new` or `session/load`, `session/prompt`, streamed
`session/update`s, `stopReason`) flows host ↔ bridge stdio ↔ in-VM
WebSocket ↔ claude. Permission requests (`session/request_permission`) and
input requests (`session/request_input`) auto-grant / answer with a
`<codeybox-question>` default so a headless ACP turn never waits on a
human. Session continuity uses the assigned ACP session id, passed back via
`session/load` on the next turn.

The bridge ships as a `.csproj` in the master solution
(`src/CodeyBox.Agents.Claude.AcpBridge`); operators produce the native
binary by running `scripts/publish-acp-bridge.sh` on the build host (which
needs `musl-tools` installed — the static linker the AOT publish step
invokes). The publish settings use the documented NativeAOT `StaticExecutable`
MSBuild property for fully-static linking against musl on `linux-musl-x64`;
the script also runs `ldd` after publish and warns if the produced binary
isn't statically linked. The script writes the published ELF to
`src/CodeyBox.Agents.Claude/Resources/acp-bridge`, where it is picked up
as an embedded resource of the orchestrator-side Claude assembly. On hosts
that have not run the publish script, a tracked placeholder resource is
embedded instead — `AcpClaudeTransport.MaterialiseBridgeAsync` consults
`AcpBridgeBinary.IsPlaceholderBuild` and raises
`AcpTransportUnavailableException` **before touching the sandbox**, so the
worker degrades to the `print` transport on the first ACP turn rather than
spending a sandbox roundtrip to exec a non-binary.

The materialised payload travels via `SandboxExec.Stdin` (the bridge ELF
is base64-encoded on the host and decoded inside the sandbox by `base64 -d`
reading from stdin). It does NOT travel through argv — Linux
`MAX_ARG_STRLEN` caps every argv element at 128 KiB regardless of in-shell
heredoc syntax, which a multi-MB NativeAOT bridge would trip. Keeping the
script tiny (~200 bytes) means the same materialise step works identically
across Process / Bubblewrap / Multipass sandbox providers.

If the ACP transport fails to open or any turn raises
`AcpTransportUnavailableException`, the worker logs
`agent.claude_acp_transport_degraded` and falls back to the `print`
transport for the rest of that session — the work item is never stranded.
Per-turn metrics (`ClaudeSessionTurnMetrics`) carry the `Transport` tag
(`"print"` / `"acp"`) so dashboards can confirm traffic moved off the
metered pool.

**Compose example** — enabling the session worker for one project:

```jsonc
// appsettings.json (or local override)
{
  "CodeyBox": {
    "ClaudeSession": { "Enabled": true },
    "AgentClasses": [
      {
        "Id": "frontier-session",
        "DisplayName": "Frontier coding with Claude sessions",
        "ClaudeSession": { "Enabled": true },
        "Members": [
          { "Agent": "claude", "Billing": "Subscription", "ModelId": "claude-opus-4-7", "QualityScore": 100 },
          { "Agent": "codex", "Billing": "Subscription", "ModelId": "gpt-5.5", "QualityScore": 100 }
        ]
      }
    ],
    "Projects": [
      {
        "Id": "my-project",
        "RepositoryUrl": "https://github.com/me/repo.git",
        "Agent": "claude",
        "DefaultAgentClass": "frontier-session",
        "ClaudeSession": { "Enabled": true }
      },
      {
        "Id": "other-project",
        "RepositoryUrl": "https://github.com/me/other.git",
        "Agent": "claude"
        // ClaudeSession omitted → this project keeps the one-shot path even
        // though the global flag is on.
      }
    ]
  }
}
```

CheckAndAct items always take the one-shot path even when the gates are on —
the single-shot read-only audit has no rework loop, so session reuse has
no value. The session worker is also Claude-only; items whose effective
agent is Codex / Gemini / Copilot / Cursor / Opencode keep the legacy
path regardless of the project flag.

**Auditor isolation (non-negotiable):** the auditor NEVER shares the
worker's session or VM. Each audit iteration spins up its own fresh
sandbox via the existing `CollectFindingsAsync` path. A session-shared
auditor would rubber-stamp the worker's own changes and silently merge
broken code. This is a hard architectural invariant; the brief explicitly
forbids a persistent-auditor option in this rollout step.

Lifecycle inside the worker:

- `OpenSessionAsync` allocates local state; the Claude CLI session id is
  captured from the first turn's `stream-json` `system/init` event.
- `SendTurnAsync` runs with `claude --print --output-format stream-json --verbose`
  (so the CLI session id and per-turn usage are captured). The first turn
  runs fresh; subsequent turns add `--resume <session-id>`. The same
  `ClaudeSessionSanitizer` the one-shot runner uses scrubs partial/unsigned
  thinking blocks from the stored transcript before each resume, and a
  thinking-block 400 triggers one sanitise-and-retry pass before surfacing
  the failure.
- `SuspendSessionAsync` calls `IPreemptibleSandbox.StopAndPreserveAsync`
  on the sandbox (`multipass stop`, **not** `delete --purge`) so the VM's
  disk — including `~/.claude/projects/<slug>/<session>.jsonl` — is
  preserved.
- `ResumeSessionAsync` calls the configured sandbox-resume hook to bring
  the VM back. The hook is wired in production to
  `ISuspendingSandboxProvider.ResumeSandboxAsync` (i.e. `multipass start`)
  when the registered provider supports the suspend contract; non-suspending
  providers (process / bubblewrap) leave it null. Any failure of the hook
  flips the worker into fresh-one-shot mode for the remainder of the
  session rather than stranding the work item.
- `CloseSessionAsync` disposes the sandbox and ends the logical session.

Restart recovery: `AgentSessionHandle` is safe to persist (no live objects,
no credential material). `ClaudeSessionWorker.SnapshotPersistedHandle`
returns a handle augmented with the captured CLI session id under
`Metadata["claude.cliSessionId"]`, and a fallback flag under
`Metadata["claude.fallbackToOneShot"]` once the worker has degraded to
fresh-one-shot mode (so a restart inherits that degraded state instead of
re-attempting the failed resume).

**Pipeline-level restart behavior (item 3):** when a session-enabled item
is interrupted by an orchestrator restart, the pipeline does NOT attempt
to reattach to the orphaned session-worker VM from the prior process. The
durable one-shot-turn checkpoint described above is a separate mechanism:
when one exists, the recovered item uses the independent-phase path
to restore its source tree and, only for the exact route, its host-private
runner scratchpad in a new sandbox.

Without a checkpoint, the recovered session-worker item degrades to the
one-shot path for the remainder:

- An item picked up at `WorkComplete` or later (`skipWork=true`) doesn't
  open a new session lifecycle; the existing audit / rework / merge loop
  uses fresh sandboxes via the one-shot code path.
- An item picked up at `Queued` after a crash mid-work opens a NEW session
  against a new VM. The prior VM is treated as a leak and reaped by
  `SandboxLeakReaper`.

Either way the item never strands — it resumes from durable checkpoint
evidence when that evidence exists, or takes the degrade path. Reviving the
session worker against an existing VM would require an
`IAttachableSandboxProvider` hook the provider API does not yet expose;
agent-turn recovery intentionally does not claim to reattach that VM.

Per-turn metrics are emitted as `ClaudeSessionTurnMetrics` snapshots to the
registered `IClaudeSessionMetricsSink`. Each snapshot carries the total
input tokens, cache_read tokens, cache_creation tokens, derived fresh-input
tokens, output tokens, model id, and a `UsedResume` flag so the operator can
confirm the turn actually ran with `--resume` and the cache is paying off.
The default sink registration is `NullClaudeSessionMetricsSink`; hosts wire
a logging / metrics-backed sink by registering their own
`IClaudeSessionMetricsSink` before service-provider build.

#### Verifying ACP cache warmth

Both transports (`print` via `--resume`, `acp` via `session/load`) restart the
`claude` process per turn — `sandbox.ExecAsync` is one-shot stdin, so the ACP
bridge tears down `claude --ide` at the end of every turn just like the print
path. Provider-side prompt-cache continuity is therefore claimed via the
session id, not via process survival. **That claim is only verified for the
print transport today.** For ACP, the assumption is `session/load` reattaches
to the same logical session and the server-side cache rides over — but until
the deployed ACP binary has been observed running consecutive turns, this is
an unverified read from the protocol docs.

Before scoping the larger "daemon bridge survives across turns" follow-up
(which needs a streaming `ISandbox.ExecAsync` or host-reachable WebSocket),
**run STEP 1: empirically verify cache warmth from the per-turn metric.** The
metric exposes the four buckets needed: `CachedInputTokens` (cache_read),
`CacheCreationInputTokens` (cache_write paid this turn), `FreshInputTokens`
(billable bucket = real fresh + cache_creation), and `OutputTokens`.

1. Pick one work item with at least 3 turns on the ACP transport
   (`Transport == "acp"` on each metric record).
2. Read `CachedInputTokens` and `CacheCreationInputTokens` per turn:

   | Turn | cache_read | cache_creation | Read it as |
   |---|---|---|---|
   | 1 | 0 | large | Cold start — cache being written |
   | 2+ | large | ~0 | **Warmth preserved.** `session/load` is reattaching to a warm provider cache. The daemon bridge becomes a *latency* optimisation only (saves ~hundreds of ms per turn of `claude --ide` startup). Modest priority. |
   | 2+ | ~0 | large again | **Warmth NOT preserved.** Cache is being rebuilt every turn, paying the cache-write rate repeatedly. **Escalate.** This is a cost / 5 h-quota regression, not a latency footnote. Daemon-bridge work becomes higher-priority and must be scoped against the dollar impact, not the millisecond impact. |

3. Cross-check against the print transport on the same prompt shape to confirm
   the print path is hitting the cache too (otherwise the comparison is
   measuring something else — e.g. a TTL miss, not a transport bug).

Until the measurement runs, **do not** start the daemon-bridge work blind —
its scope depends on which branch the data points to.

The fleet stays pinned to `claude-opus-4-7`; the session worker does not
hot-swap models mid-session. Long resumable sessions are the exact trigger
surface for the thinking-block immutability 400 cluster, so the sanitiser
is wired in unconditionally (gated only by
`CodeyBox:ClaudeThinkingBlockSanitizer:Enabled`).
