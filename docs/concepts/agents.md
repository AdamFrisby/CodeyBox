# Agents

CodeyBox treats coding agents as plug-in CLIs. The framework speaks to
each agent through `IAgentRunner`. Agents are language-agnostic: they receive
a prompt and a git working tree, then edit files in whatever stack the project
uses. Language-specific behavior lives in auditors and operator-supplied
tooling, not in the agent runner contract.

## Built-in agents

| Kind        | Binary in sandbox | Auth env (sandbox-side) | Host env (orchestrator) |
|-------------|-------------------|-------------------------|-------------------------|
| `claude`    | `claude`          | `ANTHROPIC_API_KEY`, or `CLAUDE_CODE_OAUTH_TOKEN` for a subscription | `CODEYBOX_CLAUDE_API_KEY`, or `CODEYBOX_CLAUDE_OAUTH_JSON` for a subscription bundle |
| `copilot`   | `copilot`         | `GH_TOKEN`              | `CODEYBOX_COPILOT_TOKEN`  |
| `codex`     | `codex`           | `OPENAI_API_KEY`        | `CODEYBOX_CODEX_API_KEY`  |
| `gemini`    | `gemini`          | `GEMINI_API_KEY`        | `CODEYBOX_GEMINI_API_KEY` |
| `cursor`    | `agent`           | `CODEYBOX_CURSOR_AUTH_JSON` (subscription credentials JSON) | `CODEYBOX_CURSOR_AUTH_FILE` (file path on host) |
| `opencode`  | `opencode`        | `OPENCODE_AUTH_JSON`, written to `~/.local/share/opencode/auth.json` | `CODEYBOX_OPENCODE_AUTH_FILE` |
| `antigravity` | `agy`           | `CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON` (OAuth bundle, written to `~/.gemini/antigravity-cli/antigravity-oauth-token`) | `CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON` |
| `crock`     | `crock`           | `CROCK_CONFIG_JSON` (file-materialised to `~/.crockcode/config.json`) | `CODEYBOX_CROCK_CONFIG_JSON` |
| `pi`        | `pi`              | `ANTHROPIC_API_KEY` (provider API key; other providers use their own variable from pi's provider table — see [Pi quirks](../reference/agent-quirks.md#pi-coding-agent-pi)) | `CODEYBOX_PI_API_KEY` |
| `aider`     | `aider`           | `OPENROUTER_API_KEY` (provider API key; other providers use their own variable from aider's litellm provider table — see [Aider quirks](../reference/agent-quirks.md#aider-aider)) | `CODEYBOX_AIDER_API_KEY` |
| `goose`     | `goose`           | `OPENROUTER_API_KEY` (provider API key; goose honors thirteen provider variables — see [Goose quirks](../reference/agent-quirks.md#goose-cli-goose)) | `CODEYBOX_GOOSE_API_KEY` |
| `prime`     | `prime-agent`     | `OPENROUTER_API_KEY` (provider API key; other providers use their own variable from prime's provider table — see [Prime quirks](../reference/agent-quirks.md#prime-agent-prime-agent)) | `CODEYBOX_PRIME_API_KEY` |
| `autohand`  | `autohand`        | `AUTOHAND_API_KEY` (bare-mode gate; the runner seeds the same bundle value into guest `~/.autohand/config.json` — the CLI reads the key only from that file — see [Autohand quirks](../reference/agent-quirks.md#autohand-cli-autohand)) | `CODEYBOX_AUTOHAND_API_KEY` |
| `vibe`      | `vibe`            | `OPENROUTER_API_KEY` (provider API key; the variable named by the guest config's `[[providers]] api_key_env_var` — see [Vibe quirks](../reference/agent-quirks.md#vibe-vibe)) | `CODEYBOX_VIBE_API_KEY` |
| `cline`     | `cline`           | `OPENROUTER_API_KEY` (provider API key read directly from the environment — no config file required; other providers use their own variable matching `CodeyBox:Cline:Provider` — see [Cline quirks](../reference/agent-quirks.md#cline-cli-cline)) | `CODEYBOX_CLINE_API_KEY` |
| `kilo`      | `kilo`            | `KILO_API_KEY` (the runner seeds the same bundle value into guest `~/.config/kilo/kilo.jsonc` — the CLI reads the key only from that file on the openai-compatible path — see [Kilo quirks](../reference/agent-quirks.md#kilo-code-kilo)) | `CODEYBOX_KILO_API_KEY` |
| `omp`       | `omp`             | `OPENROUTER_API_KEY` (provider API key documented directly by the CLI; `OPENAI_BASE_URL` is the fallback and custom providers live in `~/.omp/agent/models.yml` — see [OMP quirks](../reference/agent-quirks.md#omp-omp)) | `CODEYBOX_OMP_API_KEY` |
| `cmd`       | `cmd`             | `OPENROUTER_API_KEY` (provider API key resolved through the seeded `~/.commandcode/providers.json` `$OPENROUTER_API_KEY` reference — the file never carries the raw key; the runner also seeds a non-credential `~/.commandcode/auth.json` placeholder for plan-less `--local-only` BYOK — see [Command Code quirks](../reference/agent-quirks.md#command-code-cmd)) | `CODEYBOX_CMD_API_KEY` |

The sandbox-side env name is what the agent CLI reads. The host-side name is
what the orchestrator looks up when building the credential bundle — for most
agents through `EnvironmentCredentialProvider`, and for Claude, Cursor,
Antigravity, opencode, and Crock through a dedicated provider that materialises
a credential *file* inside the sandbox. Quota probes resolve their own tokens
separately: Antigravity's, for instance, falls back to the Gemini OAuth file and
then `CODEYBOX_ANTIGRAVITY_OAUTH_TOKEN`. They are intentionally namespaced
differently so the host environment can hold multiple agents'
credentials at once without collision.

## Sandbox install commands

Each agent CLI must be present in the sandbox VM before dispatch; otherwise
dispatch fails with exit 127 (`<binary>: No such file or directory`). This
applies to both baked baselines and full-launch provisioning. The orchestrator
does **not** install agent binaries — that is operator-owned config under
`CodeyBox:MultipassExtraRuncmd` or `CodeyBox:Incus:ExtraRuncmd` (see
[`../reference/sandbox-baselines.md`](../reference/sandbox-baselines.md)).
Adding an `AgentKind` to an agent class without also adding its install line is
the most common cause of fresh-class dispatch failures.

| Kind | Sandbox install command | Notes |
|------|-------------------------|-------|
| `claude`  | `npm install -g @anthropic-ai/claude-code` | Needs Node.js on the image. |
| `copilot` | *operator-supplied — verify with current [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) docs* | The runner execs the standalone `copilot` binary (NOT `gh copilot`); `gh extension install github/gh-copilot` is the wrong CLI and will not satisfy the runner. |
| `codex`   | `npm install -g @openai/codex` | Reuses the Node.js stack from Claude. |
| `gemini`  | `npm install -g @google/gemini-cli` | `ReasoningMode` is **not** wired into argv — Gemini's reasoning level is encoded in `ModelId` (pick a `gemini-3-*-preview` model for HIGH). See [Gemini quirks](../reference/agent-quirks.md#google-gemini-cli-googlegemini-cli). |
| `cursor`  | `curl -fsSL https://cursor.com/install \| bash` | Installs as `agent` (not `cursor-agent`). See [Cursor quirks](../reference/agent-quirks.md#cursor-cli-agent). |
| `opencode` | `curl -fsSL https://opencode.ai/install \| bash` | Plaintext stdout only — no structured stream. |
| `caveman` | `npm install -g @juliusbrussee/caveman-code` | Invoked as `caveman-code` (the unambiguous alias — the successor `caveman wrap` package ships a colliding `caveman` binary). Needs Node.js 20+ on the image. Plaintext stdout only — no structured stream. |
| `antigravity` | *operator-supplied — stage the `agy` binary on the host and ship it via `CodeyBox:MultipassExecutableProvisions` or `CodeyBox:Incus:ExecutableProvisions`, matching the selected provider* (see [Antigravity quirks](../reference/agent-quirks.md#google-antigravity-cli-agy)). Do not use `curl -fsSL https://antigravity.google/cli/install.sh \| bash`: that URL serves the landing page, not a script, and piping HTML into `bash` fails silently when the runcmd ends with `\|\| true`. | Installs the proprietary `agy` CLI on the non-login sandbox PATH. Multi-model gateway — each gateway model id is a separate quota bucket. Configure each accepted model as its own `AgentClass` member; the router gates per-model via the existing `(AgentKind, ModelId)` exhaustion key. |
| `pi` | `npm install -g --ignore-scripts @earendil-works/pi-coding-agent` | MIT-licensed; needs Node.js on the image. `--ignore-scripts` skips npm lifecycle scripts during install. See [Pi quirks](../reference/agent-quirks.md#pi-coding-agent-pi). |
| `aider` | `curl -fsSL https://aider.chat/install.sh \| bash` then `uv tool install aider-chat` (or `uv tool install aider-chat@<version>` to pin) | Apache-2.0; needs Python 3.12 on the image. The install script installs `uv` and then aider via `uv tool install --python python3.12 aider-chat`. See [Aider quirks](../reference/agent-quirks.md#aider-aider). |
| `goose` | tag-pinned `download_cli.sh` with SHA256 verification (see [sandbox baselines](../reference/sandbox-baselines.md)) | Shell installer for macOS/Linux (Windows unsupported — irrelevant: CodeyBox sandboxes are Linux). Installs the `goose` binary. Pinned to `v1.50.1` with `GOOSE_VERSION` (never `stable`) for reproducible bakes. See [Goose quirks](../reference/agent-quirks.md#goose-cli-goose). |
| `prime` | `curl -fsSL https://app.primeintellect.ai/prime-agent/install.sh \| sh` (pin with `PRIME_AGENT_VERSION=<version>`, e.g. `PRIME_AGENT_VERSION=0.9.5`) | Self-contained binary (no runtime needed). The installer resolves the latest stable release unless `PRIME_AGENT_VERSION` is set. See [Prime quirks](../reference/agent-quirks.md#prime-agent-prime-agent). |
| `autohand` | `npm install -g --ignore-scripts autohand-cli@0.9.7` | Apache-2.0; needs Node.js on the image. `--ignore-scripts` skips the postinstall lifecycle script (chmod-only, guarded with `\|\| true` upstream). Version-pinned for reproducible bakes — re-verify the headless contract before bumping. See [Autohand quirks](../reference/agent-quirks.md#autohand-cli-autohand). |
| `vibe` | `curl -LsSf https://mistral.ai/vibe/install.sh \| bash`, `uv tool install mistral-vibe`, or `pip install mistral-vibe` (pin with `uv tool install mistral-vibe@<version>`, e.g. `mistral-vibe==2.25.4`) | Needs Python 3.10+ on the image. Installs the `vibe` binary (plus a separate `vibe-acp` binary CodeyBox does not use). The guest also needs a `~/.vibe/config.toml` defining the provider and model alias (see [Vibe quirks](../reference/agent-quirks.md#vibe-vibe) and [sandbox baselines](../reference/sandbox-baselines.md)). |
| `cline` | `npm install -g cline@3.0.62` | Apache-2.0; ships platform binaries, needs no runtime. Version-pinned for reproducible bakes — re-verify the headless contract before bumping. See [Cline quirks](../reference/agent-quirks.md#cline-cli-cline). |
| `kilo` | `npm install -g @kilocode/cli@7.7.2` | MIT-licensed (Kilo Code Customs LLC); needs Node.js on the image. Version-pinned for reproducible bakes — re-verify the headless contract (`run --auto --format json`) before bumping. See [Kilo quirks](../reference/agent-quirks.md#kilo-code-kilo). |
| `omp` | `curl -fsSL https://omp.sh/install \| sh -s -- --binary --ref v18.2.2` (or `bun install -g @oh-my-pi/pi-coding-agent`) | MIT-licensed (oh-my-pi fork of pi); installs the `omp` binary. NOT the `oh-omp` fork, which is a different project shipping a different binary. Version-pinned for reproducible bakes — re-verify the headless contract (`-p --mode json`) before bumping. See [OMP quirks](../reference/agent-quirks.md#omp-omp). |
| `cmd` | `npm install -g command-code@1.54.2` | Needs Node.js on the image. Version-pinned for reproducible bakes — re-verify the headless contract (`-p --output-format json`), the `--yolo` autonomy flag, and the `~/.commandcode/` file layout (see below) before bumping. The base image should also pre-seed `~/.commandcode/providers.json` (openrouter entry with the `$OPENROUTER_API_KEY` reference — never a raw key) and `~/.commandcode/auth.json` (non-credential presence placeholder) so ad-hoc runs work; the runner re-seeds both at dispatch (see [Command Code quirks](../reference/agent-quirks.md#command-code-cmd) and [sandbox baselines](../reference/sandbox-baselines.md)). |

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
   [`../reference/sandbox-baselines.md`](../reference/sandbox-baselines.md)). The providers
   keep independent content-addressed baseline identities, and Incus also
   applies its explicit inputs on the full-launch path. Pin by digest where
   the upstream supports it.

6. **Document it.** Add the install command to the table above and a section
   to [`../reference/agent-quirks.md`](../reference/agent-quirks.md) covering
   the binary name, non-interactive invocation, credential layout, and any
   reasoning-flag or quota-probe specifics. That page is what stops the next
   person rediscovering the same footgun.

7. **Add both smoke probes.** `IAgentSmokeProbe` checks the credential on the
   host before pickup — see `ClaudeSmokeProbe` (HTTP endpoint) and
   `CursorSmokeProbe` (credential-bundle presence) for the two shapes.
   `IInVmSmokeProbe` execs the CLI inside a cloned sandbox, which is what
   catches a missing binary or a mis-materialised auth file before the first
   dispatch instead of after it. A class member whose agent has no registered
   `IInVmSmokeProbe` is benched at startup. See
   [`../operating/sandbox-reliability.md`](../operating/sandbox-reliability.md#agent-smoke-probes).

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

The one-shot process can exit before the orchestrator commits its work. For
work and rework turns there are three bounded recovery layers, tried in order.
The third is Incus-only.

| Layer | What is preserved | Resume behaviour |
|---|---|---|
| Live-sandbox CLI resume | The current `/work` tree and, for Claude and Codex, the validated CLI session id captured from structured output | While the sandbox is still usable, Claude runs `--resume <id>` and Codex `exec resume <id>`, with a short continuation instruction rather than the original task. |
| Durable agent-turn checkpoint | The dirty tree in an immutable content-addressed Git ref, a bounded CLI scratchpad archive in host-private SQLite, and the exact route, model, reasoning mode, phase and prompt revision | A later dispatch claims an attempt and fetches the ref into a fresh sandbox. Only the original route gets the private archive and native session id. |
| Retained Incus recovery lease | The exact stopped VM with its COW-root `/work`, bound to an opaque provider token and creation-time manifest | Used only when Incus cannot run the commands that would create the checkpoint. A later pickup adopts the VM, converts it into an ordinary checkpoint without running an agent, deletes the VM, and queues the normal resume. |

A checkpoint is taken only for a recognised quota failure, a transient network
failure, an infrastructure failure carrying the provider's explicit
`ExecutionUnavailable` signal, or exit `137`. Infrastructure-shaped text in
stderr is not enough, and neither is an exhausted CLI retry budget.

The whole path is fail-closed: if capture, the private write, or the push
fails, the original failure stands and the normal phase-retry policy applies —
recovery never manufactures a successful turn. Mechanics, caps, and the
adoption checks are in
[`../operating/agent-turn-checkpoints.md`](../operating/agent-turn-checkpoints.md).

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
| `pi` | *(no network call — pi fronts 30+ providers, so no single endpoint validates the credential)* — verifies the bundle carries `ANTHROPIC_API_KEY`; real auth check happens on first CLI call | `ANTHROPIC_API_KEY` |
| `aider` | *(no network call — aider fronts many providers through litellm, so no single endpoint validates the credential)* — verifies the bundle carries `OPENROUTER_API_KEY`; real auth check happens on first CLI call | `OPENROUTER_API_KEY` |
| `kilo` | *(no network call — kilo fronts hundreds of models, so no single endpoint validates the credential)* — verifies the bundle carries `KILO_API_KEY`; real auth check happens on first CLI call | `KILO_API_KEY` |
| `omp` | *(no network call — omp fronts ~60 providers, so no single endpoint validates the credential)* — verifies the bundle carries `OPENROUTER_API_KEY`; real auth check happens on first CLI call | `OPENROUTER_API_KEY` |

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
