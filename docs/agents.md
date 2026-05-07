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

The sandbox-side env name is what the agent CLI reads. The host-side env
name is what the orchestrator's `EnvironmentCredentialProvider` looks up
when building the credential bundle. They are intentionally namespaced
differently so the host environment can hold multiple agents'
credentials at once without collision.

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

5. Make sure the binary is present in your sandbox image. (Pin by digest.)

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

## Per-agent quirks

### Claude Code
Run with `--print` for non-interactive output and
`--dangerously-skip-permissions` because the VM boundary already is the
permission boundary. Remove `--dangerously-skip-permissions` if you also
want the agent's built-in tool-use prompts (you usually don't, inside a VM).

### GitHub Copilot CLI
Reads `GH_TOKEN` from the environment. **Important**: a generic
`GH_TOKEN` grants the agent broad GitHub access, not just Copilot. Issue
a fine-grained token scoped to the minimum the agent needs, ideally one
that cannot push to your real repos. Sandbox network policy must
**not** include `github.com` for this token to be safe.

### OpenAI Codex CLI
Reads `OPENAI_API_KEY`. The `--full-auto` flag skips Codex's per-edit
confirmations — appropriate inside a sandbox.

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

**Quota probe:** no Gemini usage endpoint is currently registered. Register
Gemini as `PayPerApi` in agent-class config so the orchestrator never gates on
it:
```json
{ "Agent": "gemini", "Billing": "PayPerApi", "ModelId": "gemini-2.5-pro" }
```

**Vertex AI / service-account auth:** the Gemini CLI also accepts Application
Default Credentials (ADC). If you prefer service-account auth over an API key,
set `GOOGLE_APPLICATION_CREDENTIALS` to the path of your service-account JSON.
This requires a custom `ICredentialProvider` that materialises the JSON into
the sandbox via `AgentCredential.Files` — the `Files` map on `AgentCredential`
is designed for exactly this use case.

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

**Globally** — set `CodeyBox:Smoke:Enabled=false`. No probes run at startup
or pickup.

**Per-project** — set `SkipCredentialSmokeTest: true` in the project
configuration. Useful for agents (like Copilot) that have no testable
credential, or for internal test projects that always use fake credentials.

### Configuration reference

| Key | Default | Description |
|-----|---------|-------------|
| `CodeyBox:Smoke:Enabled` | `true` | Enable or disable all smoke testing. |
| `CodeyBox:Smoke:CacheTtlMinutes` | `15` | How long to cache a probe result before re-probing. |
| `CodeyBox:Smoke:StartupTimeoutSeconds` | `10` | Per-agent timeout for the startup probe. |
