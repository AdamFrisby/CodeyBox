namespace CodeyBox.Core;

/// <summary>
/// A "knob" is a small, registered tuning directive that can be attached
/// per work item or per project to nudge the agent's behaviour. The framework
/// is intentionally minimalist: a knob is identified by its <see cref="Key"/>,
/// constrained by its <see cref="AllowedValues"/> set, and contributes its
/// behaviour via per-phase hooks (currently just
/// <see cref="GetWorkPromptFragment"/>).
///
/// <para>
/// Adding a new knob is a localised change: implement
/// <see cref="IKnob"/>, register it as a DI singleton, and that knob is
/// immediately visible to the API (set/validate), persisted on work items,
/// and consulted by the work-prompt assembly seam — no edit to the pipeline
/// core required.
/// </para>
///
/// <para>
/// Knobs intentionally do NOT carry runtime behaviour beyond the prompt seam:
/// they are operator-facing dials, not plug-in handlers. Future seams (audit
/// prompt fragments, merge strategy hints, post-merge behaviour, …) plug in
/// here by adding optional methods to this interface with default
/// implementations that contribute nothing — existing knobs need no edits.
/// </para>
/// </summary>
public interface IKnob
{
    /// <summary>
    /// Canonical key used in storage and the API. Lower-case camelCase by
    /// convention (e.g. <c>changeScope</c>). Comparisons are case-insensitive.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Operator-facing description shown in API discovery responses. Keep this
    /// short; details belong in <c>docs/knobs.md</c>.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The enumeration of values this knob accepts. Comparisons are
    /// case-insensitive. An empty list means "any non-null string accepted".
    /// </summary>
    IReadOnlyList<string> AllowedValues { get; }

    /// <summary>
    /// Value used when neither the work item nor the project sets the knob.
    /// MUST itself be a member of <see cref="AllowedValues"/> when that list
    /// is non-empty.
    /// </summary>
    string DefaultValue { get; }

    /// <summary>
    /// Optional fragment appended to the work-phase prompt for the given
    /// effective <paramref name="value"/>. Return <c>null</c> or whitespace
    /// when this value contributes nothing (e.g. when the value names the
    /// existing default behaviour and would just clutter the prompt).
    /// </summary>
    string? GetWorkPromptFragment(string value);
}

/// <summary>
/// Outcome of validating a knob assignment against the registry.
/// </summary>
public readonly record struct KnobValidationResult(bool Ok, string? Error)
{
    public static KnobValidationResult Success { get; } = new(true, null);
    public static KnobValidationResult Fail(string error) => new(false, error);
}

/// <summary>
/// Registry of all configured <see cref="IKnob"/> implementations. Used by:
/// <list type="bullet">
///   <item>The API to validate per-item and per-project knob assignments at
///   set-time and to surface knob discovery to operators.</item>
///   <item>The prompt-assembly seam to resolve the effective knob map and
///   gather each knob's contributed prompt fragment.</item>
/// </list>
/// </summary>
public interface IKnobRegistry
{
    /// <summary>All registered knobs, ordered by <see cref="IKnob.Key"/>.</summary>
    IReadOnlyList<IKnob> All { get; }

    /// <summary>
    /// Looks up a knob by key (case-insensitive). Returns <c>true</c> and
    /// populates <paramref name="knob"/> when found; otherwise <c>false</c>.
    /// </summary>
    bool TryGet(string key, out IKnob knob);

    /// <summary>
    /// Validates one (key, value) pair against the registry. The first failure
    /// reason is returned verbatim — surface it directly in API error
    /// responses.
    /// </summary>
    KnobValidationResult Validate(string key, string value);

    /// <summary>
    /// Validates an entire proposed map. Returns the first failure encountered.
    /// Iteration order over the input dictionary is preserved so the error
    /// names the FIRST offending key, which makes operator triage simpler.
    /// </summary>
    KnobValidationResult ValidateAll(IReadOnlyDictionary<string, string>? proposed);

    /// <summary>
    /// Resolves the effective value of every registered knob using the
    /// documented precedence:
    /// <list type="number">
    ///   <item>per-item <paramref name="itemKnobs"/></item>
    ///   <item>per-project <paramref name="projectKnobs"/></item>
    ///   <item>knob <see cref="IKnob.DefaultValue"/></item>
    /// </list>
    /// Unknown keys in either input map are ignored — the API path validates
    /// them at set-time, so reaching this method with an unknown key means a
    /// knob was unregistered after the value was persisted; resolution falls
    /// back to per-project / default rather than failing the pipeline.
    /// </summary>
    IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs);
}
