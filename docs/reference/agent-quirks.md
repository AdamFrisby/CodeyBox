# Per-agent quirks

Everything specific to one agent CLI: binary name, non-interactive invocation,
credential layout, reasoning flags, quota probes, and the traps each one sets.
Read the relevant section before adding that agent to a class.


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

Install with `npm install -g @github/copilot`. Everything below was verified against **v1.0.82**.

**`--allow-all-tools` is required for non-interactive mode** — the CLI's own words. Without it a
`-p` run blocks on a permission prompt nothing will answer. The runner also passes
`--allow-all-paths` (every CodeyBox run is sandboxed, where Copilot's own path check guards nothing
the VM does not while still interrupting constantly), but deliberately **not** `--allow-all-urls` or
`--allow-all`: network egress is a different boundary, governed by the sandbox network profile.

**`--model <id>` exists** (an earlier comment in this runner claimed it did not) and is the only
thing that selects the wire model in `-p` mode. `COPILOT_MODEL`, `COPILOT_PROVIDER_MODEL_ID` and
`COPILOT_PROVIDER_WIRE_MODEL` do **not** change it — captured at an instrumented endpoint, all three
left the wire model at Copilot's session default.

**No stdin prompt mode.** The prompt is an argv element (`-p <text>`); both `-p -` and a bare
invocation ignore stdin. Unlike the agy/codex/gemini runners this one cannot dodge the 128 KiB
`MAX_ARG_STRLEN`, so a very large rework prompt can still surface as exit 126.

#### BYOK (bring your own key)

Setting a base URL points inference at any OpenAI-compatible endpoint, with no GitHub account
involved in inference. Copilot exposes this axis **only** through the environment — there are no
argv flags — so CodeyBox renders it from `CodeyBox:Copilot`:

| Variable | Meaning |
| --- | --- |
| `COPILOT_PROVIDER_BASE_URL` | **Activates BYOK**; every other variable is inert without it. Copilot appends `/chat/completions`, so include the version segment (`http://model-host:11434/v1`). |
| `COPILOT_PROVIDER_TYPE` | `openai` (covers Ollama, vLLM, llama.cpp), `azure`, `anthropic` |
| `COPILOT_PROVIDER_API_KEY` / `..._BEARER_TOKEN` | Bearer wins inside Copilot — set one |
| `COPILOT_PROVIDER_WIRE_API` | `completions` or `responses` |
| `COPILOT_PROVIDER_TRANSPORT` | `http` or `websockets` (websockets only with `responses`) |
| `COPILOT_PROVIDER_HEADERS` | newline-separated `Name: Value` — embed `{{codeybox.session_id}}` in a value for a fresh UUID per invocation (also exported as `CODEYBOX_COPILOT_SESSION_ID` for diagnosis; never log the headers themselves) |
| `COPILOT_OFFLINE` | no GitHub auth/telemetry/web tools/GitHub MCP/auto-update. **Requires a provider**, so CodeyBox emits it only alongside one. |

The credential is **not** a config value: it arrives through the credential chain as
`CODEYBOX_COPILOT_PROVIDER_API_KEY` → `COPILOT_PROVIDER_API_KEY`, so the secret never sits in a
config file.

**Per-member providers.** `CodeyBox:Copilot:Providers:{name}` holds named BYOK
entries a class member selects with its `Provider` reference; a member with no
reference keeps using the agent-global `CodeyBox:Copilot:Provider`. The runner
renders each invocation from the member's effective provider, so a BYOK
harness member and a native-subscription member (distinct `InstanceId`s) can
coexist on one CLI. A member naming an entry that does not exist fails
configuration validation at startup — it never silently falls back to another
backend.

**`apply_patch` breaks strict servers.** Copilot offers it as an OpenAI *custom* tool with a Lark
grammar (`"type":"custom"` rather than `"type":"function"`), and a server implementing only function
tools rejects the **whole** tools array — an llama.cpp-backed endpoint answers
`Failed to parse tools: Unsupported tool type` with HTTP 500 and no turn can start. So
`ExcludedTools` defaults to `["apply_patch"]` whenever a provider is configured; set it to `[]` to
opt out.

Whether the custom tool is sent at all depends on the `--model` id, which also selects
`reasoning_effort`:

| `--model` | `reasoning_effort` sent | tool types sent |
| --- | --- | --- |
| a local model's own name (unrecognised) | omitted | `function` only |
| a well-known id (`gpt-5.6-*`) | e.g. `medium` | `custom` + `function` |

So naming the local model directly is the safe choice, and the `apply_patch` exclusion is harmless
there and necessary with a well-known id — which is why it defaults on rather than being conditioned
on the id. Note this differs from older guidance written against v1.0.81, which advised substituting
a well-known id; on 1.0.82 that *introduces* the custom-tool breakage instead of avoiding it.

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

#### Invocation: `--print` is unusable; use stream-json (verified agy 1.1.24 and 1.1.26)

`--print` is a **string** flag, so a bare `--print` swallows the next argv element as its prompt.
The shape this runner used until 2026-09-05 — `[agy, --print, --dangerously-skip-permissions]` with
the prompt on stdin — therefore ran with the literal prompt `--dangerously-skip-permissions`,
discarded the real prompt, and left permissions un-skipped. The CLI says so:

```
Error: --print took "--dangerously-skip-permissions" as its prompt, so the intended prompt was
left as an argument and ignored.
```

…and then **exits 0**. A totally failed run was therefore indistinguishable from a successful one and
surfaced downstream as "produced no changes". Moving `--print` last does not help either
(`flag needs an argument: -print`, also exit 0).

Attaching the prompt to the flag (`--print='…'`) is not an option here: rework prompts carrying audit
findings exceed Linux's 128 KiB `MAX_ARG_STRLEN`, which is why the prompt is on stdin.
`--input-format stream-json` selects non-interactive mode on its own, keeps the prompt on stdin, and
is the only shape that does both. It requires `--output-format stream-json`, so **structured output
is mandatory, not optional**. One NDJSON frame per turn on stdin:

```json
{"event":"user","message":{"role":"user","content":[{"type":"text","text":"…"}]}}
```

The envelope key is `event`, **not** `type` — agy's stream-json resembles Claude Code's and is not it;
a Claude-shaped line is rejected with `stream input message is missing the "event" field`. Output
frames are `init` (carries `conversation_id`, `cwd`, the tool list and `permission_mode`),
`step_update`, and one `result` per turn.

#### `--add-dir` must be absolute

Without a workspace, a clean guest can silently drop file writes: agy reports `SUCCESS` while the
working tree is untouched. Pass `--add-dir <absolute working directory>`. The path **must** be
absolute — verified against 1.1.26, `--add-dir .` also reported `SUCCESS` and wrote the file
*nowhere at all*: not the working directory, not `~/.gemini/antigravity-cli/scratch/`. The runner
therefore omits the flag entirely rather than ever passing a relative path.

#### Model ids are not display names

`agy models` prints `id<TAB>Display Name`. `--model` takes the **id** (`gemini-3.8-flash-high`), not
the display name (`Gemini 3.8 Flash (High)`). Reasoning effort is encoded in the id (`-high`,
`-medium`, `-low`) rather than passed separately. The gateway's catalogue moves independently of the
CLI version, so `AntigravityKnownModels` goes stale silently — it named `gemini-3.5-flash-*` long
after the gateway had delisted it for 3.6/3.7/3.8, and after Sonnet's id dropped its `-thinking`
suffix. `agy models` is the authority.

---

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

### Pi coding agent (`pi`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider (verified against pi 0.85.1, 2026-09-14):

```sh
npm install -g --ignore-scripts @earendil-works/pi-coding-agent
```

MIT-licensed ([repo](https://github.com/badlogic/pi-mono/tree/main/packages/coding-agent),
npm `@earendil-works/pi-coding-agent`). Lean terminal agent (Read/Write/Edit/Bash
tools) fronting 30+ providers through its pi-ai layer.

**Non-interactive invocation.** The runner drives `pi --mode json` with the
prompt on stdin and NO positional prompt argument:

```sh
pi --mode json --no-session --offline [--model <id>] [--thinking <level>]
```

`--mode json` was chosen over raw `-p` and `--mode rpc`: `-p` prints only the
final response text (usage, dispatch model, and terminal error shape would be
unrecoverable), while `--mode rpc` is a bidirectional prompt/response protocol
needing a driver loop for no extra signal on a one-shot run. `--mode json`
exits after the run like `-p` but emits one JSON event per stdout line, with
cumulative `usage {input, output, cacheRead, cacheWrite, totalTokens}` and
`model` on the assistant message frames. `--no-session` skips persisting
`~/.pi/agent/sessions` (the VM is ephemeral); `--offline` disables pi.dev
startup network (version checks, telemetry). The runner deliberately passes
NEITHER `--approve` NOR `--no-approve`: the sandbox tree is untrusted, so the
`ask` default (ignore project resources) is the safe posture.

**Exit-zero errors.** Pi exits 0 even when the run dies before producing
output (verified: missing API key and a provider 401 both exit 0 with the
cause only in the event stream). The runner lifts the terminal error
(`stopReason: "error"` + `errorMessage` on the message frames, or the
plaintext `No API key found for the selected model.`) into
`TerminalDiagnostic`, so the pipeline's no-changes branch parks quota/auth
give-ups instead of dead-lettering them as "produced no changes" — the same
shape `agy` has.

**Authentication.** Provider API keys from the environment (`ANTHROPIC_API_KEY`,
`OPENAI_API_KEY`, `GEMINI_API_KEY`, … — full table in pi's `providers.md`).
The shipped credential mapping wires host `CODEYBOX_PI_API_KEY` to
sandbox-side `ANTHROPIC_API_KEY`; operators fronting other providers add that
provider's variable to the mapping. Interactive `/login` state is not shipped
into sandboxes. Prefer `provider/id`-qualified `ModelId` values
(e.g. `anthropic/claude-haiku-4-5`): pi's default provider is google, so a bare
id can resolve against the wrong catalog.

**Reasoning effort.** `ReasoningMode` maps 1:1 onto `pi --thinking`
(`off|minimal|low|medium|high|xhigh|max`). Only exact allowlist members are
emitted; anything else is ignored rather than passed through to fail the CLI.

**Quota probe.** Ships as Unknown-only: pi has no meterable quota endpoint
(it fronts 30+ providers). The router's `QuotaUnknownPolicy` (default
`UseObservedFailures`) gates dispatch via observed failure history, and
`PiQuotaFailureDetector` classifies the relayed provider errors (401/auth
shapes → Unauthorized; 429/rate-limit → RateLimitExceeded; 402/billing →
LimitReached) with operator-extensible rows under
`CodeyBox:QuotaFailurePatterns:pi`.

**Smoke probes.** Host-side `PiSmokeProbe` is a credential-presence check only
(no network call — no single endpoint validates a multi-provider credential).
`PiInVmSmokeProbe` execs `pi --version` plus a `pi --help | grep -q -- --mode`
assertion, so a pi build that dropped the JSON event stream benches at smoke
time instead of failing first dispatch.

**Model-list probe.** `pi --list-models` needs an authenticated provider plus
network, so the host-side probe returns the curated `PiKnownModels` seed
instead of live-reading the catalog. Operator `ModelId` values absent from the
seed surface as a startup warning, never a hard reject (pi accepts fuzzy and
provider-qualified ids beyond the seed).

**Cost attribution.** `PiCostExtractor` takes the latest (cumulative) `usage`
frame and the bare `message.model` id (pi strips the `provider/` qualifier in
`message.model`). Bundled rates live in `agent-pricing-defaults.json` under the
`pi` bucket for the shipped Anthropic-backed member; operators fronting other
providers add that provider's list prices there (or under
`CodeyBox:AgentPricing`) keyed by the bare model id.

### Aider (`aider`)

**Install in the sandbox image** — add the install lines to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider (verified against aider 0.86.2, 2026-09-16):

```sh
curl -fsSL https://aider.chat/install.sh | bash
uv tool install --python python3.12 aider-chat==0.86.2
```

Apache-2.0 ([repo](https://github.com/Aider-AI/aider), PyPI `aider-chat`).
The install script installs `uv` and then aider via
`uv tool install --force --python python3.12 --with pip aider-chat@latest`;
pin with `uv tool install aider-chat@<version>` for reproducible bakes. Needs
Python 3.12 on the image. Aider is the smallest integration surface of the
registered agents: no hooks, no session resume — a pure one-shot runner.

**Non-interactive invocation.** The runner drives aider's headless one-shot
form via its file twin, with the prompt on stdin and NO argv prompt:

```sh
aider --model <id> --message-file /dev/stdin [--yes-always --no-auto-commits ...]
```

`--message` / `--msg` / `-m` ("send one message, process the reply, then
exit — disables chat mode") is the documented one-shot flag; the runner uses
`--message-file /dev/stdin` instead so the rework prompt travels on stdin
rather than in one argv element (Linux `MAX_ARG_STRLEN` is 128 KiB per
element). Verified live: a piped-stdin message ran one-shot and produced
`Applied edit to …` plus the `Tokens: …` accounting line. Without either flag
aider waits on interactive input that never arrives in the sandbox. The runner
adds `--yes-always` (never prompt), `--no-auto-commits` (CodeyBox owns
commits — edits stay in the worktree for the pipeline to collect),
`--no-gitignore` (otherwise aider appends `.aider*` to the repo's
`.gitignore`, polluting the diff), `--analytics-disable`,
`--no-check-update`, `--no-show-release-notes` (no telemetry or self-update
stalls outside the sandbox allow-list), and `--no-pretty` (ANSI-free logs).
`--chat-history-file /dev/null --input-history-file /dev/null` redirect aider's
default repo-dir history files away so one-shot runs never pollute the worktree
diff (verified: `/dev/null` targets run normally and create no files).

**Exit-zero errors.** Aider exits 0 even when the run dies before producing
output (verified: a bad OpenRouter key exits 0 with only
`litellm.AuthenticationError: AuthenticationError: OpenrouterException -
{"error":{"message":"Missing Authentication header","code":401}}` on stdout,
followed by `The API provider is not able to authenticate you. Check your API
key.`). The runner lifts the first terminal error line into
`TerminalDiagnostic` via `AiderTerminalDiagnoser`, so the pipeline's
no-changes branch parks quota/auth give-ups instead of dead-lettering them as
"produced no changes" — the same shape `agy` and `pi` have.

**Authentication.** Provider API keys from the environment through aider's
litellm layer (`OPENROUTER_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`,
`GEMINI_API_KEY`, …); OpenRouter is supported natively with
`openrouter/<model>` ids. The shipped credential mapping wires host
`CODEYBOX_AIDER_API_KEY` to sandbox-side `OPENROUTER_API_KEY`; operators
fronting other providers add that provider's variable to the mapping.
`--env-file` / `.env` and `-c` / `.aider.conf.yml` exist for local use but the
sandbox path always uses the staged environment. Always configure an explicit
`--model` (via `CodeyBox:AgentDefaults[aider]` or the class member): aider's
own startup default is `gpt-4o`, which needs an OpenAI key the sandbox may not
carry. Aider model ids are `provider/id`-qualified; the shipped default is
`openrouter/anthropic/claude-haiku-4.5` (note the dotted form — `claude-haiku-4-5`
is unknown to aider's catalog and only triggers a "Did you mean" warning).

**Reasoning effort.** `ReasoningMode` is deliberately NOT mapped: aider
exposes both `--reasoning-effort` (reasoning_effort API parameter) and
`--thinking-tokens` (thinking budget), and which knob — and which value
vocabulary — is valid depends on the backing provider behind `--model`.
Emitting one unconditionally would fail dispatches for the other provider
family.

**Quota probe.** None — aider exposes no quota/credit meter (same position as
opencode). Availability is covered by the credential-presence host probe plus
observed failure history (`QuotaUnknownPolicy`, default
`UseObservedFailures`), and `AiderQuotaFailureDetector` classifies the relayed
litellm errors (`litellm.RateLimitError` / shared 429 rows →
RateLimitExceeded; `litellm.AuthenticationError` / 401 / missing-key rows →
Unauthorized; `insufficient_quota` / `insufficient credits` / 402 rows →
LimitReached) with operator-extensible rows under
`CodeyBox:QuotaFailurePatterns:aider`. Both stdout and stderr are scanned —
aider's one-shot output is stdout-first and exits 0 on failure.

**Smoke probes.** Host-side `AiderSmokeProbe` is a credential-presence check
only (`OPENROUTER_API_KEY` in the bundle — no network call). `AiderInVmSmokeProbe`
execs `aider --version` plus an `aider --help | grep -q -- --message`
assertion, so an aider build that dropped the one-shot message form benches at
smoke time instead of dispatching into an interactive wait.

**Model-list probe.** `aider --list-models <query>` needs a partial-match query
argument, prints an interactive OpenRouter onboarding prompt when no model or
key is configured, and emits thousands of rows, so the host-side probe returns
the curated `AiderKnownModels` seed instead of live-reading the catalog.
Operator `ModelId` values absent from the seed surface as a startup warning,
never a hard reject (aider routes any litellm `provider/id` beyond the seed).

**Cost attribution.** `AiderCostExtractor` parses aider's own accounting line
(`Tokens: <sent> sent[, <n> cache hit], <received> received.` with `k`
suffixes per aider's `format_tokens`) and the dispatch id from the
`Model: <id> with … edit format` run header. The `sent` counter already
includes cache-write tokens, and cache hits are split out of `sent` so the
calculator does not double count. The optional dollar `Cost:` trailer is
provider-billed spend, not a token count, and is not parsed. No output line
means unknown (null), never a zero that looks like data. Bundled rates live in
`agent-pricing-defaults.json` under the `aider` bucket for the shipped
OpenRouter-backed member, keyed by the full dispatch id; operators fronting
other providers add that provider's list prices there (or under
`CodeyBox:AgentPricing`) in the same qualified form.
### Goose CLI (`goose`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider (verified against goose 1.50.1, 2026-09-16):

```sh
GOOSE_VERSION=v1.50.1
GOOSE_INSTALLER=/tmp/goose-download_cli.sh
curl -fsSL -o "$GOOSE_INSTALLER" "https://github.com/aaif-goose/goose/releases/download/${GOOSE_VERSION}/download_cli.sh"
printf '%s  %s\n' "ab5ae40513348ec4e6047cc7338040aab2df5246800c111d22065766ba6013f0" "$GOOSE_INSTALLER" | sha256sum -c -
GOOSE_VERSION="${GOOSE_VERSION}" bash "$GOOSE_INSTALLER"
rm "$GOOSE_INSTALLER"
```

Apache-2.0 ([repo](https://github.com/aaif-goose/goose)). Shell installer for
macOS/Linux (Windows unsupported — irrelevant: CodeyBox sandboxes are Linux).
The installer script is pinned by release tag with its SHA256 verified before
execution (mirroring the gitleaks baseline entry), and `GOOSE_VERSION`
pins the binary download too — never `stable` for reproducible bakes. The installer drops the `goose` binary
(verified at `~/.local/bin/goose`); the baseline bake's `--version` check
catches a missing binary before first dispatch.

**Non-interactive invocation.** The runner drives a one-shot
`goose run` with the prompt on stdin and NO positional prompt argument:

```sh
goose run -i - --output-format stream-json --no-session [--provider <id>] [--model <id>] [--max-turns <n>]
```

`goose run -t "<text>"` without `-s/--interactive` processes the prompt and
exits — exactly the headless contract CodeyBox needs. (The
agent-orchestrator project deliberately forces `-t "" --interactive` because
it wants a human-steered session in a PTY; one turn is what CodeyBox wants,
so the runner drops both.) The prompt travels via `-i -` (stdin) rather than
`-t` argv: Linux's `MAX_ARG_STRLEN` is 128 KiB per argv element and rework
prompts can exceed it. `--output-format stream-json` is the runner's only
transport — even when the caller did not ask for structured capture — so
cost attribution, failure classification, and stream parsing never depend on
which call path dispatched the run. Plain `text` output carries no token
totals; whole-doc `json` is one pretty-printed blob, not a stream.
`--no-session` skips the session store (the VM is ephemeral).

**Approval mode.** Goose reads its approval mode from the `GOOSE_MODE`
environment variable (`auto / approve / chat / smart_approve`), not a CLI
flag. The runner always sets `GOOSE_MODE=auto`: the sandbox VM is throwaway
with no human to approve anything, so any approval prompt would hang or fail
the run. The turn loop is bounded by `--max-turns` from the hot-reloadable
`CodeyBox:Goose:MaxTurns` knob (default 100); the provider id comes from
`CodeyBox:Goose:Provider` (shipped as `openrouter`).

**Exit-zero errors.** Goose exits 0 even when the provider call fails
(verified: an OpenRouter 401 exits 0 with the cause only in a content
`type: "error"` block; a missing key exits 1 with plaintext
`error: Error Configuration value not found: OPENROUTER_API_KEY.`). The
runner lifts the terminal error into `TerminalDiagnostic`, so the pipeline's
no-changes branch parks quota/auth give-ups instead of dead-lettering them
as "produced no changes" — the same shape `agy` has.

**Authentication.** Thirteen provider API-key variables
(`GOOSE_API_KEY`, `GOOSE_PROVIDER__API_KEY`, `GOOSE_EDITOR_API_KEY`,
`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`,
`OPENROUTER_API_KEY`, `DEEPSEEK_API_KEY`, `GROQ_API_KEY`, `XAI_API_KEY`,
`MISTRAL_API_KEY`, `COHERE_API_KEY`) plus `~/.config/goose/config.yaml` and
`secrets.yaml`. The shipped credential mapping wires host
`CODEYBOX_GOOSE_API_KEY` to sandbox-side `OPENROUTER_API_KEY`; operators
fronting other providers add that provider's variable to the mapping. The
guest also needs `openrouter.ai` on `CodeyBox:AgentAllowedHosts` (shipped in
the default) for the OpenRouter route.

**Quota probe.** Ships as Unknown-only: goose exposes no meterable quota
endpoint (it fronts 30+ providers), so no probe is registered and members
fall through to the `NullQuotaProbe` unknown path. The router's
`QuotaUnknownPolicy` (default `UseObservedFailures`) gates dispatch via
observed failure history, and `GooseQuotaFailureDetector` classifies the
relayed provider errors (401/auth shapes → Unauthorized; 429/rate-limit →
RateLimitExceeded; 402/billing → LimitReached) with operator-extensible rows
under `CodeyBox:QuotaFailurePatterns:goose`.

**Smoke probes.** Host-side `GooseSmokeProbe` is a credential-presence check
only (no network call — no single endpoint validates a multi-provider
credential, and any provider call would spend real quota). No network probe
runs for goose for the same reason. `GooseInVmSmokeProbe` execs
`goose --version` plus a `goose run --help | grep -q -- --output-format`
assertion, so a goose build that dropped the JSONL event stream benches at
smoke time instead of failing first dispatch.

**Model-list probe.** The catalog is per provider and server-side, so the
host-side probe returns the curated `GooseKnownModels` seed instead of
live-reading it. Operator `ModelId` values absent from the seed surface as a
startup warning, never a hard reject (goose accepts any provider-native id
via `--model`).

**Cost attribution.** `GooseCostExtractor` takes the latest terminal
`type: "complete"` totals frame (`input_tokens`, `cache_read_input_tokens`,
`output_tokens`) and the dispatch model id from
`message.metadata.inference.requestedModel` (e.g.
`nvidia/nemotron-3.5-lightning:free`). All-zero totals (the shape a failed
run emits) yield null rather than a zero-token snapshot. No fallback rate is
shipped — goose fronts 30+ providers with unrelated economics — but the
bundled `goose` bucket in `agent-pricing-defaults.json` records the shipped
free-tier model's explicit $0 rate (verified live: the CLI's own complete
frame reports `cost_usd` 0.0). Operators fronting paid models add that
model's list prices there (or under `CodeyBox:AgentPricing`) keyed by the
requested-model id.

### Prime Agent (`prime-agent`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider (verified against prime-agent 0.9.5, 2026-09-16):

```sh
curl -fsSL https://app.primeintellect.ai/prime-agent/install.sh | sh
```

The installer drops a self-contained `prime-agent` binary on PATH (no
Node/Python runtime needed) and resolves the latest stable release unless
`PRIME_AGENT_VERSION` is set — pin with
`PRIME_AGENT_VERSION=0.9.5 curl -fsSL … | sh` for reproducible bakes. Prime
shares lineage with pi (same `--mode json` event vocabulary, same usage
shape) but is a separate CLI with its own provider wiring.

**Non-interactive invocation.** The runner drives the documented headless
form with the prompt on stdin and NO positional prompt argument:

```sh
prime-agent -p --mode json --no-session --offline --provider <id> [--model <id>] [--thinking <level>]
```

`-p` ("Print response and exit") is the one-shot contract — without it
prime-agent enters the interactive TUI and waits forever in the sandbox.
`--mode json` keeps the JSON event stream (cumulative
`usage {input, output, cacheRead, cacheWrite, totalTokens}` and `model` on
the assistant message frames); bare `-p` prints only the final text and
`--mode rpc` needs a driver loop, so neither is used. `--no-session` skips
persisting `~/.prime/agent/sessions` (the VM is ephemeral); `--offline`
disables startup network. `--provider` defaults to `openrouter` via
`CodeyBox:Prime:Provider` (blank omits the flag); `--model` takes the
provider-catalog id verbatim (OpenRouter-style when the provider is
openrouter). `--autonomous` is deliberately NOT passed: budget exhaustion
exits non-zero ("Autonomous run stopped before terminal evidence"), which
the pipeline treats as a reported failure before staging diffs — losing real
work. A plain `-p` run already works multi-turn until the model stops
(verified live: a file edit completed across 3 turns, exit 0). Prompt travels
on stdin (verified: stdin-only answers normally) to dodge the 128 KiB
`MAX_ARG_STRLEN` ceiling on rework prompts.

**Exit-zero errors.** Prime exits 0 even when the run dies before producing
output (verified: a bad OpenRouter key exits 0 with
`stopReason:"error"` + `errorMessage:"401 User not found…"` in the event
stream; a missing key exits 0 with `No API key found for the selected
model.` on STDERR — unlike pi, which prints it on stdout). The runner lifts
the terminal error into `TerminalDiagnostic` via `PrimeTerminalDiagnoser`,
which scans BOTH streams, so the pipeline's no-changes branch parks
quota/auth give-ups instead of dead-lettering them as "produced no changes".

**Authentication.** Provider API keys from the environment
(`OPENROUTER_API_KEY`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, … — 26
provider variables recognised; full table in prime's `providers.md`, with a
verbatim OpenRouter block in the docs). The shipped credential mapping wires
host `CODEYBOX_PRIME_API_KEY` to sandbox-side `OPENROUTER_API_KEY` and
`CodeyBox:Prime:Provider` defaults to `openrouter` to match; operators
fronting other providers add that provider's variable to the mapping and set
the provider knob. `PRIME_API_KEY` is only the Prime Inference provider
entry, not a CLI credential, and is not mapped. Interactive `/login` state
is in-session only and is not shipped into sandboxes. Always configure an
explicit `--model` (via `CodeyBox:AgentDefaults[prime]` or the class
member): the CLI's own startup default is a paid model, which fails a $0-key
run with `Key limit exceeded`. Custom providers live in
`~/.prime/agent/models.json` for local use but the sandbox path always uses
the staged environment plus `--provider`/`--model`.

**Project trust.** Unlike pi's ask-default posture, prime loads `AGENTS.md`
context non-interactively with no approval prompt (verified in a fresh
directory: a marker rule fired with exit 0 and empty stderr). The CLI
exposes no approve/trust override flags, so the runner passes none — and
nothing can gate an unattended run.

**Reasoning effort.** `ReasoningMode` maps 1:1 onto prime's `--thinking`
(`off|minimal|low|medium|high|xhigh|max` — same vocabulary as pi). Only
exact allowlist members are emitted; anything else is ignored rather than
passed through to fail the CLI.

**Quota probe.** None — prime exposes no quota/credit meter (same position
as aider/opencode). Availability is covered by the credential-presence host
probe plus observed failure history (`QuotaUnknownPolicy`, default
`UseObservedFailures`), and `PrimeQuotaFailureDetector` classifies the
relayed provider errors (`401 User not found` / `Missing Authentication
header` / `No API key found` rows → Unauthorized; `Key limit exceeded` /
`insufficient credits` / 402 rows → LimitReached; shared 429 rows →
RateLimitExceeded) with operator-extensible rows under
`CodeyBox:QuotaFailurePatterns:prime`. Both stdout and stderr are scanned —
terminal frames live on stdout, the pre-session failure on stderr. Note the
ordering: prime appends `Run /login to update credentials.` to EVERY
provider error including quota refusals, so the auth row sits last and a 403
parks as LimitReached, not Unauthorized.

**Smoke probes.** Host-side `PrimeSmokeProbe` is a credential-presence check
only (`OPENROUTER_API_KEY` in the bundle — no network call).
`PrimeInVmSmokeProbe` execs `prime-agent --version` plus a
`prime-agent --help` assertion covering BOTH halves of the transport
(`-p/--print` and `--mode`), so a prime build that dropped either half
benches at smoke time instead of dispatching into an interactive wait or an
unparseable run.

**Model-list probe.** `prime-agent model list` needs an authenticated
provider plus network and emits a human-readable table rather than
machine-readable ids, so the host-side probe returns the curated
`PrimeKnownModels` seed instead of live-reading the catalog. Operator
`ModelId` values absent from the seed surface as a startup warning, never a
hard reject (prime routes any provider-catalog id beyond the seed).

**Cost attribution.** `PrimeCostExtractor` takes the latest non-zero
(cumulative) `usage` frame and the verbatim `message.model` id (prime
reports the full provider-catalog id, e.g.
`nvidia/nemotron-3.5-lightning:free`). No stream usage means unknown
(null), never a zero that looks like data. Frame scanning is shared with pi
(`PiShapeParsing`) — both CLIs speak the same wire shape. Bundled rates live
in `agent-pricing-defaults.json` under the `prime` bucket for the shipped
OpenRouter-backed member (plus a $0 row for the `:free` smoke tier);
operators fronting other providers add that provider's list prices there (or
under `CodeyBox:AgentPricing`) keyed by the verbatim `message.model` id.

### Caveman-code CLI (`caveman-code`)

Caveman-code (`github.com/JuliusBrussee/caveman-code`, npm
`@juliusbrussee/caveman-code`, **MIT**) is a standalone terminal coding agent
in the pi-mono family whose pitch is token compression (~2× fewer tokens than
Codex CLI on its published 25-task MicroBench: 524k vs 1,010k fresh tokens,
14/25 vs 15/25 passes, gpt-5.5 xhigh). All CLI behaviour below was verified
live against 0.65.2 (npm install + `--help` + unauthenticated dispatch +
`--list-models` with placeholder keys).

**Status: frozen upstream.** The author froze this repo in August 2026; active
work moved to the `caveman wrap` successor, whose `caveman` binary **shadows**
this package's primary alias — uninstall one before installing the other. The
runner therefore invokes the unambiguous `caveman-code` alias (same CLI, no
collision), and the shipped `frontier-coding` member carries no `sensitive`
capability: same-model, same-vendor spend as the codex member, but a frozen
upstream gets conservative clearance until an operator opts in. Expect no
upstream fixes; treat CLI-flag drift as freeze, not breakage.

**Install in the sandbox image** — needs Node.js 20+:

```sh
npm install -g @juliusbrussee/caveman-code
```

**Non-interactive invocation.** `caveman-code -p` ("print mode: process
prompt and exit") with the prompt on **stdin** — a piped prompt with no
positional reaches agent init, which keeps large rework prompts under the
128 KiB MAX_ARG_STRLEN ceiling. Do NOT switch to `caveman-code exec --json`
for large prompts: the exec subcommand is dispatched before stdin is read, so
its prompt must ride argv (and it accepts no `--provider`/`--thinking`). Text
mode prints only the final assistant text; errors go to stderr (exit 1) —
except the missing-key message, which prints to **stdout with exit 0**, so a
keyless dispatch looks "successful" unless the smoke gate benches the agent
first (it does — see below).

**Authentication — BYOK API keys only.** The CLI reads provider keys from the
environment (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, … —
full list in `CavemanCodeAgentRunner.CredentialEnvironmentVariables`). OAuth
(`/login`, OS-keychain tokens) has no headless path and is unsupported. The
runner never passes `--api-key` (secrets must not ride argv). Operator
wiring: set `CODEYBOX_CAVEMAN_ANTHROPIC_API_KEY` /
`CODEYBOX_CAVEMAN_OPENAI_API_KEY` / `CODEYBOX_CAVEMAN_GEMINI_API_KEY` /
`CODEYBOX_CAVEMAN_OPENROUTER_API_KEY` on the host (or inject the conventional
provider vars directly — both are mapped through). Endpoint-style providers
(Azure OpenAI, AWS Bedrock) need companion config beyond a bare key and are
not covered; per-instance member `CredentialReference` tokens are not mapped
for this kind (the sandbox var is provider-dependent), so multi-key setups
use one member per provider key. The key never appears in any log line.

**Default model.** Shipped default is `openai/gpt-5.5` (both
`CodeyBox:AgentDefaults:caveman` and the `frontier-coding` member): the
provider-prefixed form pins the provider so `--model` resolves without
`--provider`, and it is the exact configuration the upstream bench measured.
`gpt-5.5`, `claude-opus-4-6`, `claude-opus-4-7`, and `claude-sonnet-4-6` are
confirmed present in the 0.65.2 registry via `caveman-code --list-models`.

**Reasoning effort.** `--thinking off|minimal|low|medium|high|xhigh`
(verified). The runner forwards `ReasoningMode` only when it matches that set
(lowercased); anything else is dropped rather than forwarded. Pin thinking
via member `ReasoningMode`, not a `:suffix` on `ModelId` — a suffixed id will
not match the model-list probe and trips a startup warning.

**Billing — pay-per-token (`PayPerApi`).** Spend bills to the operator's own
provider accounts, so the orchestrator never waits on quota for this member.
Rates live in `agent-pricing-defaults.json` under the `caveman` bucket in both
bare and provider-prefixed key forms, hot-reloadable under
`CodeyBox:AgentPricing`. Cost extraction is JSON-envelope-only (caveman
camelCase usage, Anthropic/OpenAI usage objects): plain-text `-p` runs emit
no counts, and prose token mentions are never matched, so a normal dispatch
attributes zero until a structured envelope appears.

**Quota probe.** None — BYOK keys expose no remaining-credit meter (same
position as opencode). Availability is covered by the credential-presence host
smoke probe plus the in-VM `caveman-code --list-models` check; transient
provider refusals (shared 429 shapes, `overloaded_error`, missing-key output)
are classified by `CavemanCodeQuotaFailureDetector` into rate-limit backoff /
auth-error paths.

**Model-list probe.** Runs `caveman-code --list-models` on the API host and
parses the provider table (emits both `provider/model` and bare `model` ids
so either member spelling validates). The registry answers offline from env
keys alone, but with no key visible it prints `No models available…` at exit
0 — the probe treats zero parsed ids as failure. Set
`CODEYBOX_CAVEMANCODE_BINARY` to override the binary path. When the CLI is
missing or keyless on the host, validation is skipped with a warning.

### Autohand CLI (`autohand`)

**Install in the sandbox image** — add the install line to
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd`, matching the
selected provider (verified against autohand-cli 0.9.7, 2026-09-16):

```sh
npm install -g --ignore-scripts autohand-cli@0.9.7
```

Apache-2.0 ([repo](https://github.com/autohandai/autohand-code)). npm
package `autohand-cli` (binary `autohand`); needs Node.js on the image.
`--ignore-scripts` skips the postinstall lifecycle script (it only
`chmod`s the binary; skipping it keeps bakes hermetic on images where
`node-pty` cannot compile — the postinstall `|| true` guard means skipping
is safe). The `@0.9.7` pin keeps the bake deterministic — bump it only after
re-verifying the headless contract below, because flag names and the auth
gate have already moved once (the documented `--session`/`--new` flags do
not exist in 0.9.7).

**Non-interactive invocation.** The runner drives a one-shot headless run
with the prompt on stdin and NO positional prompt argument:

```sh
autohand -p --output-format stream-json --bare --offline --yes --unrestricted [--model <id>]
```

`autohand -p "<text>"` is documented on the dedicated Headless Mode page as
*Run a single instruction in command mode*: the agent processes the
instruction, acts on the repository, and exits — exactly the headless
contract CodeyBox needs. The prompt travels on stdin with a bare `-p`
instead of `-p <text>` argv: Linux's `MAX_ARG_STRLEN` is 128 KiB per argv
element and rework prompts can exceed it. `--output-format stream-json` is
the runner's only transport — whole-doc `json` is rejected by this CLI
version (`Invalid --output-format value "json"`). Observed bare stream
frames: `tool_start` / `tool_end` / `file_modified` / `result` (final text in
the `content` string) / `error` (terminal failure in `message`). No usage
frame is emitted, so cost attribution records unknown, never zero.
`--plan` is never passed (read-only planning produces no changes by design).

**Authentication — `--bare` is mandatory, a vendor account is not.**
Without `--bare` the CLI forces an interactive Autohand-account device login
(`Initiating authentication… visit autohand.ai/signin`) even with a valid
provider key configured — that blocks forever in the sandbox, so `--login`
being "optional" in the docs does not hold for unattended runs. With
`--bare` the CLI runs headless with no vendor account, but bare mode
requires `AUTOHAND_API_KEY` in the environment (the shipped credential
mapping wires host `CODEYBOX_AUTOHAND_API_KEY` to it). That variable is only
a gate: the provider credential is read exclusively from the guest
`~/.autohand/config.json` provider block (`{"provider":"openrouter",
"openrouter":{"apiKey":…,"baseUrl":…,"model":…}}`), which no environment
variable backfills (verified: an empty file key fails with `Setup
cancelled`, a dummy file key with `Authentication failed … User not
found`, even with both `OPENROUTER_API_KEY` and `AUTOHAND_API_KEY` set).
The runner therefore seeds the guest config before every dispatch via the
credential-file writer (stdin transport, mode 0600) and fails fast when the
bundle carries no key — never dispatching into the blocking first-run
wizard. The provider id comes from the hot-reloadable
`CodeyBox:Autohand:Provider` knob (shipped as `openrouter`); the guest also
needs `openrouter.ai` on `CodeyBox:AgentAllowedHosts` (shipped in the
default) for the OpenRouter route. A `$0`-spend-limit OpenRouter key only
serves ids ending `:free` — a paid id fails with `Key limit exceeded (total
limit)`, which the detector parks as quota exhaustion. Bare mode also skips
hooks, LSP, attribution, and AGENTS.md auto-discovery: repo instructions in
`AGENTS.md` are NOT picked up on autohand runs (the prompt itself carries
everything the run needs).

**Exit-zero errors.** Autohand exits 0 on some terminal failures (verified:
`{"type":"error","message":"Command did not complete successfully."}`
exits 0). The runner lifts the terminal error into `TerminalDiagnostic`, so
the pipeline's no-changes branch parks quota/auth give-ups instead of
dead-lettering them as "produced no changes" — the same shape `agy` has.

**Quota probe.** Ships as Unknown-only: autohand exposes no meterable quota
endpoint (it fronts nine providers), so no probe is registered and members
fall through to the `NullQuotaProbe` unknown path. The router's
`QuotaUnknownPolicy` (default `UseObservedFailures`) gates dispatch via
observed failure history, and `AutohandQuotaFailureDetector` classifies the
relayed provider errors (`Authentication failed` / `verify your OpenRouter
API key` → Unauthorized; `Key limit exceeded` / `permission for this model`
→ LimitReached; shared 429 rows → RateLimitExceeded) with
operator-extensible rows under `CodeyBox:QuotaFailurePatterns:autohand`.
The generic `Command did not complete successfully.` frame is deliberately
unmatched — it is a give-up, not quota/auth evidence.
