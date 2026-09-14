# Evaluation: caveman as an output-compression layer over CodeyBox agents

**Status:** Spike / evaluation. No behavioural code shipped with this document —
the capture-compatibility gate is analysed, savings and risks are measured against
real captured streams, and a config-driven per-agent opt-in is designed and left
ready to build. See [Recommendation](#recommendation).

**Subject:** [`caveman`](https://github.com/JuliusBrussee/caveman) (`v1.8.x`, MIT,
© 2026 Julius Brussee) — a token-**compression** skill/plugin, *not* a runner
agent. It injects a system-prompt ruleset that makes an existing coding-agent CLI
emit terser natural-language prose ("drop articles, filler, pleasantries") while,
per its own ruleset, keeping "code, commands, errors, file paths, URLs, JSON,
identifiers" and "structured output and machine-readable formats" **byte-for-byte
exact**. It hooks across Claude Code, Codex, Gemini, Cursor, Copilot, and 30+
agents.

The question this spike answers: does caveman interfere with the way CodeyBox
tees and **parses** agent stdout / structured streams? If yes → NO-GO, document
and stop. If no → measure real savings vs. risk and design a per-agent opt-in.

---

## 1. What CodeyBox actually depends on in agent output

CodeyBox requests each CLI's structured streaming mode (`claude --print
--output-format stream-json --verbose`, `codex exec --json`, gemini/agy
stream-json when probed-capable), tees stdout to JSONL under `logs/agents/…`,
and parses it (see [`agent-streams.md`](agent-streams.md),
[`stream-analysis.md`](stream-analysis.md)). The parse/consume path was mapped
end-to-end. Every dependency falls into one of three buckets, and **none of them
branches on the wording of the model's natural-language prose** — which is the
only thing caveman rewrites:

| # | Consumer | Keys off | Touched by caveman? |
|---|----------|----------|---------------------|
| 1 | Stream parsers (`FlexibleAgentStreamParser` + per-agent `ClaudeStreamParser`/`CodexStreamParser`/`GeminiStreamParser`/`CursorStreamParser`) | JSON envelope **fields**: event `type`, `usage`/`token_usage` counts, `total_cost_usd`, `tool_use`/`tool_result` ids + names, `is_error`, durations | **No.** Requires exact field names/types; the CLI serialises the envelope, not the model. Final assistant text is *stored*, never keyword-scanned. |
| 2 | `AgentFailureClassifier` | Literal **CLI stderr/stdout error signatures**: `usage_limit`, `HTTP 429`, `overloaded_error`, `401 Unauthorized`, `invalid_api_key`, `ECONNRESET`, `command not found`, CLI login-prompt sentences, `turn.failed` JSON `error.message` | **No.** These are CLI/HTTP/provider strings, kept byte-exact. The classifier *deliberately* refuses to trust free-form stdout precisely because "stdout can be model-controlled" — terser prose only *shrinks* its false-positive surface. |
| 3 | Turn success / completion (`PipelineRunner`) | **Process exit code** (`AgentResult.Success = ExitCode==0`) + **git state** (`git diff --cached --quiet` exit, `HasMeaningfulAgentChangesAsync`) | **No.** `AgentResult.Summary` is consumed only for exit-code extraction and redacted logging, never prose-branched. |

The single place model prose flows *downstream* is the **cross-agent handoff
brief** (`AgentStreamBriefBuilder` → `CrossAgentHandoffPromptPreprocessor`): a
≤2000-char tail of the prior agent's final message, injected into a fallback
agent's prompt inside a fenced `[UNTRUSTED DATA SECTION]` block. It is advisory
context for another LLM — concatenated and sanitised, never parsed for exact
content — and is already opt-in (`EnableHandoffSeeding`, default `false`).
Terser prior-agent prose summarises fine here; there is no exact-content contract.

The plaintext-fallback summariser counts lines containing `error`/`fatal`/…
into an observability integer (`[plaintext-fallback … errors=N]`). Nothing
branches on it; it is a dashboard counter. Cosmetic at most.

## 2. The real risk surface: model-emitted **exact-literal** contracts

CodeyBox requires the *model itself* to emit several byte-exact structured
artifacts. These are **not prose** — they are exactly the "structured output /
machine-readable formats / code / identifiers" that caveman's ruleset lists as
**never-compress**. A correctly-scoped caveman leaves them intact; the risk is
purely one of **imperfect model compliance** with a soft prompt instruction:

- **Commit messages + the `CodeyBox-Prompt-Revision` trailer.** When the
  *orchestrator* commits it composes the trailer block itself (byte-exact, safe).
  But the design also has the **agent echo** `CODEYBOX_PROMPT_REVISION` as a
  trailer on commits it makes itself; a `process:prompt-revision-trailer` auditor
  verifies it via regex `^\s*\*?CodeyBox-Prompt-Revision\s*:\s*(\d+)\s*\*?\s*$`.
  A terse-rewrite of a commit message would fail that audit → blocks merge.
- **Check-and-Act verdict sentinels** — the model must output
  `<<<CODEYBOX_VERDICT>>> {json} <<<END_VERDICT>>>`; the parser hard-fails on
  missing sentinels or fields.
- **`<codeybox-question id="…">…</codeybox-question>`** blocks — strict regex.
- **Plan-audit verdict JSON** and **`.codeybox/suggestions.json`** enum tokens —
  strict JSON with fixed vocabularies; malformed entries are dropped/blocking.

**Gate verdict:** the capture/parse path is **compatible** — CodeyBox parses JSON
envelopes, CLI error signatures, exit codes, and git state, all of which caveman
leaves exact by design. The residual exposure is not a parse-*interference* but a
*probabilistic compliance* risk against the exact-literal contracts above, which
collides directly with this repo's "false machine-facing evidence is the gravest
offense" contract. That risk is real but bounded and testable (§4).

## 3. Measured savings — small on *our* workload, not the headline 65 %

caveman's "average 65 % output reduction" is measured on **prose-heavy** prompts
(bug explanations 87 %, Q&A 72 %, web-search summarisation 68 %). Its own numbers
for the category that matches CodeyBox — **"Code edits: 50 %"**, "Architecture
discussion: 30 %" — are lower, and three multipliers shrink them further for a
tool-using coding agent:

1. **Output-tokens only.** Input and **reasoning** tokens are untouched. For the
   thinking models CodeyBox runs (e.g. `claude-opus-*-thinking`), reasoning is a
   large share of billed output — entirely outside caveman's reach.
2. **Tool-use payloads are untouched.** Inspecting CodeyBox's own captured
   stream fixtures (`tests/…/Fixtures/AgentStreams/*.jsonl`), a coding turn's
   `usage.output_tokens` is dominated by `tool_use` inputs — the exact commands
   and file edits (`dotnet test …`, `Edit` diffs) — which caveman keeps
   byte-exact. The compressible surface is only the interstitial NL prose plus
   the short final `result` summary.
3. **~1–1.5 k input tokens/turn overhead** for the ruleset itself. On short
   tool-heavy turns this can make the change **net-negative**.

Net: expect low-single- to low-double-digit percent of *output* tokens on real
work-phase turns, with the largest wins on the rare prose-heavy turns (audit
explanations, question text). This is worth trialling behind a flag, not
enabling globally.

**A live end-to-end measurement was not run in this spike** because it requires
API-credentialed agent invocations that this sandbox does not hold. CodeyBox
already persists the exact substrate for that measurement: `agent_stream_summaries`
records `output_tokens`, `input_tokens`, and `estimated_usd` **per invocation**.
The operator A/B is therefore first-class (§5): run a fixed work-item corpus with
the flag off, then on, and read the deltas straight from that table — no new
instrumentation needed.

## 4. Activation caveat (headless mode)

caveman's always-on path for Claude Code is a **SessionStart/UserPromptSubmit
hook** that writes a per-session flag file. CodeyBox invokes every CLI headless
and single-shot (`--print` / `exec`), in a **fresh cloned VM per work item** with
no prior interactive session to set the flag. Whether those hooks fire in headless
mode is unverified upstream. The robust enablement therefore does **not** rely on
caveman's session hook — it injects the ruleset explicitly per invocation (§5),
which also makes the toggle deterministic and hot-reloadable.

## 5. Proposed per-agent opt-in (config-driven, hot-reloadable) — design, not yet built

Reuse existing seams; add no generic "arbitrary extra args" passthrough (there is
none today, deliberately).

- **Enablement channel — the existing `IAgentPromptPreprocessor` chain.** Add a
  `CavemanPromptPreprocessor` that prepends the caveman ruleset (vendored, pinned
  by commit — MIT permits this; do not depend on a live `npx`/marketplace fetch
  inside the sandbox) to the work/rework prompt **only when enabled for that
  agent kind**. This is the same ordered seam already used by
  `CrossAgentHandoffPromptPreprocessor`, so it is provider-agnostic and needs no
  per-CLI flag plumbing. For Claude specifically, an optional enhancement is to
  route the ruleset through its separate system-prompt channel
  (`SupportsSeparateSystemPrompt` / `--append-system-prompt`) instead of the
  prompt body.
- **Config — a hot-reloadable per-agent snapshot**, mirroring the established
  `AgentNetworkToleranceSnapshot` / `AgentDefaultsSnapshot` / `PipelineTuningSnapshot`
  patterns (all `IOptionsMonitor`-backed). Shape:

  ```json
  {
    "CodeyBox": {
      "Caveman": {
        "Enabled": false,
        "PerAgent": { "claude": true, "codex": false, "gemini": false },
        "RulesetPath": "vendor/caveman/SKILL.md",
        "Channel": "AppendSystemPrompt"
      }
    }
  }
  ```

  Default **off** everywhere. `Enabled=false` is a hard master switch. `RulesetPath`
  points at the vendored, version-pinned ruleset (no network at dispatch).
  `Channel` ∈ `{PromptPrefix, AppendSystemPrompt}`.
- **Do NOT adopt caveman's opt-in sub-features:** `/caveman-compress` rewrites
  repo memory files (e.g. `CLAUDE.md`) — that is a real working-tree mutation and
  must never run in an autonomous work sandbox. The `caveman-shrink` MCP
  middleware compresses tool *descriptions* and would sit on the tool-call path —
  out of scope and explicitly excluded.
- **Guardrails the ruleset text must carry** (belt-and-braces against the §2
  compliance risk): an explicit instruction that commit messages, the
  `CodeyBox-Prompt-Revision`/`Co-Authored-By` trailers, the
  `<<<CODEYBOX_VERDICT>>>`/`<codeybox-question>` blocks, and any `.codeybox/*.json`
  are structured output to be emitted verbatim. caveman's stock ruleset already
  covers "structured output / machine-readable formats"; the vendored copy should
  name CodeyBox's specific contracts too.
- **Ship behind these tests before default-on for any agent:** (a) a regression
  asserting the `CodeyBox-Prompt-Revision` trailer still matches the auditor regex
  on a caveman-enabled commit; (b) round-trip parse of a `<<<CODEYBOX_VERDICT>>>`
  block emitted under caveman; (c) an A/B token-savings check reading
  `agent_stream_summaries.output_tokens` across a fixed corpus, asserting net
  savings > 0 for the enabled agent before promoting it.

## Recommendation

**Compatible — conditional GO, gated behind a default-off per-agent opt-in.** The
capture/parse gate passes: nothing CodeyBox parses or branches on is what caveman
rewrites. But the realistic savings on CodeyBox's tool-heavy, thinking-model
workload are modest and possibly net-negative on short turns, and there is a real
(if bounded) compliance risk against the exact-literal machine-facing contracts in
§2. The responsible path is the §5 design: vendored+pinned ruleset injected via the
existing prompt-preprocessor seam, hot-reloadable per-agent config defaulting to
**off**, the two parse-safety regressions and the A/B savings check as gates before
enabling any single agent. This spike delivers that analysis and design; it
intentionally does **not** wire the feature on, because the measurement does not
justify shipping enablement blindly, and a default-off feature with no live
consumer would be speculative machinery.
