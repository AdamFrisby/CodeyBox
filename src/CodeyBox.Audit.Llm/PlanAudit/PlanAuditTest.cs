namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// A single test in the plan-audit chain, described as data. Every test in the
/// chain is one <see cref="PlanAuditTest"/> plugged into the same
/// <see cref="PlanAuditChainAuditor"/> and the same
/// <see cref="PlanAuditChainFramework"/> — so the shared reviewer framework is
/// implemented once and reused, and each test contributes only its verbatim
/// objective, review questions, pass/fail lines, automatic-blocker conditions,
/// required fixes, and the criterion keys a plan may self-skip as NOT_APPLICABLE.
/// </summary>
public sealed record PlanAuditTest
{
    /// <summary>Two-digit chain index, e.g. <c>"01"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Stable auditor name for logs, findings, and per-project toggling.</summary>
    public required string AuditorName { get; init; }

    /// <summary>Human-facing test title.</summary>
    public required string Title { get; init; }

    /// <summary>The test's Objective line (verbatim from the suite).</summary>
    public required string Objective { get; init; }

    /// <summary>The Review questions the reviewer must answer against the plan.</summary>
    public required string ReviewGuidance { get; init; }

    /// <summary>What a passing plan looks like for this test.</summary>
    public required string PassCriteria { get; init; }

    /// <summary>What a failing plan looks like for this test.</summary>
    public required string FailCriteria { get; init; }

    /// <summary>Conditions that are an automatic BLOCKER regardless of anything else.</summary>
    public required string AutomaticBlocker { get; init; }

    /// <summary>The required fixes a failing plan must apply.</summary>
    public required string RequiredFixes { get; init; }

    /// <summary>
    /// The criterion keys this test evaluates. A specific plan that genuinely
    /// does not touch a criterion self-skips it as NOT_APPLICABLE with a
    /// one-line reason; the reviewer is told to draw N/A entries from this list.
    /// Never bake project-specific N/A into the test — per-project relevance is
    /// the auditor on/off toggle, per-plan relevance is a NOT_APPLICABLE entry.
    /// </summary>
    public required IReadOnlyList<string> Criteria { get; init; }
}

/// <summary>
/// The built-in plan-audit chain tests. TEST 01 is the foundation
/// grounding / anti-hallucination gate; later tests in the chain assume a
/// grounded plan and are added as additional <see cref="PlanAuditTest"/> values.
/// <see cref="All"/> is the single ordered source of truth the wiring
/// (DI registration + <c>ProjectAuditorComposer</c> auto-inclusion) iterates,
/// so a new chain test is wired everywhere by adding it here alone.
/// </summary>
public static class PlanAuditTests
{
    /// <summary>Stable name of the TEST 01 auditor (referenced by DI + composition).</summary>
    public const string Test01AuditorName = "plan:integrity-evidence";

    /// <summary>Stable name of the TEST 02 auditor (referenced by DI + composition).</summary>
    public const string Test02AuditorName = "plan:goal-scope-acceptance";

    /// <summary>Stable name of the TEST 03 auditor (referenced by DI + composition).</summary>
    public const string Test03AuditorName = "plan:architecture-boundary";

    /// <summary>Stable name of the TEST 04 auditor (referenced by DI + composition).</summary>
    public const string Test04AuditorName = "plan:invariants-contracts-migrations";

    /// <summary>Stable name of the TEST 05 auditor (referenced by DI + composition).</summary>
    public const string Test05AuditorName = "plan:security-privacy-supply-chain";

    /// <summary>Stable name of the TEST 06 auditor (referenced by DI + composition).</summary>
    public const string Test06AuditorName = "plan:reliability-failure-concurrency";

    /// <summary>Stable name of the TEST 07 auditor (referenced by DI + composition).</summary>
    public const string Test07AuditorName = "plan:test-strategy-evidence";

    /// <summary>Stable name of the TEST 08 auditor (referenced by DI + composition).</summary>
    public const string Test08AuditorName = "plan:observability-operations-repair";

    /// <summary>
    /// TEST 01 — PLAN INTEGRITY AND EVIDENCE CLASSIFICATION. Determines whether
    /// the plan is grounded in the actual system rather than hallucinated
    /// structure, generic assumptions, or fake precision.
    /// </summary>
    public static PlanAuditTest Test01 { get; } = new()
    {
        Id = "01",
        AuditorName = Test01AuditorName,
        Title = "PLAN INTEGRITY AND EVIDENCE CLASSIFICATION",
        Objective =
            "Determine whether the plan is grounded in the actual system rather than " +
            "hallucinated structure, generic assumptions, or fake precision.",
        ReviewGuidance = """
            - Does the plan distinguish existing code, inferred behavior, and proposed changes?
            - Does it name the relevant files, modules, services, data stores, APIs, jobs, queues,
              permissions, and external systems it depends on or changes?
            - Are the named files / APIs / schemas / services / commands / dependencies supported by
              the supplied context (repo excerpts, prompt, attached artifacts)?
            - Does the plan identify missing context and its own assumptions explicitly?
            - Does it avoid inventing implementation details not present in the codebase or prompt?
            - Does it avoid line-level or file-level precision unless justified by inspected code?
            """,
        PassCriteria =
            "The plan clearly separates observed facts from proposed changes and assumptions; " +
            "important architectural claims are grounded in the supplied context; unknowns are " +
            "explicitly called out.",
        FailCriteria =
            "The plan invents files / APIs / services / data-models / conventions; treats " +
            "assumptions as established facts; or proposes implementation steps before establishing " +
            "what exists.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - relies on unsupported claims about security, data ownership, public contracts,
              migrations, or production behavior; or
            - makes changes to unidentified or hallucinated components (a file, service, table, or
              API that the supplied context does not show to exist).
            """,
        RequiredFixes = """
            - Classify every referenced artifact as OBSERVED / INFERRED / PROPOSED / UNSUPPORTED.
            - Replace each unsupported implementation claim with an explicit verification step.
            - Add the missing context-gathering steps the plan needs before implementation begins.
            """,
        Criteria =
        [
            "evidence-classification",   // observed vs inferred vs proposed separation
            "artifact-naming",           // names concrete files/modules/services/etc.
            "context-support",           // named artifacts are supported by supplied context
            "assumptions-and-unknowns",  // missing context and assumptions are explicit
            "no-invention",              // no invented implementation details
            "justified-precision",       // no unjustified line/file-level precision
        ],
    };

    /// <summary>
    /// TEST 02 — GOAL, SCOPE, NON-GOALS, AND ACCEPTANCE CRITERIA. Verifies the
    /// plan defines the problem, the intended behavior change, its scope
    /// boundaries, and measurable, objectively-verifiable completion criteria —
    /// and that it is the smallest useful change, not an opportunistic bundle of
    /// unrelated cleanup, broad refactoring, or speculative enhancements.
    /// </summary>
    public static PlanAuditTest Test02 { get; } = new()
    {
        Id = "02",
        AuditorName = Test02AuditorName,
        Title = "GOAL, SCOPE, NON-GOALS, AND ACCEPTANCE CRITERIA",
        Objective =
            "Verify the plan defines the problem, the intended behavior change, its scope " +
            "boundaries, and measurable completion criteria.",
        ReviewGuidance = """
            - What user- or system-visible behavior is changing, and is that change stated plainly?
            - What is explicitly out of scope (non-goals)?
            - What must NOT regress — what existing behavior, contract, or invariant must remain
              unchanged / backward-compatible?
            - What assumptions about the requirements does the plan make, and are they stated?
            - Are the success / acceptance criteria objectively verifiable (tied to specific
              behavior, tests, metrics, or compatibility) rather than vague or subjective?
            - Does the plan define the smallest useful change that satisfies the task?
            - Does it avoid mixing unrelated cleanup, broad refactoring, or speculative
              enhancements into the feature?
            """,
        PassCriteria =
            "Goal, non-goals, requirement assumptions, and acceptance criteria are explicit; the " +
            "success criteria are objectively verifiable (behavior/tests/metrics/compatibility); " +
            "the plan states what must remain unchanged; and it avoids opportunistic scope expansion.",
        FailCriteria =
            "The plan jumps straight to file edits without defining the problem; acceptance criteria " +
            "are vague, subjective, or absent; or it combines unrelated changes without justification.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - changes user- or system-visible behavior without saying what the behavior should
              become; or
            - cannot state what must remain backward-compatible or unchanged (the not-regress set).
            """,
        RequiredFixes = """
            - Add a concise goal statement naming the problem and the intended behavior change.
            - Add explicit non-goals (what this change deliberately does not do).
            - Add acceptance criteria tied to concrete behavior, tests, metrics, or compatibility —
              each objectively verifiable, not subjective.
            - Name what must remain unchanged / not regress.
            - Remove or defer unrelated refactors and speculative enhancements to a separate task.
            """,
        Criteria =
        [
            "goal-statement",           // concise problem statement is present
            "behavior-change",          // the intended user/system-visible behavior change is stated
            "scope-boundaries",         // what is in scope is bounded
            "non-goals",                // explicit out-of-scope / non-goals
            "requirement-assumptions",  // assumptions about the requirements are explicit
            "acceptance-criteria",      // objectively-verifiable completion criteria
            "must-not-regress",         // states what must remain unchanged / backward-compatible
            "smallest-useful-change",   // defines the smallest useful change
            "no-scope-creep",           // no unrelated cleanup / refactor / speculative extras
        ],
    };

    /// <summary>
    /// TEST 03 — ARCHITECTURAL BOUNDARY, MODULARITY, AND COUPLING. Determines
    /// whether the plan places the change inside the correct architectural
    /// boundary with minimal coupling and appropriate abstraction — the right
    /// module/layer owns the new behavior, domain logic stays out of
    /// UI/transport/persistence/glue, and any new interface, adapter, service, or
    /// abstraction is justified by concrete volatility, testability, security, a
    /// second implementation, or a clearly-owned boundary rather than vague future
    /// extensibility or architecture-by-fashion.
    /// </summary>
    public static PlanAuditTest Test03 { get; } = new()
    {
        Id = "03",
        AuditorName = Test03AuditorName,
        Title = "ARCHITECTURAL BOUNDARY, MODULARITY, AND COUPLING",
        Objective =
            "Determine whether the plan places the change inside the correct architectural boundary " +
            "with minimal coupling and appropriate abstraction.",
        ReviewGuidance = """
            - Which module / layer / service / component owns the new behavior, and is that owner named?
            - Does the plan preserve the existing architecture and its idioms (layer direction,
              dependency rules, established patterns) rather than cutting against them?
            - Does domain / business logic stay out of UI, transport, persistence, and glue code?
            - Does the change minimize the number of boundaries it crosses?
            - Does it avoid leaking vendor / database / transport / framework / UI details into layers
              that should not know about them?
            - Are any new interfaces or adapters justified (a real seam), and are they narrow and stable?
            - Is every new abstraction justified by CONCRETE volatility, testability, security,
              a clearly-owned boundary, or multiple implementations — not vague future extensibility?
            - Does it avoid architecture-by-fashion — unnecessary microservices, event buses, plugin
              systems, CQRS, queues, Redis, or generic factories introduced without a concrete need?
            - If it proposes a distributed / multi-process architecture, does it explain data ownership,
              consistency, deployment, and operational impact?
            """,
        PassCriteria =
            "The plan identifies the correct owner boundary for the new behavior; changes are localized " +
            "and cohesive within that boundary; new interfaces/adapters are stable, narrow, and " +
            "purposeful; and every new abstraction is concretely justified.",
        FailCriteria =
            "The plan spreads business logic across layers; couples broadly to a third-party or " +
            "implementation detail; justifies abstractions only by vague future extensibility; or adds " +
            "a new service / package / module without lifecycle, ownership, scaling, security, or " +
            "coupling justification.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - corrupts a core architectural boundary (e.g. inverts the layer direction, introduces a
              dependency cycle, or makes an inner/platform-neutral layer depend on an outer/vendor one); or
            - places authoritative business rules only in UI, client, or request-edge code; or
            - proposes a distributed / multi-process architecture without explaining data ownership,
              consistency, deployment, and operational impact.
            """,
        RequiredFixes = """
            - Name the affected boundary and the module / layer that owns the new behavior.
            - Move authoritative business rules to the layer that owns them, out of UI/transport/glue.
            - Add or narrow the interfaces / adapters needed to keep the boundary clean and decoupled.
            - Remove speculative abstractions that lack a concrete second consumer or volatility driver.
            - Separate any preparatory refactoring from the behavior change into a distinct step.
            """,
        Criteria =
        [
            "boundary-ownership",          // a specific module/layer/service owns the new behavior
            "architecture-preservation",   // preserves existing layer direction, dependency rules, idioms
            "domain-logic-placement",      // business logic stays out of UI/transport/persistence/glue
            "boundary-crossings",          // minimizes the boundaries the change crosses
            "no-detail-leakage",           // no vendor/db/transport/framework/UI leak into unrelated layers
            "interface-justification",     // new interfaces/adapters are real, narrow, stable seams
            "abstraction-justification",   // abstractions justified by concrete volatility/testability/etc.
            "no-architecture-by-fashion",  // no unjustified microservice/event-bus/plugin/CQRS/queue/Redis/factory
            "distributed-architecture",    // distributed arch explains ownership/consistency/deploy/ops
            "refactor-separation",         // preparatory refactoring is separated from behavior change
        ],
    };

    /// <summary>
    /// TEST 04 — DOMAIN INVARIANTS, DATA OWNERSHIP, CONTRACTS, AND MIGRATIONS.
    /// Verifies the plan protects the correctness of business rules, state,
    /// schemas, APIs, events, and cross-version compatibility — every domain
    /// invariant has a named enforcement point, each important fact has one source
    /// of truth (with invalidation defined for any derived/cached/duplicated data),
    /// schema/API/event changes are backward-compatible or justified, migrations
    /// and backfills are idempotent/observable/reversible, and rolling-deploy mixed
    /// versions (old-code/new-schema, new-code/old-data) and duplicate/out-of-order
    /// events are handled rather than assumed away.
    /// </summary>
    public static PlanAuditTest Test04 { get; } = new()
    {
        Id = "04",
        AuditorName = Test04AuditorName,
        Title = "DOMAIN INVARIANTS, DATA OWNERSHIP, CONTRACTS, AND MIGRATIONS",
        Objective =
            "Verify the plan protects correctness of business rules, state, schemas, APIs, events, " +
            "and cross-version compatibility.",
        ReviewGuidance = """
            - What domain invariants must hold, and where is each one enforced?
            - What is the single source of truth for each important fact?
            - What data is derived / cached / duplicated / denormalized, and how is invalidation handled?
            - Are schema changes additive, backward-compatible, and safely deployable?
            - Is an expand-contract (add → backfill → switch → remove) migration used where a breaking
              change is otherwise required?
            - Are migrations and backfills idempotent, resumable, observable, and safe for large datasets?
            - Are public and internal interfaces, events, queues, SDKs, clients, and webhooks kept
              backward-compatible (or is the break explicitly justified with a compatibility path)?
            - What are the transactional boundaries and consistency expectations?
            - Does the plan account for mixed-version operation during a rolling deploy — old code on a
              new schema, and new code on old data — rather than assuming an atomic deploy?
            - Are duplicate events, ordering, idempotency, and replay handled?
            """,
        PassCriteria =
            "Invariants and their enforcement points are explicit; each important fact has one source " +
            "of truth with invalidation defined for derived data; schema/API/event changes are " +
            "backward-compatible or justified; migration/backfill is safe, observable, and reversible; " +
            "and consistency and idempotency rules are clear.",
        FailCriteria =
            "The plan changes persistent data without migration details; duplicates state without an " +
            "invalidation strategy; changes a contract without a compatibility analysis; assumes atomic " +
            "deploys; or ignores old-code/new-schema and new-code/old-data operation.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - risks data corruption, lost records, an irreversible destructive migration, or a broken
              contract that it does not address; or
            - modifies persistent state (on-disk format, DB schema, durable events/logs) without a
              rollback, forward-fix, or compatibility strategy.
            """,
        RequiredFixes = """
            - Name each domain invariant and the exact point at which it is enforced.
            - Identify the source of truth for each important fact and mark what is derived / cached.
            - Add a schema / API / event compatibility plan (additive or expand-contract, or a justified
              break with a compatibility path).
            - Add a migration / backfill / rollback plan that is idempotent, observable, and reversible.
            - State the idempotency and consistency rules (transactional boundaries, duplicate/ordering/replay).
            """,
        Criteria =
        [
            "domain-invariants",           // each invariant named with its enforcement point
            "source-of-truth",             // one authoritative source per important fact
            "derived-data-invalidation",   // derived/cached/duplicated/denormalized data has invalidation
            "schema-compatibility",        // schema changes additive/backward-compatible/safely-deployable
            "expand-contract-migration",   // expand-contract used where a breaking change is otherwise needed
            "migration-safety",            // migration/backfill idempotent, resumable, observable, large-data safe
            "migration-reversibility",     // rollback / forward-fix path for the migration
            "contract-compatibility",      // API/event/queue/SDK/client/webhook backward-compatible or justified
            "transactional-consistency",   // transactional boundaries + consistency expectations explicit
            "mixed-version-operation",     // old-code/new-schema + new-code/old-data during rolling deploys
            "idempotency-ordering",        // duplicate events, ordering, idempotency, replay handled
        ],
    };

    /// <summary>
    /// TEST 05 — SECURITY, PRIVACY, ABUSE CASES, SUPPLY CHAIN, CONFIGURATION, AND
    /// SECRETS. Determines whether the plan identifies the assets, trust
    /// boundaries, and attacker-controlled inputs the change touches; enforces
    /// authorization on the authoritative path (never in UI/client, prompts, or
    /// comments); handles sensitive-data flow, retention, redaction, deletion, and
    /// audit logging; and — for LLM/agent/RAG/tooling features — addresses prompt
    /// injection, untrusted context, excessive agency, tool permissions, secret
    /// exposure, repository exfiltration, poisoned files, and operator/human
    /// approval gates. It also checks that new dependencies are justified
    /// (purpose, maintenance, LICENSE, vulnerability, transitive and alternative
    /// risk) and that config values and secrets are named, stored securely,
    /// rotated, validated at startup, and handled safely when missing. This is a
    /// GENERAL, project-agnostic gate: the full criteria set is kept for every
    /// project (per-project relevance is the auditor on/off toggle); a specific
    /// plan that genuinely does not touch an area self-skips just those criteria
    /// as NOT_APPLICABLE.
    /// </summary>
    public static PlanAuditTest Test05 { get; } = new()
    {
        Id = "05",
        AuditorName = Test05AuditorName,
        Title = "SECURITY, PRIVACY, ABUSE CASES, SUPPLY CHAIN, CONFIGURATION, AND SECRETS",
        Objective =
            "Determine whether the plan identifies assets, trust boundaries, attacker-controlled " +
            "inputs, permissions, dependency risks, and secret/config handling.",
        ReviewGuidance = """
            - What assets, trust boundaries, and new inputs does the change affect?
            - What can a malicious, compromised, or confused actor control (network input, argv, env,
              file/stream contents, IPC/RPC peers, another tenant's data, model/agent/tool output)?
            - Is auth/authz enforced on the server-side / authoritative path and centralized to avoid
              bypass — never only in the UI, client, prompt, or comment?
            - Does every handler acting on a client-supplied id re-verify ownership/role at the moment
              of action, comparing credentials by exact equality?
            - How are sensitive-data flows handled: retention, minimization, logging, redaction, deletion?
            - Are there audit logs for security-relevant actions (authz decisions, admin/privileged
              operations, data access/exports)?
            - FOR LLM / AGENT / RAG / TOOLING FEATURES: does the plan address prompt injection, untrusted
              context, EXCESSIVE AGENCY, tool permissions/scoping, secret exposure, repository
              exfiltration, poisoned files, and operator/human approval gates for high-impact actions?
            - Are new dependencies justified by purpose, maintenance, LICENSE, known vulnerabilities,
              transitive-risk, and alternatives considered?
            - Are config values and secrets named, stored securely, rotated, validated at startup, and
              handled safely when missing or malformed?
            - Are input sizes, loop/recursion depth, buffers, queues, retries, concurrency, and
              decompression ratios bounded before buffering?
            """,
        PassCriteria =
            "A concrete threat model appropriate to the change (assets, trust boundaries, attacker " +
            "inputs, abuse cases, mitigations); controls live in enforceable code paths, not " +
            "conventions; privacy and audit logging are explicit where relevant; and dependencies, " +
            "config, and secrets are justified and managed (stored securely, validated, safe when missing).",
        FailCriteria =
            "Generic security language ('add validation', 'handle securely') with no named boundary or " +
            "control; authorization placed only in UI / client; abuse cases omitted for " +
            "admin / integration / AI-agent / cross-trust flows; secrets hardcoded or vaguely handled; " +
            "or a new dependency added without justification.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - touches auth, permissions, user data, admin operations, files, external integrations, or
              LLM tools WITHOUT a concrete threat model (assets, trust boundaries, attacker inputs,
              abuse cases, mitigations); or
            - relies on LLM behavior, prompt wording, code comments, developer/agent discipline, or
              UI-hiding as a SECURITY boundary (a real boundary is an enforced check in an
              authoritative code path); or
            - risks leaking secrets or sensitive data (committing credentials, logging secrets or
              unredacted sensitive data, exposing them to an untrusted model/tool, or exfiltrating the
              repository).
            """,
        RequiredFixes = """
            - Add a threat model: assets, trust boundaries, attacker-controlled inputs, abuse cases,
              and the mitigation for each.
            - Name the exact authorization checks and the authoritative enforcement point for each
              (server-side / domain path, re-verified per action, exact-equality credential compares).
            - Add negative security tests (rejected-unauthorized, injection-blocked, oversized-input-bounded).
            - Add audit logging for security-relevant actions.
            - Justify each new dependency (purpose, maintenance, LICENSE, vulnerabilities, transitive
              risk, alternatives) and define config/secret handling (named, stored securely, rotated,
              validated at startup, safe when missing).
            - For LLM/agent features: scope tool permissions to least privilege, isolate untrusted
              context from trusted instructions, bound agent authority, and add operator/human approval
              gates for high-impact or irreversible actions.
            """,
        Criteria =
        [
            "assets-trust-boundaries",     // assets, trust boundaries, and new inputs are identified
            "attacker-control",            // what a malicious/compromised/confused actor can control
            "authz-enforcement",           // authz server-side/authoritative-path, centralized, per-action re-check
            "sensitive-data-handling",     // data flow, retention, minimization, deletion of sensitive data
            "logging-redaction",           // no secrets/unredacted sensitive data in logs
            "audit-logging",               // audit logs for security-relevant actions
            "input-bounding",              // input sizes/depth/buffers/retries/concurrency bounded before buffering
            "prompt-injection",            // prompt injection + untrusted context isolated from instructions
            "excessive-agency",            // tool permissions least-privilege, agent authority bounded
            "repo-exfiltration",           // secret exposure, repository exfiltration, poisoned files
            "human-approval-gates",        // operator/human approval gates for high-impact/irreversible actions
            "dependency-justification",    // new deps justified: purpose/maintenance/LICENSE/vuln/transitive/alternatives
            "config-secret-handling",      // config/secrets named, stored securely, rotated, validated, safe-when-missing
            "negative-security-tests",     // negative security tests (unauthorized/injection/oversized)
        ],
    };

    /// <summary>
    /// TEST 06 — RELIABILITY, FAILURE MODES, CONCURRENCY, AND DEGRADATION.
    /// Verifies the plan handles real-world failure rather than assuming the happy
    /// path: primary failure modes are named with mitigations; every external call
    /// is timeout-bounded and retries are capped and backed off; multi-step work is
    /// idempotent (or has a recovery/state model) so it can be safely retried after
    /// a partial success and tolerates duplicate delivery (duplicate message /
    /// duplicate webhook / resubmission); concurrent and duplicate processing,
    /// ordering, and locking / optimistic-concurrency are addressed rather than
    /// assumed away; there is no hidden global or mutable shared state, and no
    /// unsafe singleton lifecycle, without concurrency semantics; background jobs
    /// are retryable, cancellable, observable, and poison-safe with a
    /// dead-letter / repair path; resilience patterns (circuit breakers, rate
    /// limits, bulkheads, queues, fallbacks) are used where needed; and degraded /
    /// user-visible behavior under a dependency outage, slowdown, invalid response,
    /// timeout, or rate-limit is defined. This is a GENERAL, project-agnostic gate:
    /// the full criteria set is kept for every project (per-project relevance is
    /// the auditor on/off toggle); a specific plan that genuinely does not touch an
    /// area self-skips just those criteria as NOT_APPLICABLE with a one-line reason.
    /// </summary>
    public static PlanAuditTest Test06 { get; } = new()
    {
        Id = "06",
        AuditorName = Test06AuditorName,
        Title = "RELIABILITY, FAILURE MODES, CONCURRENCY, AND DEGRADATION",
        Objective =
            "Verify the plan handles real-world failure, partial completion, retries, concurrency, " +
            "and dependency instability rather than assuming the happy path.",
        ReviewGuidance = """
            - What are the primary failure modes of this change, and what mitigates each?
            - What happens if each step only partially succeeds — can the workflow resume, roll back,
              or reconcile, or does it leave inconsistent state behind?
            - Are all external / cross-process calls given explicit timeouts (never unbounded waits)?
            - Are retries bounded, backed off, and jittered — never unbounded or tight-looping?
            - Are operations idempotent under retry, duplicate message, duplicate webhook, and
              resubmission (so a repeat does not double-apply an effect)?
            - Are circuit breakers, rate limits, bulkheads, queues, or fallbacks needed anywhere,
              and does the plan add them where they are?
            - Is user-visible degraded behavior defined (what the caller sees when a dependency is
              down, slow, or rate-limited)?
            - Are background / async jobs retryable, cancellable, observable, and poison-safe, with a
              dead-letter or manual-repair path for messages that never succeed?
            - Are race conditions, locking, optimistic concurrency, duplicate processing, and ordering
              addressed for anything that mutates shared or persistent state?
            - Does the plan avoid hidden global state, unsafe singleton lifecycle, and mutable shared
              state without concurrency semantics?
            - What is the behavior under dependency outage, slowness, invalid response, timeout, or
              rate-limit?
            """,
        PassCriteria =
            "Failure modes and their mitigations are explicit; every external interaction is " +
            "timeout-bounded and retry-safe (bounded, backed-off, idempotent); partial-failure " +
            "recovery and concurrency control are addressed; and degraded / user-visible behavior " +
            "under dependency instability is defined.",
        FailCriteria =
            "The plan assumes the happy path; uses unbounded retries or has no timeout on an external " +
            "call; cannot safely retry after a partial failure; ignores duplicate delivery or " +
            "concurrent updates; or lacks a fallback / user-facing failure behavior.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - lets a security-sensitive, destructive, or persistent-mutation workflow be
              repeated or partially applied unsafely (no idempotency key, no recovery/state
              model, no atomicity); or
            - lets an external dependency failure hang a critical request indefinitely (an
              external / cross-process call with no timeout or bounded wait); or
            - introduces unsafe concurrent mutation of shared or persistent state (no locking,
              optimistic concurrency, or other concurrency control).
            """,
        RequiredFixes = """
            - Add explicit timeout, bounded+backed-off retry, idempotency, and fallback semantics for
              every external / cross-process interaction.
            - Add a state machine or recovery model for multi-step workflows so a partial failure can
              resume, roll back, or reconcile rather than leaving inconsistent state.
            - Add a concurrency-control or locking strategy (lock, optimistic concurrency, or
              single-writer ownership) for anything that mutates shared or persistent state.
            - Add a dead-letter / poison-message / manual-repair path for background jobs, and define
              the degraded, user-visible behavior under a dependency outage/slow/invalid/timeout/rate-limit.
            """,
        Criteria =
        [
            "failure-modes",           // primary failure modes named, each with a mitigation
            "partial-failure",         // partial-success recovery/rollback/reconcile for multi-step work
            "external-timeouts",       // every external/cross-process call is timeout-bounded
            "bounded-retries",         // retries capped, backed off, jittered — never unbounded/tight-loop
            "retry-idempotency",       // idempotent under retry/duplicate-message/duplicate-webhook/resubmission
            "resilience-patterns",     // circuit breakers/rate limits/bulkheads/queues/fallbacks where needed
            "degraded-behavior",       // user-visible degraded behavior under dependency failure defined
            "background-jobs",         // background jobs retryable/cancellable/observable/poison-safe
            "dead-letter-repair",      // dead-letter / poison-message / manual-repair path
            "concurrency-control",     // races/locking/optimistic-concurrency/duplicate-processing/ordering
            "shared-state-safety",     // no hidden global/mutable shared state or unsafe singleton lifecycle
            "dependency-degradation",  // behavior under dependency outage/slow/invalid/timeout/rate-limit
        ],
    };

    /// <summary>
    /// TEST 07 — TEST STRATEGY AND EVIDENCE QUALITY. Determines whether the plan
    /// provides risk-mapped evidence that the change is correct, secure,
    /// compatible, and maintainable — tests are mapped to specific
    /// risks / invariants / contracts / failure-modes rather than a bare "add
    /// tests"; pure decision logic has unit tests; persistence / boundaries /
    /// queues / jobs have integration tests; public and internal
    /// APIs / events / SDKs / webhooks have contract tests; schema / data changes
    /// have migration / backfill tests; the abuse surface (authz, validation,
    /// duplicate processing, malformed input, expired tokens, unsafe LLM / tool
    /// output) has negative + abuse tests; E2E is scoped to critical journeys
    /// rather than substituting for lower-level coverage; scale-sensitive paths
    /// have performance / load tests; test data is deterministic and not coupled
    /// to implementation detail; the plan names which existing tests to update and
    /// which regressions to prevent; and explicit, automated done-criteria connect
    /// the deliverables to the tests that verify them before deployment. This
    /// reviews only whether the plan DECLARES an adequate strategy — actual test
    /// existence / execution is a code-stage concern. This is a GENERAL,
    /// project-agnostic gate: the full criteria set is kept for every project
    /// (per-project relevance is the auditor on/off toggle); a specific plan that
    /// genuinely does not touch an area self-skips just those criteria as
    /// NOT_APPLICABLE with a one-line reason.
    /// </summary>
    public static PlanAuditTest Test07 { get; } = new()
    {
        Id = "07",
        AuditorName = Test07AuditorName,
        Title = "TEST STRATEGY AND EVIDENCE QUALITY",
        Objective =
            "Determine whether the plan provides risk-mapped evidence that the change is correct, " +
            "secure, compatible, and maintainable. Review only whether the plan DECLARES an adequate " +
            "strategy — actual test execution/existence is code-stage.",
        ReviewGuidance = """
            - Are tests mapped to specific risks / invariants / contracts / failure-modes, or does the
              plan just say "add tests" without naming what each test pins down?
            - Are there unit tests for pure decision logic?
            - Are there integration tests for persistence / boundaries / queues / jobs (through the real
              component, not a mock asserting its own calls)?
            - Are there contract tests for public and internal APIs / events / SDKs / webhooks?
            - Are there migration / backfill tests for schema / data changes?
            - Are there negative + abuse tests (authz rejection, input validation, duplicate processing,
              malformed input, expired / invalid tokens, unsafe LLM / tool output) for the relevant risks?
            - Is E2E testing limited to critical journeys rather than used as a substitute for
              lower-level unit / integration / contract coverage?
            - Are there performance / load tests where scale or throughput is a concern?
            - Is the test data deterministic (injected clock, seeded randomness, isolated fixtures) and
              free of brittle implementation-detail assertions?
            - Does the plan name which existing tests to update and which regressions to prevent?
            - Does the plan define explicit, objectively-checkable done-criteria that connect the
              deliverables to the tests / checks / metrics that verify them before deployment?
            """,
        PassCriteria =
            "Tests are specific and risk-based, covering positive, negative, compatibility, migration, " +
            "and failure cases; the main invariants and contracts have direct test evidence (a named " +
            "test that would fail if that behavior broke); and the plan defines how completion is " +
            "verified through automated checks rather than a human sign-off.",
        FailCriteria =
            "The plan merely says 'add tests'; the tests only verify implementation details; " +
            "negative / security / migration / failure tests are missing despite relevant risks; or " +
            "there are no acceptance criteria connected to tests.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - leaves a critical business, security, data-integrity, or contract risk with NO direct
              test evidence (no named test that would fail if that specific behavior broke); or
            - cannot say how correctness is verified before deployment (no acceptance / done criteria
              connected to concrete tests, checks, or metrics).
            """,
        RequiredFixes = """
            - Add a test matrix mapping each test to the specific risk / invariant / contract /
              failure-mode it verifies.
            - Add negative + abuse tests (authz rejection, validation, duplicate processing, malformed
              input, expired tokens, unsafe LLM / tool output) for the relevant risks.
            - Add the missing migration / contract / integration tests for the persistence, schema, and
              boundary changes the plan makes.
            - Add explicit, automated done-criteria that connect each deliverable to the test, check, or
              metric that verifies it before deployment.
            """,
        Criteria =
        [
            "risk-mapped-tests",              // each test mapped to a specific risk/invariant/contract/failure-mode
            "unit-tests-pure-logic",          // unit tests for pure decision logic
            "integration-tests",              // integration tests for persistence/boundaries/queues/jobs
            "contract-tests",                 // contract tests for public/internal APIs/events/SDKs/webhooks
            "migration-tests",                // migration/backfill tests for schema/data changes
            "negative-abuse-tests",           // negative+abuse tests (authz/validation/duplicate/malformed/expired/unsafe-LLM)
            "e2e-scoping",                    // E2E limited to critical journeys, not a substitute for lower-level coverage
            "performance-load-tests",         // performance/load tests where scale is a concern
            "deterministic-test-data",        // deterministic data, no brittle implementation-detail assertions
            "existing-tests-and-regressions", // which existing tests to update, which regressions to prevent
            "done-criteria",                  // explicit automated done-criteria connecting deliverables to tests
        ],
    };

    /// <summary>
    /// TEST 08 — OBSERVABILITY, OPERATIONS, DEBUGGABILITY, AND REPAIRABILITY.
    /// Verifies the plan makes production behavior observable, diagnosable,
    /// supportable, and repairable without ad-hoc heroics: logs added / changed are
    /// structured, tied to the changed behavior, and free of sensitive-data leakage;
    /// success / failure / latency / throughput / retries / queue-depth / error-rate
    /// have metrics where the change affects them; traces or correlation IDs follow a
    /// request / job across services, jobs, and external calls; the critical failure
    /// modes have alerts; support / operators can inspect stuck / failed / in-flight
    /// state without manual database spelunking and can safely retry / cancel /
    /// repair / reconcile a workflow that sticks or partially fails; security / admin /
    /// data-sensitive actions emit audit events; migration / backfill progress and
    /// correctness are verifiable; no critical failure path is silent; and the recovery
    /// steps for common failures are discoverable and automated or operator-facing.
    /// This reviews only whether the plan DECLARES adequate observability and
    /// operability — actual log / metric emission is a code-stage concern. This is a
    /// GENERAL, project-agnostic gate: the full criteria set is kept for every project
    /// (per-project relevance is the auditor on/off toggle); a specific plan that
    /// genuinely does not touch an area self-skips just those criteria as
    /// NOT_APPLICABLE with a one-line reason. The human-process framing of a "runbook"
    /// is reframed to the autonomous-factory equivalent: discoverable, self-documenting
    /// recovery that is automated or operator-facing, never reliant on a human
    /// remembering tribal knowledge.
    /// </summary>
    public static PlanAuditTest Test08 { get; } = new()
    {
        Id = "08",
        AuditorName = Test08AuditorName,
        Title = "OBSERVABILITY, OPERATIONS, DEBUGGABILITY, AND REPAIRABILITY",
        Objective =
            "Verify production behavior can be observed, diagnosed, supported, and repaired without " +
            "ad hoc heroics.",
        ReviewGuidance = """
            - What logs are added or changed — are they structured (queryable, not free-text), tied to
              the changed behavior, and safe from sensitive-data leakage?
            - Are there metrics for success / failure / latency / throughput / retries / queue-depth /
              error-rate where this change introduces or affects them?
            - Are there traces or correlation IDs that follow a request / job across services, jobs, and
              external calls?
            - Are there alerts for the critical failure modes this change introduces?
            - Can support / operators inspect stuck, failed, or in-flight state without manual database
              spelunking?
            - Can operators safely retry, cancel, repair, or reconcile a workflow that sticks or
              partially fails?
            - Are there audit events for security-relevant, admin, and data-sensitive actions?
            - If the change includes a migration / backfill, how is its progress and correctness verified?
            - What does failure look like in production — is every critical failure path observable rather
              than silent?
            - Are the recovery steps for common failures discoverable and automated or operator-facing,
              rather than tribal knowledge a human must remember?
            """,
        PassCriteria =
            "Observability signals (structured logs, metrics, traces / correlation IDs, alerts) are tied " +
            "to the changed behavior and its failure modes; workflows that can stick or partially fail " +
            "have an operator inspect + retry / cancel / repair / reconcile path; audit events cover " +
            "security / admin / data-sensitive actions; and logs and diagnostics are useful and " +
            "privacy-safe.",
        FailCriteria =
            "There is no way to know whether the change works in production; debugging a failure requires " +
            "manual database spelunking; security- or billing-relevant actions lack auditability; there " +
            "is no repair path for partial failure; or sensitive data would be logged or exposed through " +
            "diagnostics.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - lets a critical workflow fail silently (a failure path with no log, metric, alert, or other
              signal that would reveal it in production); or
            - leaves operators unable to detect or repair stuck / partially-applied / provisioning /
              user-impacting state (no way to inspect it and no safe retry / cancel / reconcile / repair
              path); or
            - would log or expose sensitive data through diagnostics (secrets or unredacted sensitive data
              in logs, traces, error responses, or debug endpoints).
            """,
        RequiredFixes = """
            - Add structured logs, metrics, and traces / correlation IDs tied to the changed behavior and
              its failure modes (success / failure / latency / throughput / retries / queue-depth /
              error-rate), plus alerts for the critical failure modes.
            - Add audit events for security-relevant, admin, and data-sensitive actions.
            - Add an admin / operator repair path to inspect and safely retry / cancel / reconcile stuck
              or partially-applied state.
            - Define discoverable recovery steps for common failures — self-documenting and automated or
              operator-facing detection + recovery, not reliant on a human remembering a runbook.
            - Make migration / backfill progress and correctness verifiable, and ensure no failure path is
              silent or leaks sensitive data through diagnostics.
            """,
        Criteria =
        [
            "structured-logs",            // logs added/changed are structured, queryable, tied to changed behavior
            "diagnostic-privacy-safety",  // logs/traces/error-responses/debug endpoints never leak secrets/sensitive data
            "metrics",                    // success/failure/latency/throughput/retries/queue-depth/error-rate metrics
            "tracing-correlation",        // traces or correlation IDs across services/jobs/external calls
            "alerting",                   // alerts for the critical failure modes the change introduces
            "state-inspection",           // support/operators can inspect stuck/failed/in-flight state (no db spelunking)
            "repair-reconcile",           // safe operator retry/cancel/repair/reconcile for stuck/partial state
            "audit-events",               // audit events for security/admin/data-sensitive actions
            "migration-observability",    // migration/backfill progress + correctness are verifiable
            "silent-failure-visibility",  // no critical failure path is silent; failure is observable in production
            "recovery-procedure",         // discoverable, automated-or-operator-facing recovery for common failures
        ],
    };

    /// <summary>
    /// Every built-in plan-audit chain test, in chain order. The DI registration
    /// and <c>ProjectAuditorComposer</c> auto-inclusion both iterate this list,
    /// so adding a chain test here wires it everywhere without touching either
    /// call site (one source of truth for the chain membership).
    /// </summary>
    public static IReadOnlyList<PlanAuditTest> All { get; } = [Test01, Test02, Test03, Test04, Test05, Test06, Test07, Test08];
}
