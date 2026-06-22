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
/// Wiring scope (intentional): this knob only adjusts the WORK-PHASE prompt.
/// Audit-side enforcement (e.g. an auditor that flags out-of-scope edits
/// when <c>surgical</c>) and merge-friendliness gating are tracked as
/// separate dependent items so the prompt change can ship and be evaluated
/// in isolation.
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
}
