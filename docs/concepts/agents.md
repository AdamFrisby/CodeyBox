# Agents

CodeyBox treats coding agents as plug-in CLIs. The framework speaks to
each agent through `IAgentRunner`. Agents are language-agnostic: they receive
a prompt and a git working tree, then edit files in whatever stack the project
uses. Language-specific behavior lives in auditors and operator-supplied
tooling, not in the agent runner contract.

## Built-in agents

| Kind        | Binary in sandbox | Auth env (sandbox-side) | Host env (orchestrator) |
|-------------|-------------------|-------------------------|-------------------------|
| `claude`    | `claude`          | `ANTHROPIC_API_KEY`     | `CODEYBOX_CLAUDE_API_KEY` |
| `copilot`   | `copilot`         | `GH_TOKEN`              | `CODEYBOX_COPILOT_TOKEN`  |
| `codex`     | `codex`           | `OPENAI_API_KEY`        | `CODEYBOX_CODEX_API_KEY`  |
| `gemini`    | `gemini`          | `GEMINI_API_KEY`        | `CODEYBOX_GEMINI_API_KEY` |
| `cursor`    | `agent`           | `CODEYBOX_CURSOR_AUTH_JSON` (subscription credentials JSON) | `CODEYBOX_CURSOR_AUTH_FILE` (file path on host) |
| `opencode`  | `opencode`        | `OPENCODE_AUTH_JSON` (file-materialised) | `CODEYBOX_OPENCODE_AUTH_FILE` |
| `antigravity` | `agy`           | `CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON` (OAuth credentials JSON, file-materialised to `~/.agy/oauth_creds.json`) | `CODEYBOX_ANTIGRAVITY_OAUTH_TOKEN` (raw access token fallback) |

The sandbox-side env name is what the agent CLI reads. The host-side env
name is what the orchestrator's `EnvironmentCredentialProvider` looks up
when building the credential bundle. They are intentionally namespaced
differently so the host environment can hold multiple agents'
credentials at once without collision.

## Sandbox install commands

Each agent CLI must be present in the sandbox VM before dispatch; otherwise
dispatch fails with exit 127 (`<binary>: No such file or directory`). This
applies to both baked baselines and full-launch provisioning. The orchestrator
does **not** install agent binaries — that is operator-owned config under
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd` (see
[`baseline-bake-examples.md`](baseline-bake-examples.md)).
Adding an `AgentKind` to an agent class without also adding its install line is
the most common cause of fresh-class dispatch failures.

| Kind | Sandbox install command | Notes |
|------|-------------------------|-------|
| `claude`  | `npm install -g @anthropic-ai/claude-code` | Needs Node.js on the image. |
| `copilot` | *operator-supplied — verify with current [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) docs* | The runner execs the standalone `copilot` binary (NOT `gh copilot`); `gh extension install github/gh-copilot` is the wrong CLI and will not satisfy the runner. |
| `codex`   | `npm install -g @openai/codex` | Reuses the Node.js stack from Claude. |
| `gemini`  | `npm install -g @google/gemini-cli` | `ReasoningMode` is **not** wired into argv — Gemini's reasoning level is encoded in `ModelId` (pick a `gemini-3-*-preview` model for HIGH). See [Gemini quirks](#google-gemini-cli-googlegemini-cli). |
| `cursor`  | `curl -fsSL https://cursor.com/install \| bash` | Installs as `agent` (not `cursor-agent`). See [Cursor quirks](#cursor-cli-agent). |
| `opencode` | *not yet integrated in this repo — no `IAgentRunner` for opencode has shipped.* Operators tracking the integration can pre-stage with `curl -fsSL https://opencode.ai/install \| bash`, but the orchestrator will not route work to it until a runner is registered. | Listed for doc parity with the install-checklist; **does not** imply opencode is dispatchable today. |
| `antigravity` | *operator-supplied — stage the `agy` binary on the host and ship it via `CodeyBox:MultipassExecutableProvisions` or `CodeyBox:Incus:ExecutableProvisions`, matching the selected provider* (see [Antigravity quirks](#google-antigravity-cli-agy)). The previously documented `curl -fsSL https://antigravity.google/cli/install.sh \| bash` URL no longer serves a shell script (returns HTML as of 2026-06-17); piping HTML into `bash` fails silently if the runcmd ends with `\|\| true`. | Installs the proprietary `agy` CLI on the non-login sandbox PATH. Multi-model gateway — each gateway model id is a separate quota bucket. Configure each accepted model as its own `AgentClass` member; the router gates per-model via the existing `(AgentKind, ModelId)` exhaustion key. |

Verify each command against its upstream install docs at the time of baking —
versions and install URLs change. Multipass and Incus keep independent bake
inputs and baseline identities; changing a selected provider's explicit
provisioning config changes its baseline hash and triggers the corresponding
new bake.

## Adding a new agent

1. Create a project `CodeyBox.Agents.<Name>`.
2. Subclass `CliAgentRunnerBase` and implement `BuildInvocation`. Example:

   ```csharp
   public sealed class AiderAgentRunner : CliAgentRunnerBase
   {
       public override AgentKind Kind => new("aider");
       public string Binary { get; init; } = "aider";

       protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? cred)
           => new(["aider", "--yes", "--message", prompt]);
   }
   ```

3. Register it in `Program.cs` alongside the others.
4. Add a credential mapping if the agent needs an API key:

   ```csharp
   new AgentCredentialMapping(new AgentKind("aider"), "CODEYBOX_AIDER_KEY", "OPENAI_API_KEY"),
   ```

5. **Install the binary during sandbox VM provisioning.** This is **not
   optional** — without it, every dispatch to the new agent fails with
   exit 127 (`<binary>: No such file or directory`). The orchestrator does
   not auto-install; the install line lives in operator config under
   `CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd` (see
   [`baseline-bake-examples.md`](baseline-bake-examples.md)). The providers
   keep independent content-addressed baseline identities, and Incus also
   applies its explicit inputs on the full-launch path. Pin by digest where
   the upstream supports it.

6. **Document the install command** in the "Sandbox install commands"
   table above and add a "Per-agent quirks" subsection covering the
   binary name, non-interactive invocation, credential layout, and any
   reasoning-flag / quota-probe specifics. Doc parity keeps the next
   class-edit from rediscovering this footgun.

7. **Add a smoke probe** (`IAgentSmokeProbe`) so the credential gets
   verified before work-item pickup — see `ClaudeSmokeProbe` /
   `CursorSmokeProbe` for the two shapes (HTTP-endpoint probe vs.
   credential-bundle-presence probe).

   > **Scope note — pre-dispatch binary check.** Today's smoke probes run
   > on the host (HTTP-endpoint or credential-bundle presence) and do
   > **not** exec the CLI inside a freshly-cloned sandbox, so a missing
   > binary still surfaces as exit-127 at first dispatch rather than at
   > smoke time. Closing that loop (sandbox-side `--version` execution
   > gating dispatch) is the **companion smoke-gate ticket**; it is
   > intentionally out of scope for this configuration-side fix, which
   > only documents the canonical install commands. Until the gate ships,
   > the operator's defence against exit-127 is the install table above.

## Why one-shot CLI invocation

Every supported agent has a non-interactive mode that takes a prompt, edits
files, and exits. CodeyBox relies on that contract: the agent's job is to
leave the working tree at `/work` in the state it wants committed. The
orchestrator then stages everything and commits. If the agent makes no
file changes, the work phase fails (rather than producing an empty commit).

Agents that only have an interactive mode are not currently supported.
Adding them would mean implementing a streaming variant of `ISandbox.ExecAsync`
plus a turn-loop in the runner — both are bounded scope and can be added
without changing the orchestrator.

## Recovering an interrupted one-shot turn

The one-shot process may exit before the orchestrator can commit its work. For
work and rework turns, CodeyBox has three bounded recovery layers. The third is
currently Incus-only:

| Layer | What is preserved | Resume behavior |
|---|---|---|
| Live-sandbox CLI resume | The current `/work` tree and, for Claude/Codex, the exact validated CLI session id captured from structured output | While the sandbox is still usable, Claude runs `--resume <id>` and Codex runs `exec resume <id>`. The resumed CLI receives a short continuation instruction rather than the original task again. |
| Durable agent-turn checkpoint | The dirty source tree in an immutable content-addressed Git ref; a bounded CLI scratchpad archive in host-private SQLite storage; phase, prompt revision, and exact route/model/reasoning metadata; and an optional native session id | A later dispatch atomically claims an attempt and fetches the source ref into a new sandbox. The exact route receives the private archive through `RunResumedAsync`; Claude/Codex also use the exact saved session id when available. |
| Retained Incus recovery lease | The exact stopped Incus VM and its persistent COW-root `/work`, plus a provider-bound opaque token and creation-time recovery manifest | Used only when Incus cannot execute the commands needed to create the immutable checkpoint. A later pickup adopts that exact VM under an exclusive preparation claim, converts it to the ordinary Git/private-state checkpoint without dispatching an agent, deletes the duplicate VM, and automatically queues the immutable resume. |

The durable checkpoint is retained only when a work/rework result remains a
recognised quota failure, transient-network failure, infrastructure failure
with the sandbox provider's explicit `ExecutionUnavailable` signal, or process
exit `137` (SIGKILL/OOM infrastructure evidence). It is not retained merely
because stderr contains infrastructure-shaped text or a generic CLI retry
budget was exhausted.

The scratchpad archive is capped at 32 MiB and never committed to Git. It is
captured under the private `/run/codeybox/agent-turn` tmpfs, removed from that
tmpfs before the dirty source tree is staged, and any legacy repository-local
scratchpad artifacts are stripped from the Git index.
The resulting ref has the canonical shape
`refs/heads/codeybox/preempt/<work-item-id>/<source-commit>-<archive-sha256>`;
resume verifies that it names the same work item and resolves to the embedded
source commit. The archive is an immutable SQLite BLOB keyed by that exact ref,
and its bytes are verified against the embedded SHA-256 before restoration.
This content binding lets a failed replacement capture roll back its own BLOB
without changing an older valid checkpoint.

If Incus reports execution unavailable and the immutable capture cannot run,
CodeyBox may publish a retained-sandbox lease instead. Lease publication keeps
the same turn metadata and is atomic with the work-item lifecycle comparison.
It is accepted only while the global
`CodeyBox:PipelineTuning:MaxRetainedAgentTurnSandboxes` count is below its
configured bound (16 by default). Reaching the cap does not manufacture a
checkpoint or success result: preservation is disarmed and the original
failure remains authoritative.

The lease is an internal capability. Incus binds its token hash and immutable
manifest hash to the VM at creation time and keeps the manifest in its private
host staging tree. Adoption validates the exact provider, project, instance,
sandbox specification, storage and guest identities, network and mount
topology, host-source inode pins, and recorded guest links. A database
preparation claim and the provider's host file lock prevent two workers or
processes from mutating the retained VM concurrently. A failed adoption or
conversion leaves the VM preserved and releases that preparation claim; it
does not consume a resumed-agent attempt.

The retained VM is mutable recovery evidence, so CodeyBox never launches the
next agent turn in it. Once infrastructure is healthy, the adopter first
captures the CLI scratchpad and dirty tree into the normal content-bound
checkpoint. SQLite publication atomically replaces the lease with the
Git/private-state boundary. Only then is VM preservation disarmed, the VM
deleted, and the item enqueued for an ordinary immutable resumed dispatch.

Retry without an explicit phase automatically chooses the interrupted work or
rework boundary. The first resumed dispatch is pinned to the exact agent
instance route, model, and reasoning mode that created the checkpoint. If an
agent-class fallback later selects a different member, that member may continue
from the checkpointed source tree, but it never receives another route's
host-private archive or native session id. Because the private bytes were never
Git objects, the fallback cannot recover them from the worktree, Git history,
or the mounted bare origin. This file-only fallback prevents a provider session
from being attached across an agent/account boundary.

A clean resumed invocation with no new Git diff is successful only when the
checkpoint itself already contains meaningful source changes relative to the
work-branch tip from before the interrupted turn. An empty checkpoint followed
by a no-op resume follows the ordinary initial-work or rework no-diff failure
policy; recovery does not manufacture a successful turn from an allow-empty
checkpoint commit.

`CodeyBox:PipelineTuning:AgentSessionResumeMaxAttempts` bounds both live
CLI-native resume attempts and durable checkpoint re-dispatches; the counters
are separate. Every durable dispatch, including direct startup/dead-worker
re-enqueues, converges on one atomic claim in `PipelineRunner`; concurrent or
over-budget claims fail closed. A value of `0` disables CLI-native resume and
agent-turn re-dispatch, but does not redefine the separate legacy suspend retry,
sandbox-adoption, or Git-only preempt-record behavior. A prompt revision change,
an explicit retry from a different phase, successful phase completion, or
exhaustion of the durable attempt budget discards the checkpoint instead of
resuming stale context. The checkpoint also remains paired while an exact route
is parked in `WaitingForAgentResume`. Typed Git or retained-lease boundaries
also remain paired in `NeedsOperatorInput` and
`AbandonedAfterRecoveryAttempts`, where an operator may still choose the exact
recovery boundary. Cancellation or a lifecycle move that intentionally clears
the boundary exposes any retained VM to the normal sandbox leak reaper.

After a resumed agent returns successfully and CodeyBox pushes and syncs its
resulting tree to the work branch, the older turn checkpoint and private archive
are cleared before required-build or other post-agent verification. If that
later verification is unavailable, retry starts from the durable published
branch boundary rather than replaying the pre-turn source tree and session.

This path is fail-closed. Outside the bounded Incus retained-lease fallback, a
runner that cannot capture a checkpoint, a failed private-archive write, a
failed Git push, or a failure before any resumable state exists follows the
normal phase failure/retry policy. It does not manufacture a successful turn.
Clearing checkpoint metadata deletes the paired private archives in the same
SQLite update; startup reconciliation removes orphaned or no-longer-referenced
archives left by a crash. A deleted retained VM or lost root disk cannot be
reconstructed. After conversion, a replacement sandbox needs the pushed
content-bound source ref, and exact-route session restoration additionally
needs its matching host-private archive.

## Session-capable runners

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

### Claude session worker (`ClaudeSessionWorker`)

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
takes the session path; any one of them off keeps the legacy
independent-phase pipeline (fresh sandbox per work / rework call, no
`--resume`, no shared VM across phases):

| Gate | Where | Default | Description |
|------|-------|---------|-------------|
| Global flag | `CodeyBox:ClaudeSession:Enabled` | `false` | Master switch. When false, Claude work items always use the one-shot path. |
| Per-project flag | `CodeyBox:Projects:<id>:ClaudeSession:Enabled` | `false` | Per-project opt-in. Unset projects keep the legacy pipeline even with the global flag on. |
| Class/member flag | `CodeyBox:AgentClasses:<idx>:ClaudeSession:Enabled` or `Members:<idx>:ClaudeSession:Enabled` | unset / `false` | Required for class-routed items. Member settings override the class setting, so one Claude member can use sessions while another stays legacy. Direct non-class Claude items are controlled by the project flag. |
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
        // ClaudeSession omitted → this project keeps the legacy path even
        // though the global flag is on.
      }
    ]
  }
}
```

CheckAndAct items always take the legacy path even when the gates are on —
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
when one exists, the recovered item uses the legacy independent-phase path
to restore its source tree and, only for the exact route, its host-private
runner scratchpad in a new sandbox.

Without a checkpoint, the recovered session-worker item degrades to the
legacy one-shot path for the remainder:

- An item picked up at `WorkComplete` or later (`skipWork=true`) doesn't
  open a new session lifecycle; the existing audit / rework / merge loop
  uses fresh sandboxes via the legacy code path.
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

## Per-agent quirks

### Claude Code
Run with `--print` for non-interactive output and
`--dangerously-skip-permissions` because the VM boundary already is the
permission boundary. Remove `--dangerously-skip-permissions` if you also
want the agent's built-in tool-use prompts (you usually don't, inside a VM).

**Reasoning level:** the CLI's `--effort` flag accepts `low | medium | high |
xhigh | max`. `ReasoningMode` on the agent-class member is passed through
verbatim; if it's unset the flag is omitted and the CLI's own default applies.

**Network tolerance:** `CodeyBox:AgentNetworkTolerance:claude:ApiTimeoutMs`
maps to Claude Code's `API_TIMEOUT_MS` environment variable. CodeyBox leaves
it unset by default because raising the timeout helps slow large-context calls
but also lengthens hangs on dead connections. Set it only when the operator
chooses that tradeoff. Values are capped at 28,800,000 ms (480 minutes), the
maximum work-attempt window accepted by the API.

### GitHub Copilot CLI
Reads `GH_TOKEN` from the environment. **Important**: a generic
`GH_TOKEN` grants the agent broad GitHub access, not just Copilot. Issue
a fine-grained token scoped to the minimum the agent needs, ideally one
that cannot push to your real repos. Sandbox network policy must
**not** include `github.com` for this token to be safe.

### OpenAI Codex CLI
Reads `OPENAI_API_KEY`. The `--full-auto` flag skips Codex's per-edit
confirmations — appropriate inside a sandbox.

**Network tolerance:** Codex gets provider-scoped `-c` overrides from
`CodeyBox:AgentNetworkTolerance:codex`. The shipped defaults are more
tolerant than the vendor defaults:

```json
"AgentNetworkTolerance": {
  "codex": {
    "RequestMaxRetries": 8,
    "StreamMaxRetries": 15
  }
}
```

`RequestMaxRetries` maps to `request_max_retries` and `StreamMaxRetries` maps
to `stream_max_retries`; both are capped at 100. `StreamIdleTimeoutMs` is
optional, maps to `stream_idle_timeout_ms` when configured, and is capped at
28,800,000 ms (480 minutes), the maximum work-attempt window accepted by the
API. `Provider` is optional and must match `[A-Za-z0-9_-]+`; when unset,
CodeyBox derives the provider id from the effective model id and falls back to
`openai`.

### Opencode CLI (`sst/opencode`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider. opencode publishes both an `npm`
distribution and a `curl | bash` installer:

```sh
# Preferred when npm is already on the baseline (the Gemini/Claude CLIs need it):
npm install -g opencode
# OR
curl -fsSL https://opencode.ai/install | bash
```

**Authentication.** opencode bundles access to multiple model providers
(DeepSeek, Anthropic, OpenAI, …) under a single "opencode Go" subscription
credential written by `opencode auth login`. The subscription auth file is
the only supported credential path; there is intentionally no API-key
side-channel (per the brief's "Don't do" rule — provider-specific keys
like `DEEPSEEK_API_KEY` are NOT honoured).

Point `CODEYBOX_OPENCODE_AUTH_FILE` at the host file `opencode auth login`
writes (default `~/.local/share/opencode/auth.json`; verify with the CLI
on the host). CodeyBox watches the file, ships its raw bytes to the
sandbox as `OPENCODE_AUTH_JSON`, and the runner materialises them inside
the VM before invoking `opencode run`. Token rotations from the host CLI
are picked up without an orchestrator restart.

If `opencode auth login` writes its credential file somewhere other than
the XDG default, set `CODEYBOX_OPENCODE_AUTH_DEST` on the host to the
sandbox-side path opencode expects to find the file at. The default value
is the XDG path which appears to be opencode's current default but has
not been verified in this environment. Operator-trust boundary: the value
flows into the in-sandbox materialisation script as-is, so keep it under
`$HOME` and avoid pointing at `/etc/*` or symlinks unless you intend to
overwrite the target.

**Default model.** The shipped appsettings default points opencode at
`deepseek-v4-flash`. DeepSeek is the differentiated capability opencode adds
over the other registered agents (Claude / Codex / Gemini already cover
Opus-class); DeepSeek's MoE economics fit the bulk audit-rework workload that
consumes Codex's weekly quota. **Confirm the exact model id** with
`opencode models` on the host and override `DefaultModelId` (or pin a specific
id per agent-class member via `ModelId`) to whichever DeepSeek variant the
operator's subscription tier surfaces as the best option.

**Multi-provider routing.** opencode can be slotted multiple times into
the same agent class with different `ModelId` values — `deepseek-v4-flash` as
the bulk-volume cheap-tokens member, `anthropic/claude-sonnet-4-6` as a
top-shelf fallback for items the DeepSeek path can't carry, etc. This
turns opencode into a redundant high-quality fallback path that survives
single-provider outages.

**Reasoning effort.** The CLI flag opencode uses for reasoning has not
been verified in this environment. The runner reads
`OPENCODE_REASONING_FLAG` from the host — set it to the flag name (e.g.
`--reasoning-effort`) confirmed via `opencode run --help` and the
runner will append the configured `ReasoningMode` after it. When the env
var is unset the runner drops `ReasoningMode` rather than guessing.

**Quota probe.** Ships as Unknown-only at integration time: opencode's
subscription metering shape has not been verified. The router's
`QuotaUnknownPolicy` (default `UseObservedFailures`) gates dispatch via
observed failure history until a real probe endpoint is wired.

**Smoke probe.** Credential-presence check only (no network call); see
the per-agent probe table below.

**Model-list probe.** Runs `opencode models` on the API host (operator must
install the CLI there) and validates `ModelId` values at startup via
`AgentClassConfigValidator`. Set `CODEYBOX_OPENCODE_BINARY` to override the
`opencode` binary path. When the CLI is missing, validation is skipped with
a warning.

### Google Gemini CLI (`@google/gemini-cli`)

**Install in the sandbox image:**
```sh
npm install -g @google/gemini-cli
```
Node.js is already on the baseline image (installed for the Claude CLI), so
adding the Gemini CLI costs only one extra `npm install -g` line in the
selected provider's `ExtraRuncmd` configuration.

**Credential:** set `CODEYBOX_GEMINI_API_KEY` on the orchestrator host to your
[Google AI Studio API key](https://aistudio.google.com/app/apikey) (format
`AIza…`). The orchestrator injects it as `GEMINI_API_KEY` inside the sandbox,
which is the env var the Gemini CLI reads.

**Non-interactive invocation:** `gemini --yolo -p "<prompt>"`

- `--yolo` skips all tool-use confirmation prompts (analogous to Claude's
  `--dangerously-skip-permissions`; appropriate inside the VM where the host
  boundary is the real permission boundary).
- `-p` delivers the prompt in a single non-interactive turn and exits.

**Model selection:** pass `ModelId` in the agent-class config to select a
specific Gemini model:
```json
{ "Agent": "gemini", "Billing": "PayPerApi", "ModelId": "gemini-2.5-pro" }
```
When `ModelId` is omitted the CLI uses its own default.

**Quota probe:** `GeminiQuotaProbe` queries the Code Assist endpoint
`POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota` using
the OAuth `access_token` from `~/.gemini/oauth_creds.json` (refreshed by
running `gemini` once). Per-bucket `remainingFraction` values are clamped to
0-100% and aggregated by min (most-restrictive bucket wins). `Subscription`
billing is supported; for plain API-key billing use `PayPerApi`:
```json
{ "Agent": "gemini", "Billing": "PayPerApi", "ModelId": "gemini-2.5-pro" }
```

**Reasoning level:** Gemini CLI 0.40+ has no `--thinking` / `--reasoning` /
`--effort` flag. The thinking budget is encoded in the model preset:
`gemini-3-*-preview` (e.g. `gemini-3-flash-preview`, `gemini-3-pro-preview`)
extends `chat-base-3` which sets `thinkingLevel: HIGH`; `gemini-2.5-*` uses
the default budget. To get "max reasoning", pick a `gemini-3-*-preview`
model id — `ReasoningMode: "high"` on the agent-class member is informational
only for Gemini and does not change the invocation.

**Vertex AI / service-account auth:** the Gemini CLI also accepts Application
Default Credentials (ADC). If you prefer service-account auth over an API key,
set `GOOGLE_APPLICATION_CREDENTIALS` to the path of your service-account JSON.
This requires a custom `ICredentialProvider` that materialises the JSON into
the sandbox via `AgentCredential.Files` — the `Files` map on `AgentCredential`
is designed for exactly this use case.

**Intermittent unavailability — why every Gemini call may exit 1:**
the Gemini CLI uses exit code `1` for every failure shape — quota, expired
OAuth refresh token, network/TLS errors, an unknown `--model` id, an
invalid argv, even an unrecognised sandbox flag. Reading the exit code
alone is uninformative, so `GeminiAgentRunner` now appends a single-line
stderr (or stdout, when stderr is empty) tail to the failure summary —
operators see e.g.
`agent exited 1: RESOURCE_EXHAUSTED quota exceeded for gemini-3-flash-preview`
on the work item's `lastError` instead of a bare `agent exited 1`. The
full stderr is still preserved on `AgentResult.Stderr` and in the audit
log; only the surfaced summary is capped (~240 chars).

Common failure shapes and what to check:

- `RESOURCE_EXHAUSTED` / `quota exceeded` / `exhausted your capacity` —
  genuine quota. `GeminiQuotaFailureDetector` classifies these as
  rate-limit / limit-reached, and the orchestrator marks the member
  exhausted for an hour so subsequent pickups skip it. To shorten that
  window, wait for the reset hint embedded in the stderr (e.g.
  `reset after 13m`) or rotate to a different model id.
- `API Error: 401` / `invalid_grant` / `Token has been expired or revoked` —
  the OAuth refresh token in `~/.gemini/oauth_creds.json` is no longer
  valid (Google rotates these aggressively; an idle account can lose its
  token in days). **Re-auth out of band on the orchestrator host:** run
  `gemini` once interactively (or `gemini auth login` for non-interactive
  flows), complete the Sign-in-with-Google flow in your browser, and
  confirm the file has been rewritten:
  ```sh
  ls -l ~/.gemini/oauth_creds.json
  ```
  If you've pointed CodeyBox at a non-default path via
  `CODEYBOX_GEMINI_OAUTH_FILE`, refresh that file instead. The
  orchestrator re-reads the file on every pickup, so re-auth propagates
  without a restart.
- Unknown / typo'd `ModelId` (e.g. `gemini-3.1-flash-lite` when the
  catalog only ships `gemini-3-flash-preview`) — the CLI exits 1
  without a clear marker. Check the model against
  `GeminiKnownModels.All`; the agent-class validator warns at startup
  but does not reject unknown ids.
- Bare `agent exited 1` with no appended tail — both stderr and stdout
  were empty. Investigate the sandbox image: a missing or non-executable
  `gemini` binary on `$PATH` produces this shape, as does a corrupt
  `~/.gemini/settings.json` that the CLI rejects before emitting
  diagnostics.

If quota or auth is recurrent and Gemini contributes no Done items, the
quickest mitigation is to move Gemini to a higher index in the
agent-class members list (or drop it entirely) until the underlying
cause is resolved; the persistent observed-failure store will
automatically gate the agent for `ObservedFailureWindow` after the first
classified exit-1, but a configuration change is the only durable fix
when re-auth is required.

### Cursor CLI (`agent`)

> **HARD CONSTRAINT — never invoke in fast mode.**
> Cursor's fast mode burns ~6× more credits for the same output with no
> parallelism-relevant speed benefit. This pipeline optimises for throughput,
> not per-iteration latency. `CursorAgentRunner.BuildInvocation` **never**
> emits `--fast` or any equivalent flag, and the
> `CursorAgentRunner_FastModeRegressionTests` fixture pins this. **Do not
> add a fast-mode toggle**; if a future Cursor release flips the default to
> fast-by-default the runner must explicitly opt out. Any proposal to expose
> a fast-mode option must be evaluated against the 6× cost penalty in writing.

**Binary name:** the Cursor CLI installs as `agent` (NOT `cursor-agent`).

**Install in the sandbox image:** follow Cursor's official install
instructions for your distro. For example, on the Ubuntu VM image used by
the VM providers:
```sh
curl -fsSL https://cursor.com/install | bash
```
Add the install command to `CodeyBox:MultipassExtraRuncmd` or
`CodeyBox:Incus:ExtraRuncmd`, matching the selected provider. The binary must
end up on `$PATH` as `agent`.

**Subscription auth setup:**

1. On the host (NOT in the sandbox image), run `agent login` once and
   complete the Cursor subscription auth flow. This writes a credentials
   file to disk (defaults to `~/.cursor/credentials.json`).
2. Point CodeyBox at it with `CODEYBOX_CURSOR_AUTH_FILE=/path/to/credentials.json`
   (or leave unset to use the default path).
3. The orchestrator reads the file on every pickup (rotations propagate
   without restart) and ships its contents into the sandbox via the
   `CODEYBOX_CURSOR_AUTH_JSON` env var; `CursorAgentRunner` materialises a
   private copy at `~/.cursor/credentials.json` inside the VM before
   invoking the CLI.
4. The host's credential directory is **not** bind-mounted into the agent
   sandbox; only the file contents flow through (same pattern as Codex's
   `~/.codex/auth.json` handling).

**Non-interactive invocation:** `agent --print --model composer-2.5`
(the prompt is delivered on stdin, not as a positional argv, so audit-
finding prompts that exceed Linux's 128 KiB MAX_ARG_STRLEN keep working).

**Default model:** `composer-2.5` — operator-graded as Opus-4.6-equivalent
quality. Override by setting `ModelId` on the agent-class member:
```json
{ "Agent": "cursor", "Billing": "Subscription", "ModelId": "composer-2.5", "QualityScore": 98 }
```

**Reasoning level:** the Cursor CLI does not currently expose a reasoning-
effort flag analogous to Claude's `--effort`. `ReasoningMode` on the
agent-class member is accepted (so the schema stays uniform across agents)
but is not threaded into argv. If a future Cursor release adds one, wire it
in `CursorAgentRunner.BuildInvocation`.

**Quota probe:** `CursorQuotaProbe` POSTs to Cursor's Connect-RPC
`DashboardService/GetCurrentPeriodUsage` on `api2.cursor.sh`, using the
`accessToken` from `~/.config/cursor/auth.json` (same credential bundle as
the runner). Overall availability is
`100 - max(planUsage.totalPercentUsed, planUsage.autoPercentUsed, planUsage.apiPercentUsed)`
— the most-constrained dimension wins, so the router floor gates cursor as
soon as any single axis is exhausted. Explicit out-of-usage signals
(`remainingBonus==false && totalSpend>=limit`, `displayMessage` matching
`/hit your .*usage limit/i`, or `enabled==false`) override the percent-
derived headline to a hard 0%, so a partial response with a missing percent
field still gates correctly. `billingCycleEnd` (an epoch-MILLISECONDS
string) becomes `ResetAt`; the cycle is monthly. Per-model routing uses
`autoBucketModels` plus `autoPercentUsed` (composer-* automatic models).
When a member has `ModelId` set but that id is absent from the parsed
buckets, the probe reports `AvailablePct=-1` so the router applies its
unknown policy rather than falling open on the global percentage.
Results are cached for `QuotaCacheTtlSeconds` (default 60s). Token refresh
is file-driven (no OAuth refresh helper yet); transient HTTP failures can
pin unknown until cache expiry or `TokenUpdated` invalidates the cache.
`CursorQuotaFailureDetector` still classifies dispatch-time limit signals.

**Smoke probe:** the Cursor smoke probe performs a credential-bundle
presence check (it verifies that `CODEYBOX_CURSOR_AUTH_JSON` is set);
authoritative credential validation happens on the first real CLI call,
where any `401 Unauthorized` is classified by
`CursorQuotaFailureDetector`.

**Billing flip:** the agent-class member's `Billing` field accepts
`Subscription` (default) or `PayPerApi`, mirroring Gemini. Cursor's
pay-per-api surface is undocumented at the time of writing; treat
`PayPerApi` as a forward hook.

### Google Antigravity CLI (`agy`)

Antigravity is Google's successor to `gemini-cli`. Gemini Code Assist (the
subscription `gemini-cli` rides) is being sunset 2026-06-18; the `agy`
binary is the official replacement and exposes a multi-model gateway —
Gemini, Anthropic Claude, and OpenAI GPT-OSS models all ride a single
Google AI subscription quota. The runner is registered as **light-duty
overflow**, not a workhorse: AI Pro caps requests on a weekly window with
up to a 7-day lockout on cap breach, so over-use is especially expensive.

**Binary name:** `agy`. **Provision through the selected provider's explicit
executable list — `CodeyBox:MultipassExecutableProvisions` or
`CodeyBox:Incus:ExecutableProvisions` — not an `ExtraRuncmd` installer.** The previously documented installer at
`https://antigravity.google/cli/install.sh` no longer serves a shell script —
as of 2026-06-17 it returns the Antigravity landing page (HTTP 200,
`Content-Type: text/html`). Piping HTML into `bash` exits 2; with a trailing
`|| true`, the runcmd reports success and the baseline is sealed without
`agy`. Subsequent dispatches to antigravity fail `agy: command not found`
(exit 127). The current self-update endpoint is only reachable from an
already-installed agy binary, so there is no usable `curl|bash` line to put
into runcmd. Stage a vetted copy of the binary on the host and ship it into
the baseline at bake time:

```jsonc
// settings.json
{
  "CodeyBox": {
    "MultipassExecutableProvisions": [
      {
        "HostSourcePath": "/home/<operator>/.codeybox/agy-seed/agy",
        "VmDestPath": "/home/ubuntu/.local/bin/agy",
        "VmSymlinks": ["/usr/local/bin/agy"],
        "Label": "antigravity"
      }
    ]
  }
}
```

For Incus, the same vetted host binary is configured explicitly under the
Incus provider instead; there is no fallback from the Multipass list:

```jsonc
// settings.json
{
  "CodeyBox": {
    "Incus": {
      "ExecutableProvisions": [
        {
          "HostSourcePath": "/home/<operator>/.codeybox/agy-seed/agy",
          "VmDestPath": "/home/ubuntu/.local/bin/agy",
          "VmSymlinks": ["/usr/local/bin/agy"],
          "Label": "antigravity"
        }
      ]
    }
  }
}
```

The selected provider copies the host file into the VM and installs it with
mode 0755 and deterministic root ownership. The `/usr/local/bin/agy` symlink
puts `agy` on the non-login sandbox PATH. Baseline provisioning verifies `agy --version` for
configured Antigravity members before the image is marked ready to clone,
so a missing/broken host binary fails the bake loudly instead of surfacing
as dispatch exit 127. Hot-reloadable via the existing `IOptionsMonitor`
plumbing; changing the host path or symlinks invalidates that provider's
content-addressed baseline via its hash.

**Non-interactive invocation:** `agy --print --dangerously-skip-permissions
--model <gateway-model-id>` with the prompt on stdin (the sandbox is the
real permission boundary; argv-via-stdin avoids the 128 KiB MAX_ARG_STRLEN
ceiling for big rework prompts).

**Resume:** the runner emits `--conversation <id>` when a checkpoint
captured a specific conversation id (`agy-conversation:<id>` ref) and
falls back to `--continue` (most recent conversation) otherwise — same
shape as `claude --resume`.

**Multi-model gateway — one membership per model.** Each gateway model
(`gemini-3.5-flash-high`, `claude-opus-4-6-thinking`,
`gpt-oss-120b-medium`, …) is its own request bucket on Google's side. The
router already keys exhaustion as `(AgentKind, ModelId)`, so the natural
design is one `AgentClass` member per accepted model:

```json
{
  "Id": "google-gateway",
  "Members": [
    { "Agent": "antigravity", "Billing": "Subscription",
      "ModelId": "gemini-3.5-flash-high", "QualityScore": 70 },
    { "Agent": "antigravity", "Billing": "Subscription",
      "ModelId": "claude-opus-4-6-thinking", "QualityScore": 85 }
  ]
}
```

The probe gates each member on its own quota and the router fails over
model-by-model. **Do not** introduce a separate "sub-subscription pool"
subsystem — the existing per-model exhaustion key already gives the pool
semantics for free.

**Auth setup:** Sign-in-with-Google. On the host, complete the agy OAuth
sign-in once. agy stores the token in the system keyring (Secret Service);
extract it (service `gemini`, username `antigravity`) into a file — its native
shape is `{"auth_method":…,"token":{"access_token":…,"refresh_token":…,"expiry":…}}`.
Point CodeyBox at that file's contents via one of:

- `CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON` env var carrying the JSON inline.
  Read **once** at process launch, so a keyring re-dump requires an
  orchestrator restart. Convenient when the supervisor reads the keyring at
  service start and never rotates.
- A per-instance `AgentCredentialReference.FilePath` on the antigravity
  `AgentMembership` (set via `CredentialFilePath` on the agent-class member
  or agent instance in config). Read **fresh on every dispatch** by
  `AgentInstanceCredentialResolver`, so an out-of-band re-dump of the file
  (e.g. by a periodic keyring → file refresher) is picked up on the next
  antigravity dispatch with no restart. Use this when the keyring token
  rotates and the supervisor can re-dump at intervals shorter than the
  refresh-token lifetime.

The runner materialises the bundle to
`~/.gemini/antigravity-cli/antigravity-oauth-token` inside the sandbox at
prepare-time with `chmod 600` — agy's `fileTokenStorage` path, used when no
system keyring is present (every headless sandbox). The refresh token **is**
shipped (verbatim): agy's access token is short-lived (~1h) and the in-VM agy
has no other refresh path, so it must self-refresh. This does not race the host
CLI, which authenticates from the keyring (a separate store) — unlike
Claude / Gemini, whose refresh tokens are stripped.

**Quota probe:** `AntigravityQuotaProbe` shares the `cloudcode-pa`
endpoint family with `GeminiQuotaProbe`. It prefers
`:retrieveUserQuotaSummary` (cleaner per-window/tier data than the
per-model-fragmented `:retrieveUserQuota`), falls back to
`:retrieveUserQuota`, and finally to a per-model `:generateContent` live
ping (same approach as Gemini — the bucket reading can report 100% while
a live call returns 429). The probe surfaces structured
`quota_metadata.lockout_until` timestamps when present, so a 7-day weekly
lockout pins `ResetAt` to the exact reset moment and the work item parks
in `WaitingForQuotaReset` until then instead of churning.

**Quota failure detector:** recognises `RESOURCE_EXHAUSTED`, the rendered
Google-API 429 message `Resource has been exhausted (e.g. check quota).`
(agy logs the human-readable message form, which carries neither the
screaming-snake status token nor the phrase `quota exceeded`),
`quota exceeded`, `weekly limit reached`, `account locked until …`, and
the structured `quota_metadata.lockout_until` envelope. Distinguishes
hard weekly lockouts (`QuotaFailureKind.LimitReached`) from transient
rate-limits so the orchestrator can park items long-term when the cap is
hit, not just bench-and-retry.

**Quota story is volatile.** Google has changed the AI Pro request cap
at least four times in four months. CodeyBox does NOT hardcode quota
sizes — the probe reads live state, the failure detector reads the
gateway's reset time, and operators tune `QuotaRouter:MinQuotaPct` /
`MaxConcurrent` per member as the cap evolves. Suggested seed: light
`MaxConcurrent` (1–2 per member) and modest `QualityScore` so the router
treats Antigravity as overflow behind paid Claude/Codex primaries.

**Cost reporting:** the cost extractor accepts both NDJSON shapes the
gateway emits (Anthropic-style `cache_creation_input_tokens` /
`cache_read_input_tokens` for the claude-backed models;
Gemini-style `cached_input_tokens` / `prompt_tokens` for the
gemini-backed models) and a human-readable footer fallback. Wire
per-gateway-model pricing via `CodeyBox:AgentPricing`.

**Reasoning level:** encoded in the gateway model id (each thinking-level
variant has its own canonical `--model` string), so
`AgentMembership.ReasoningMode` is informational only on this runner —
the same shape Gemini uses.

### CrockCode CLI (`crock`)

CrockCode (`github.com/AdamFrisby/CrockCode`) is an **asynchronous / batch**
coding agent: it submits work to Anthropic's Message Batches API (submit →
poll `crock status`, latency **minutes-to-hours**) rather than streaming a
synchronous session. It is registered as **light-duty overflow** and is not a
member of any shipped `AgentClass`; operators opt it in by wiring the items
below. The submit/poll wire shapes the runner parses are documented in
`CrockAgentRunner`/`CrockStatusParser`; they have not been verified against a
live binary in this environment, so treat model/reasoning plumbing and the
exact CLI flags as provisional (the runner logs and status parser are the
source of truth for what is actually accepted).

**Billing — pay-per-token, NOT a subscription.** CrockCode uses a real
Anthropic **API key** (`anthropic_api_key` in its config) billed per token at
the ~50% Message Batches discount. `CrockQuotaProbe` validates the key with a
token-free `GET /v1/models` (there is no per-key remaining-credit endpoint for
raw API keys); the spend gate lives in the budget provider. Rates are in
`agent-pricing-defaults.json` under the `crock` bucket (post-batch-discount
effective rates) plus a `DefaultRates.crock` Opus-tier fallback. Cost
attribution is an **estimate**, not exact spend: cache-write tokens are folded
into fresh input at the base rate (the Anthropic 1.25×/2× cache-write premium
is not represented, matching the `claude` bucket).

**Credential provisioning.** Stage the CrockCode `config.json` (containing the
API key) into the host env var **`CODEYBOX_CROCK_CONFIG_JSON`**;
`CrockEnvironmentCredentialProvider` ships it into the sandbox as
`CROCK_CONFIG_JSON` and the runner materialises it to
`~/.crockcode/config.json` (mode 0600) inside the VM. This is a host-env → in-VM
materialisation, the same shape every other agent uses — **the key is present
inside the ephemeral, internet-only sandbox** (it is not a "credential never
leaves the host" design). A per-instance member `CredentialReference` ships the
member's own config so quota routing and batch execution bill the **same** key.
The key never appears in any log line.

**Batch-latency liveness.** A crock item legitimately waits minutes-to-hours on
a batch. The default `WorkerProgressWatchdog:ProgressTimeout` (60 min) would
kill it, so seed a per-agent override under
`CodeyBox:WorkerProgressWatchdog:PerAgent:crock`
(`ProgressTimeout`/`ItemStaleTimeout`) — the shipped `appsettings.json` does
this. The poll loop also emits a per-poll progress chunk through the agent
stream; the watchdog reads each stream file's **last-activity** timestamp
(distinct from its immutable capture time) so a non-terminal poll counts as
progress against the override.

**Tunnel incompatibility → host-side daemon.** CrockCode's batch worker calls
back into local MCP tools via a public tunnel (cloudflared/ngrok). A public
tunnel inside CodeyBox's outbound-allow-list sandbox is incompatible with the
network model (see `CrockSandboxOptions` for the full rationale), so the
supported shape is a **host-side `crock daemon`** the sandbox submits to. Set
`CodeyBox:Crock:HostDaemonSocketPath` to an absolute socket path under a
directory **dedicated** to the daemon socket (e.g.
`/run/codeybox/crock-daemon.sock`). The credential provider bind-mounts that
socket's parent directory **read-only**; the path is canonicalised (`..`
segments and symlinks collapsed) and rejected if it resolves to a shared system
root, so a misconfiguration fails as an Infrastructure error rather than a
catastrophic host mount. Only sandbox providers that preserve a live local Unix
socket support this fallback. The daemon owns the tunnel + MCP tools and, if
configured with its own key, is what bills the batch.

## Credential smoke test

Before spending sandbox resources on a work item, CodeyBox performs a
lightweight credential check (a "smoke test") to verify that the agent's API
key is valid and can authenticate. This catches stale or misconfigured
credentials before they waste expensive compute.

### When it runs

1. **At orchestrator startup** — all configured agents are probed in parallel.
   Failure is non-fatal: the orchestrator starts regardless. Failures emit an
   `agent.smoke_failed` webhook event and a structured audit log entry, so
   monitoring catches stale credentials early.

2. **At work-item pickup** — just before the sandbox is allocated.
   If the credential fails, the work item transitions to `Failed` immediately
   and the pipeline returns without ever starting the agent.

### Per-agent probe shape

| Agent | Endpoint probed | Auth header |
|-------|----------------|-------------|
| `claude` | `https://api.anthropic.com/v1/messages` | `Authorization: Bearer <oauth>` or `x-api-key: <api-key>` |
| `codex` | `https://api.openai.com/v1/chat/completions` | `Authorization: Bearer <api-key>` |
| `gemini` | `https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent` | `x-goog-api-key: <api-key>` |
| `copilot` | *(no probe)* — always passes | — |
| `cursor` | *(no HTTP probe — Cursor exposes no public usage endpoint)* — verifies the credential bundle carries `CODEYBOX_CURSOR_AUTH_JSON`; real auth check happens on first CLI call | — |
| `opencode` | *(no network call)* — credential-presence check only | `OPENCODE_AUTH_JSON` |

Each probe sends the minimal possible request (`max_tokens=1`). A 2xx response
means the credential is valid. 401/403 is classified as `"auth"` failure.
5xx and network errors are classified as `"transient: try later"` (cached
like any result, so a transient server error at startup won't permanently gate
work items).

### Cache semantics

Probe results are cached per `(AgentKind, credential fingerprint)` for
`CodeyBox:Smoke:CacheTtlMinutes` (default 15 minutes). The fingerprint is a
SHA-256 hash of all credential values; the raw token is never stored.

Changing the credential (e.g. rotating a key) produces a new fingerprint and
forces a fresh probe on the next pickup.

### Disabling smoke tests

**Globally** — set `CodeyBox:Smoke:Enabled=false`. This is the master smoke
switch: no startup probes, pickup credential gate, router smoke exclusions, or
in-VM dispatch smoke gate can block dispatch. The switch is hot-reloaded.

**Per-project** — set `SkipCredentialSmokeTest: true` in the project
configuration. This skips only the pickup-time credential probe for that
project; router smoke exclusions and in-VM smoke are still governed by
`CodeyBox:Smoke:Enabled`.

### Configuration reference

| Key | Default | Description |
|-----|---------|-------------|
| `CodeyBox:Smoke:Enabled` | `true` | Master switch for all smoke testing and smoke-based dispatch exclusions. |
| `CodeyBox:Smoke:CacheTtlMinutes` | `15` | How long to cache a probe result before re-probing. |
| `CodeyBox:Smoke:StartupTimeoutSeconds` | `10` | Per-agent timeout for the startup probe. |
