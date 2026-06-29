using CodeyBox.Core;

namespace CodeyBox.Orchestrator.Knobs;

/// <summary>
/// First built-in knob. Lets operators bound how aggressively the agent
/// restructures adjacent code while doing the requested change. The
/// <c>moderate</c> value matches today's default agent behaviour, so it
/// contributes no prompt fragment — the assembled prompt is identical to
/// pre-knob output when the knob is unset or set to the default.
///
/// <para>
/// Wiring scope: this knob shapes the WORK-phase prompt (instructs the
/// agent), the AUDIT-phase prompt (instructs every LLM auditor how to
/// weigh blast radius), and the MERGE phase (telemetry tagging on merge
/// timing/log events; the agentic conflict resolver carries the value as
/// context). The merge-phase scheduling bias for refactor pile-ups in hot
/// files is provided today by <see cref="JobType.Refactor"/>'s
/// project-exclusive dispatcher gate, not by this knob.
/// </para>
/// </summary>
public sealed class ChangeScopeKnob : IKnob
{
    public const string KeyName = "changeScope";

    public const string ValueSurgical = "surgical";
    public const string ValueModerate = "moderate";
    public const string ValueRefactor = "refactor";

    public string Key => KeyName;

    public string Description =>
        "How aggressively the agent may restructure adjacent code while making the requested change. " +
        "surgical = smallest possible diff; moderate = default behaviour; refactor = restructure permitted.";

    public IReadOnlyList<string> AllowedValues { get; } =
        [ValueSurgical, ValueModerate, ValueRefactor];

    public string DefaultValue => ValueModerate;

    public string? GetWorkPromptFragment(string value)
    {
        if (string.Equals(value, ValueSurgical, StringComparison.OrdinalIgnoreCase))
        {
            return
                "Change scope: SURGICAL. Make the smallest change that satisfies the task. " +
                "Touch only the code that is strictly required. Do not refactor adjacent code, " +
                "rename surrounding identifiers, reformat unrelated files, or restructure modules. " +
                "Prefer a tight diff that is easy to review and easy to merge cleanly over a tidy " +
                "diff that touches more files than strictly necessary.";
        }

        if (string.Equals(value, ValueRefactor, StringComparison.OrdinalIgnoreCase))
        {
            return
                "Change scope: REFACTOR. You may restructure or re-architect the affected area " +
                "to do this well, even when the resulting diff is larger and harder to merge. " +
                "Untangle adjacent code that is in the way, rename for clarity, and split or " +
                "consolidate modules where it materially improves the result. Do not let " +
                "merge-friendliness or minimal-diff aesthetics constrain the design choice.";
        }

        // ValueModerate (or any unmapped value) contributes nothing — this is
        // the existing default behaviour, and a knob "with nothing to say
        // contributes nothing" per the framework contract.
        return null;
    }

    public string? GetAuditPromptFragment(string value)
    {
        if (string.Equals(value, ValueSurgical, StringComparison.OrdinalIgnoreCase))
        {
            return
                "Change scope: SURGICAL. This work item was scoped to the smallest possible change. " +
                "Minimise blast radius: flag any out-of-scope refactor, adjacent rewrites, broadened renames, " +
                "reformatting of unrelated files, or restructuring that goes beyond the strictly-required code as a finding. " +
                "Scope inflation IS a defect for this item — surface it even when the new code is otherwise correct. " +
                "A tight diff that is easy to merge cleanly is preferred over a tidy diff that touches more files than necessary.";
        }

        if (string.Equals(value, ValueRefactor, StringComparison.OrdinalIgnoreCase))
        {
            return
                "Change scope: REFACTOR. This work item was permitted to restructure or re-architect the affected area. " +
                "Do NOT penalise breadth, adjacent rewrites, renames, module splits/consolidations, or a larger and " +
                "harder-to-merge diff per se — those are explicitly in scope. An architecture-focused auditor may even " +
                "expect material structural improvement here. Focus on whether the restructuring is principled and correct, " +
                "not on whether the diff could have been smaller.";
        }

        return null;
    }

}
