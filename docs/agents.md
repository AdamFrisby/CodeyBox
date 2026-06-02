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

Each agent CLI must be present in the sandbox baseline image, or dispatch fails
with exit 127 (`<binary>: No such file or directory`). The orchestrator does
**not** install agent binaries — that is operator-owned config under
`CodeyBox:MultipassExtraRuncmd` (see [`baseline-bake-examples.md`](baseline-bake-examples.md)).
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
| `antigravity` | `curl -fsSL https://antigravity.google/install \| bash` (verify against the upstream installer at bake time; the agy binary is proprietary closed-source). Installs the `agy` CLI on `$PATH`. | Multi-model gateway — each gateway model id is a separate quota bucket. Configure each accepted model as its own `AgentClass` member; the router gates per-model via the existing `(AgentKind, ModelId)` exhaustion key. See [Antigravity quirks](#google-antigravity-cli-agy). |

Verify each command against its upstream install docs at the time of baking —
versions and install URLs change. After updating
`CodeyBox:MultipassExtraRuncmd`, delete any cached `cb-baseline-*` images to
force a fresh bake on the next sandbox launch.

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

5. **Install the binary in the sandbox baseline image.** This is **not
   optional** — without it, every dispatch to the new agent fails with
   exit 127 (`<binary>: No such file or directory`). The orchestrator does
   not auto-install; the install line lives in operator config under
   `CodeyBox:MultipassExtraRuncmd` (see
   [`baseline-bake-examples.md`](baseline-bake-examples.md)). After
   editing operator config, delete the cached baseline image so the next
   sandbox launch re-bakes with the new tool. Pin by digest where the
   upstream supports it.

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
in. Config keys (bound from `CodeyBox:ClaudeSession`):

| Key | Default | Description |
|-----|---------|-------------|
| `CodeyBox:ClaudeSession:Enabled` | `false` | Master switch. When false, Claude work items always use the one-shot path. |
| `CodeyBox:ClaudeSession:EmitTurnMetrics` | `true` | Emit per-turn cache_read vs fresh-input metrics via `IClaudeSessionMetricsSink`. |
| `CodeyBox:ClaudeSession:Transport` | `print` | Command-delivery + billing channel. `print` = today's `claude --print --resume`. `acp` = Agent Client Protocol via `claude --ide` (OFF the metered `-p` pool). Case-insensitive, hot-reloadable. Invalid values fall back to `print`. |
| `CodeyBox:ClaudeSession:TransportOverridesByAgentClassMember:<member>` | (none) | Per-agent-class-member transport override. Wins over the per-project override and the global default. |
| `CodeyBox:ClaudeSession:TransportOverridesByProject:<projectId>` | (none) | Per-project transport override. Loses to the per-class-member override. |

#### Selecting a transport

`print` is the existing path: `claude --print --dangerously-skip-permissions
[--resume <id>]` per turn. Continuity comes from the captured Claude CLI
session id (passed back via `--resume`); cache warmth follows the server-side
prompt cache TTL.

`acp` runs the in-sandbox `ClaudeSessionWorker.AcpClaudeTransport` bridge:
CodeyBox materialises a tiny Node.js bridge inside the sandbox, the bridge
hosts a WebSocket on a random local port, writes an IDE lockfile at
`~/.claude/ide/<port>.lock` carrying `{transport:"ws", url, authToken,
workspaceFolders, ...}`, and spawns `claude --ide` so the agent connects to
the bridge. ACP JSON-RPC traffic (`initialize`, `session/new` or
`session/load`, `session/prompt`, streamed `session/update`s, `stopReason`)
flows host ↔ bridge stdio ↔ in-VM WebSocket ↔ claude. Permission requests
(`session/request_permission`) and input requests (`session/request_input`)
auto-grant / answer with a `<codeybox-question>` default so a headless ACP
turn never waits on a human. Session continuity uses the assigned ACP
session id, passed back via `session/load` on the next turn.

If the ACP transport fails to open or any turn raises
`AcpTransportUnavailableException`, the worker logs
`agent.claude_acp_transport_degraded` and falls back to the `print`
transport for the rest of that session — the work item is never stranded.
Per-turn metrics (`ClaudeSessionTurnMetrics`) carry the `Transport` tag
(`"print"` / `"acp"`) so dashboards can confirm traffic moved off the
metered pool.

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
re-attempting the failed resume). Reviving the handle in a fresh process
requires a sandbox-reattacher callback wired into the worker; the current
rollout step (item 2 of 3) leaves the production reattacher unwired and
the worker surfaces a clear `InvalidOperationException` on any reattach
attempt. The dispatch wiring that persists and replays the handle across
a restart lands in item 3.

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

### GitHub Copilot CLI
Reads `GH_TOKEN` from the environment. **Important**: a generic
`GH_TOKEN` grants the agent broad GitHub access, not just Copilot. Issue
a fine-grained token scoped to the minimum the agent needs, ideally one
that cannot push to your real repos. Sandbox network policy must
**not** include `github.com` for this token to be safe.

### OpenAI Codex CLI
Reads `OPENAI_API_KEY`. The `--full-auto` flag skips Codex's per-edit
confirmations — appropriate inside a sandbox.

### Opencode CLI (`sst/opencode`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd`. opencode publishes both an `npm`
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

**Default model.** The runner ships with `DefaultModelId` pointed at
`opencode-go/deepseek-v4-flash`. DeepSeek is the differentiated capability opencode
adds over the other registered agents (Claude / Codex / Gemini already
cover Opus-class); DeepSeek's MoE economics fit the bulk audit-rework
workload that consumes Codex's weekly quota. **Confirm the exact model id**
with `opencode models` on the host and override `DefaultModelId` (or pin a
specific id per agent-class member via `ModelId`) to whichever DeepSeek
variant the operator's subscription tier surfaces as the best
option.

**Multi-provider routing.** opencode can be slotted multiple times into
the same agent class with different `ModelId` values — `opencode-go/deepseek-v4-flash`
as the bulk-volume cheap-tokens member, `anthropic/claude-sonnet-4-6` as a
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
adding the Gemini CLI costs only one extra `npm install -g` line in
`MultipassExtraRuncmd`.

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
instructions for your distro. For example, on the Ubuntu baseline used by
the multipass provider:
```sh
curl -fsSL https://cursor.com/install | bash
```
Add the install command to `CodeyBox:MultipassExtraRuncmd`. The binary must
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

**Binary name:** `agy`. Install via the upstream installer command and
verify the binary lands on `$PATH` after baking.

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

**Quota failure detector:** recognises `RESOURCE_EXHAUSTED`,
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
