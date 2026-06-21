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
    public void Normalize_ReturnsCanonicalKeyAndValue()
    {
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate"])]);

        var result = registry.Normalize("CHANGESCOPE", "SURGICAL");

        Assert.True(result.Ok);
        Assert.Equal("changeScope", result.Key);
        Assert.Equal("surgical", result.Value);
        Assert.Equal("surgical", result.TypedValue);
    }

    [Fact]
    public void Normalize_EnumValuesAreTrimmedByRegistryParser()
    {
        var registry = new KnobRegistry([new TestEnumKnob("changeScope", "moderate", ["surgical", "moderate"])]);

        var result = registry.Normalize("changeScope", " surgical ");

        Assert.True(result.Ok);
        Assert.Equal("surgical", result.Value);
    }

    [Fact]
    public void Normalize_NullValue_IsRejected()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        var result = registry.Normalize("shape", null!);

        Assert.False(result.Ok);
        Assert.Contains("must not be null", result.Error);
    }

    [Fact]
    public void ValidateAll_NullOrEmptyMap_Succeeds()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);

        Assert.True(registry.ValidateAll(null).Ok);
        Assert.True(registry.ValidateAll(new Dictionary<string, string>()).Ok);
    }

    [Fact]
    public void ValidateAll_ReturnsFirstErrorInInputOrder()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "round", ["round", "square"])]);
        var proposed = new Dictionary<string, string>
        {
            ["unknown"] = "value",
            ["shape"] = "triangle",
        };

        var result = registry.ValidateAll(proposed);

        Assert.False(result.Ok);
        Assert.Contains("unknown knob 'unknown'", result.Error);
        Assert.DoesNotContain("triangle", result.Error);
    }

    [Fact]
    public void TryGetTypedValue_ReturnsParsedValueForTypedKnob()
    {
        var registry = new KnobRegistry([new TestBooleanKnob("strict", defaultValue: false)]);
        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["strict"] = "TRUE" },
            projectKnobs: null);

        var found = registry.TryGetTypedValue<bool>(effective, "strict", out var strict);

        Assert.True(found);
        Assert.True(strict);
        Assert.Equal("true", effective["strict"]);
    }

    [Fact]
    public void TryGetTypedValue_ReturnsFalseForUnknownAbsentInvalidOrWrongClrType()
    {
        var registry = new KnobRegistry([new TestBooleanKnob("strict", defaultValue: false)]);
        var effective = registry.Resolve(
            itemKnobs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["strict"] = "TRUE" },
            projectKnobs: null);

        Assert.False(registry.TryGetTypedValue<bool>(effective, "unknown", out _));
        Assert.False(registry.TryGetTypedValue<bool>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "strict",
            out _));
        Assert.False(registry.TryGetTypedValue<bool>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["strict"] = "yes" },
            "strict",
            out _));
        Assert.False(registry.TryGetTypedValue<string>(effective, "strict", out _));
    }

    [Fact]
    public void BooleanParser_RejectsInvalidValue()
    {
        var registry = new KnobRegistry([new TestBooleanKnob("strict", defaultValue: false)]);

        var result = registry.Validate("strict", "yes");

        Assert.False(result.Ok);
        Assert.Contains("true or false", result.Error);
    }

    [Fact]
    public void IntegerParser_CanonicalisesInvariantInteger_AndRejectsNonInteger()
    {
        var registry = new KnobRegistry([new TestBuiltInKnob("limit", "0", KnobValueType.Integer, typeof(long))]);

        var ok = registry.Normalize("limit", " 42 ");
        var bad = registry.Validate("limit", "4.2");

        Assert.True(ok.Ok);
        Assert.Equal("42", ok.Value);
        Assert.Equal(42L, ok.TypedValue);
        Assert.False(bad.Ok);
        Assert.Contains("integer", bad.Error);
    }

    [Fact]
    public void DecimalParser_CanonicalisesInvariantDecimal_AndRejectsCommaDecimal()
    {
        var registry = new KnobRegistry([new TestBuiltInKnob("ratio", "0", KnobValueType.Decimal, typeof(decimal))]);

        var ok = registry.Normalize("ratio", " 1.25 ");
        var bad = registry.Validate("ratio", "1,25");

        Assert.True(ok.Ok);
        Assert.Equal("1.25", ok.Value);
        Assert.Equal(1.25m, ok.TypedValue);
        Assert.False(bad.Ok);
        Assert.Contains("decimal", bad.Error);
    }

    [Fact]
    public void JsonParser_ValidatesJson_AndReturnsJsonElement()
    {
        var registry = new KnobRegistry([new TestBuiltInKnob("payload", "{}", KnobValueType.Json, typeof(System.Text.Json.JsonElement))]);

        var ok = registry.Normalize("payload", """{ "enabled": true }""");
        var bad = registry.Validate("payload", "not-json");

        Assert.True(ok.Ok);
        Assert.Equal("""{ "enabled": true }""", ok.Value);
        var element = Assert.IsType<System.Text.Json.JsonElement>(ok.TypedValue);
        Assert.True(element.GetProperty("enabled").GetBoolean());
        Assert.False(bad.Ok);
        Assert.Contains("valid JSON", bad.Error);
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
    public void NullDescriptor_ThrowsAtRegistryConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new KnobRegistry([null!]));
    }

    [Fact]
    public void EmptyAllowedValuesEntry_ThrowsAtRegistryConstruction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KnobRegistry(
        [
            new TestEnumKnob("shape", "round", ["round", " "]),
        ]));
        Assert.Contains("empty AllowedValues entry", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKey_ThrowsAtRegistryConstruction(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KnobRegistry(
        [
            new TestEnumKnob(key, "a", ["a", "b"]),
        ]));
        Assert.Contains("empty Key", ex.Message);
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
    public void Resolve_CanonicalisesDescriptorDefaultValue()
    {
        var registry = new KnobRegistry([new TestEnumKnob("shape", "ROUND", ["round", "square"])]);

        var effective = registry.Resolve(itemKnobs: null, projectKnobs: null);

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
        // the descriptor parser validates the value — useful for free-form
        // knobs (numeric budgets, paths, etc.). The default string parser
        // accepts arbitrary non-empty input and Resolve returns it verbatim.
        var registry = new KnobRegistry([new TestEnumKnob("freeForm", "fallback", [])]);

        Assert.True(registry.Validate("freeForm", "arbitrary-string").Ok);
        Assert.True(registry.Validate("freeForm", "Another-VALUE").Ok);
        Assert.False(registry.Validate("freeForm", "").Ok);
        Assert.False(registry.Validate("freeForm", "   ").Ok);

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

    private sealed class TestBooleanKnob : IKnob
    {
        public TestBooleanKnob(string key, bool defaultValue)
        {
            Key = key;
            DefaultValue = defaultValue ? "true" : "false";
        }

        public string Key { get; }
        public string Description => $"test bool knob '{Key}'";
        public KnobValueType ValueType => KnobValueType.Boolean;
        public Type ClrType => typeof(bool);
        public IReadOnlyList<string> AllowedValues => [];
        public string DefaultValue { get; }
        public string? GetWorkPromptFragment(string value) => null;
    }

    private sealed class TestBuiltInKnob : IKnob
    {
        public TestBuiltInKnob(string key, string defaultValue, KnobValueType valueType, Type clrType)
        {
            Key = key;
            DefaultValue = defaultValue;
            ValueType = valueType;
            ClrType = clrType;
        }

        public string Key { get; }
        public string Description => $"test {ValueType} knob '{Key}'";
        public KnobValueType ValueType { get; }
        public Type ClrType { get; }
        public IReadOnlyList<string> AllowedValues => [];
        public string DefaultValue { get; }
        public string? GetWorkPromptFragment(string value) => null;
    }
}
