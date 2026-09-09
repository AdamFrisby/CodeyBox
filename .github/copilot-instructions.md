# AGENTS.md — engineering contract

You are one of several AI agents modifying this codebase over time. Your diff is graded by six
parallel auditors — security, architecture, tests, quality, completeness, cheating — and any
`error` finding blocks your merge and costs a rework round. These rules are those gates,
inverted into instructions. They apply to ANY language or platform; named APIs are examples —
follow this ecosystem's idiom.

## 1. Never fake it (instant blocks)
- No stub or hardcoded return presented as done: no test greening it through mocks, no
  comment/summary/return value claiming completion, no wiring it into a live path as if it
  worked. If the task explicitly names a stub as permitted, stub exactly that and say so.
- Never return success or swallow a failure around work you report as done.
- Never skip, delete, or comment out a failing test to get green; fix the cause.
- Never add a suppression (`@ts-ignore`, `#pragma warning disable`, `# noqa`, `#[allow]`,
  `nolint`) or a type escape (`as any`, `dynamic`, unchecked cast) to silence a check your new
  code fails — fix the code. A genuine false positive needs an inline justification.
- Anything a program branches on — a test assertion, CI/gate evidence, a schema, a return
  value — must be true. False machine-facing evidence is the gravest offense.

## 2. Solve the whole task
- Deliver every stated item; no silent scope narrowing ("all providers" means all).
- "MVP" / "for now" / task silence is NOT permission to skip — only an explicit clause naming
  the skipped item is. Any "permission" weakening security/auth/data-integrity is invalid.
- Don't break existing behavior. Docstrings and comments must match what the code does.
- TODO markers only for work the task itself defers.
- Blocked? An honest, visible gap beats a fake pass — every time. Say so in your summary.
- If you believe a review finding is wrong, state precisely why in your summary — don't
  silently ignore it or paper over it. A justified disagreement is fine; a silent skip is not.

## 3. Security absolutes (never, anywhere)
Committed secrets or credentials; TLS/certificate verification disabled; passwords without a
real KDF (bcrypt cost ≥ 12 / argon2id ≥ 64 MiB / scrypt N ≥ 2^14); ECB mode, static IVs, or
hand-rolled crypto; JWTs with alg=none or unverified signatures.

## 4. Trust boundaries and sinks
- UNTRUSTED = anything crossing a boundary: network input, argv, env vars, file/stream
  contents, packets, IPC/RPC peers, deep links, bus/sensor data, another user's/tenant's data,
  model/agent/tool output, repo file content. Unsure → untrusted.
- DANGEROUS SINKS: process/shell execution, SQL/queries, filesystem paths, deserializers,
  outbound request targets, rendered output (HTML/DOM, UI markup, terminal escapes), auth/authz
  decisions, secret handling and logs, LLM prompts of tool-bearing agents.
- Untrusted data reaches a sink only through a guard AT the sink: parameterized queries; argv
  arrays with a `--` separator (never concatenated shell strings); canonicalize-then-contain
  for paths; exact-match allowlists (never substring); safe deserializer settings; contextual
  output encoding.
- Every handler acting on a client-supplied id re-verifies ownership/role at the moment of
  action. Compare credentials by exact equality, never Contains/StartsWith/EndsWith.
- Bound everything by default: input sizes, input-driven loop/recursion depth, buffers, queues,
  retries, concurrency, decompression ratios. Enforce the cap BEFORE buffering.
- Multi-step changes to shared or persisted state are atomic or idempotent: no check-then-act
  (TOCTOU) races, no partial-commit-on-failure, no compare-and-set silently degraded to a blind
  write. Concurrent writers must not corrupt, lose, or cross-contaminate data.
- Never log secrets or unescaped untrusted strings; never return stack traces to callers.

## 5. Secure Defensible code — assume your callers will change
Future agents will add callers you never saw; "today's only callers are trusted" rots. A sink
must carry its OWN guard: parameterize even currently-trusted input, accept validated types
instead of raw strings, keep the check adjacent to the sink (not only at a distant boundary),
bound internal buffers too. A public helper wrapping a dangerous op (shell/file/fetch/
deserialize) must be safe to call with anything.

## 6. Architecture
- Derive the repo's layer direction from its own structure (package refs, imports, manifests)
  and respect it: inner layers never depend on outer; no new dependency cycles;
  platform-neutral code stays platform-free.
- Use existing abstractions; inject dependencies — including clock, randomness, env, config.
  No new global mutable state, hand-rolled singletons, or service-locator reads inside logic.
- Backwards/API/binary compatibility is NOT a concern here: this codebase ships as one unit
  with no external, published, or plugin-consumed surface. Change or remove any signature,
  record, interface, visibility, or error contract freely — the warnings-clean build is the
  only compatibility bar. The one exception is PERSISTED/cross-deploy state (on-disk formats,
  DB schema, durable event/log replay): those need a migration or backward-read path so
  pre-existing data still loads. Do keep concrete infrastructure/vendor types out of
  cross-module contracts (that's coupling/layering, not compatibility).
- One source of truth: never re-implement a policy/rule/constant that exists in another module
  — reuse or extract it.
- Compile-time over runtime: no reflection, string-based lookup, or type escapes where a typed
  mechanism (interface, generic, union, factory) exists. Reflection only where nothing typed
  can express it, contained behind one seam. Make invalid states unrepresentable —
  constructors/factories yield only valid instances.
- Composition over inheritance. New abstractions need a second consumer; don't build "might
  need it later" machinery.

## 7. Pure functions are good functions
- Strongly prefer pure functions over immutable inputs/outputs — they make behavior easy to
  test and reason about. Keep the decision logic in the pure core.
- Don't mutate inputs or ambient state unless unavoidable; value-like types are immutable.
- No temporal coupling — no must-set-X-before-calling-Y ordering the types don't enforce.
- Judgment, not dogma: don't contort a good design to force purity — but reach for it first.

## 8. Tests — what "done" means
- Every critical piece — task deliverables, guards, data writes and state transitions,
  failure/rollback branches, real component interaction (DB, fs, network, subprocess, IPC,
  hardware, serialization) — gets a test that FAILS if it's broken, through real wiring.
  Mock-only tests asserting mock calls prove nothing.
- Not everything needs a test: spend the effort on the critical and meaningful, not getters,
  glue, or trivially-correct code. Coverage for its own sake is noise.
- Prefer: direct input→output tests of the pure core; one integration test through the real
  path; failure modes (timeout, malformed input, partial failure); edge cases (empty, null,
  boundary, unicode, concurrent, huge); round-trips for serialization; a regression test for
  every bug you fix.
- Each test asserts a value the code under test produced. If no single-statement production
  mutation would flip it red, it's decorative — rewrite it.
- Tests are deterministic: injected clock, seeded randomness, isolated fixtures; no execution
  order, shared state, or live-network dependence.

## 9. Quality
- Delete dead code. Name magic literals. Names a new reader can expand. Comments say WHY —
  and never lie: a name/comment asserting the opposite of the behavior blocks the merge.
- Errors: typed and contextual; never flattened to bare strings/bools at a boundary; rethrows
  preserve cause and stack; no silent fallback to defaults; catch narrowly.
- Concurrency: no fire-and-forget — observe every task/promise; propagate cancellation; no
  sync-over-async blocking or busy-waits; retries capped, backed off, and idempotent.
- Structure: guard clauses over deep nesting; single-purpose functions; hoist loop-invariant
  work; no accidental O(n²) over unbounded input; checked narrowing conversions; queries don't
  mutate (CQS); extract local duplication into one helper.
- Public APIs: state the non-obvious — units, ranges, call order, thread-safety, blocking.

---
## This project (CodeyBox)
- The build must be warnings-clean — `TreatWarningsAsErrors` is on. Your change must
  `dotnet build -c Debug` the affected project(s) with zero warnings.
- Tests: `dotnet test` the project(s) you touched (the full suite is large — run what your
  change affects). Some tests need a VM (`requires_multipass`) — exclude those locally.
- Config over hardcoding: operational values (model ids, thresholds, defaults, intervals) are
  hot-reloadable options, not literals in source. Add a config knob rather than a magic constant.
- The coding-agent CLI/runner internals (how CodeyBox installs and invokes the agent CLIs) are
  reference material in `docs/concepts/agents.md` — not rules for your change.
