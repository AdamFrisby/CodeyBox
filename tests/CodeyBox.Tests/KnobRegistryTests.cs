using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class KnobRegistryTests
{
    [Fact]
    public void Resolve_FallsBackToKnobDefault_WhenNeitherItemNorProjectSetsValue()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var effective = registry.Resolve(itemKnobs: null, projectKnobs: null);

        Assert.Equal("round", Assert.Single(effective).Value);
    }

    [Fact]
    public void Resolve_ProjectDefaultWinsOverKnobDefault()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var effective = registry.Resolve(
            itemKnobs: null,
            projectKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "square" });

        Assert.Equal("square", effective["shape"]);
    }

    [Fact]
    public void Resolve_PerItemWinsOverProjectDefault()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "round" },
            projectKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "square" });

        Assert.Equal("round", effective["shape"]);
    }

    [Fact]
    public void Resolve_KeyLookupIsCaseInsensitive_AndCanonicalisesValueToAllowedValueCasing()
    {
        // Keys submitted in mixed case should still resolve; values should be
        // returned in the canonical casing declared by the knob so prompt
        // fragments are stable regardless of how operators typed the value.
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate", "refactor"])]);

        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CHANGESCOPE"] = "SURGICAL" },
            projectKnobs: null);

        Assert.Equal("surgical", effective["changeScope"]);
    }

    [Fact]
    public void Validate_RejectsUnknownKey_WithClearError()
    {
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate"])]);

        var result = registry.Validate("notARealKnob", "anything");

        Assert.False(result.Ok);
        Assert.Contains("unknown knob 'notARealKnob'", result.Error);
        Assert.Contains("changeScope", result.Error);
    }

    [Fact]
    public void Validate_RejectsValueOutsideAllowedValues()
    {
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate"])]);

        var result = registry.Validate("changeScope", "yolo");

        Assert.False(result.Ok);
        Assert.Contains("not allowed", result.Error);
        Assert.Contains("surgical", result.Error);
        Assert.Contains("moderate", result.Error);
    }

    [Fact]
    public void Validate_AcceptsValueInAllowedValuesCaseInsensitively()
    {
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate"])]);

        Assert.True(registry.Validate("changeScope", "SURGICAL").Ok);
        Assert.True(registry.Validate("CHANGESCOPE", "moderate").Ok);
    }

    [Fact]
    public void DuplicateKnobKey_ThrowsAtRegistryConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new KnobRegistry(
        [
            new TestEnumKnob("dup", "a", ["a", "b"]),
            new TestEnumKnob("dup", "b", ["a", "b"]),
        ]));
    }

    [Fact]
    public void DefaultValueOutsideAllowedValues_ThrowsAtRegistryConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new KnobRegistry(
        [
            new TestEnumKnob("bogus", "not-in-list", ["one", "two"]),
        ]));
    }

    [Fact]
    public void Resolve_DropsPersistedValueThatNoLongerSatisfiesAllowedValues()
    {
        // A knob's AllowedValues changed in code after a value was persisted.
        // Reaching resolution with a stale value must fall through to the
        // project / knob default — never propagate the orphan value.
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "octagon" },
            projectKnobs: null);

        Assert.Equal("round", effective["shape"]);
    }

    [Fact]
    public void Resolve_StaleItemValue_FallsThroughToProjectDefault_NotKnobDefault()
    {
        // Item carries a value that is no longer in AllowedValues. The
        // documented precedence (item > project default > knob default)
        // requires the project default to win — the stale item must NOT
        // short-circuit straight to the knob default.
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "octagon" },
            projectKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shape"] = "square" });

        Assert.Equal("square", effective["shape"]);
    }

    [Fact]
    public void EmptyAllowedValues_AcceptsAnyNonEmptyString_AndResolvesVerbatim()
    {
        // The IKnob contract documents that an empty AllowedValues list means
        // "any non-null string accepted" — useful for free-form knobs (numeric
        // budgets, paths, etc.). Validate accepts arbitrary input and Resolve
        // returns the persisted casing verbatim (no canonicalisation since
        // there's no allowed-value list to canonicalise against).
        var registry = new KnobRegistry([new TestEnumKnob("freeForm", "fallback", [])]);

        Assert.True(registry.Validate("freeForm", "arbitrary-string").Ok);
        Assert.True(registry.Validate("freeForm", "Another-VALUE").Ok);

        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["freeForm"] = "Mixed-Case-Verbatim" },
            projectKnobs: null);

        Assert.Equal("Mixed-Case-Verbatim", effective["freeForm"]);
    }

    [Fact]
    public void TryGet_ReturnsRegisteredKnob_AndFalseForUnknownOrEmpty()
    {
        var registered = new TestEnumKnob("shape", "round", ["round", "square"]);
        var registry = new KnobRegistry([registered]);

        Assert.True(registry.TryGet("shape", out var hit));
        Assert.Same(registered, hit);

        Assert.True(registry.TryGet("SHAPE", out var hitMixedCase));
        Assert.Same(registered, hitMixedCase);

        Assert.False(registry.TryGet("nope", out _));
        Assert.False(registry.TryGet(string.Empty, out _));
    }

    private sealed class TestEnumKnob : IKnob
    {
        public TestEnumKnob(string key, string defaultValue, IReadOnlyList<string> allowedValues)
        {
            Key = key;
            DefaultValue = defaultValue;
            AllowedValues = allowedValues;
        }

        public string Key { get; }
        public string Description => $"test knob '{Key}'";
        public IReadOnlyList<string> AllowedValues { get; }
        public string DefaultValue { get; }
        public string? GetWorkPromptFragment(string value) => $"applied-{value}";
    }
}
