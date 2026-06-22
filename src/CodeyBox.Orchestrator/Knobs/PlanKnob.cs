using CodeyBox.Core;

namespace CodeyBox.Orchestrator.Knobs;

/// <summary>
/// Gates the optional planning-only lifecycle phase before implementation. The
/// default value preserves the pre-planning pipeline exactly.
/// </summary>
public sealed class PlanKnob : IKnob
{
    public const string KeyName = "plan";

    public const string ValueOff = "off";
    public const string ValueOn = "on";

    public string Key => KeyName;

    public string Description =>
        "Whether to run a planning-only agent turn before implementation. off = current pipeline; on = produce and approve a stored plan first.";

    public IReadOnlyList<string> AllowedValues { get; } = [ValueOff, ValueOn];

    public string DefaultValue => ValueOff;

    public string? GetWorkPromptFragment(string value) => null;
}
