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
    /// Every built-in plan-audit chain test, in chain order. The DI registration
    /// and <c>ProjectAuditorComposer</c> auto-inclusion both iterate this list,
    /// so adding a chain test here wires it everywhere without touching either
    /// call site (one source of truth for the chain membership).
    /// </summary>
    public static IReadOnlyList<PlanAuditTest> All { get; } = [Test01, Test02, Test03, Test04];
}
