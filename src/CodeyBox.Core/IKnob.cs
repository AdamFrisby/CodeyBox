namespace CodeyBox.Core;

/// <summary>
/// Built-in value shapes the knob registry knows how to normalise and parse.
/// Descriptors with specialised semantics can override
/// <see cref="IKnob.ParseValue"/> while still advertising their high-level
/// value type here for discovery and validation.
/// </summary>
public enum KnobValueType
{
    String,
    Enum,
    Boolean,
    Integer,
    Decimal,
    Json,
}

/// <summary>
/// Optional lifecycle phases a knob can request from the pipeline. The pipeline
/// consumes this framework-level shape rather than importing individual knob
/// descriptor implementations.
/// </summary>
[Flags]
public enum KnobPipelineLifecycle
{
    None = 0,
    Planning = 1 << 0,
}

/// <summary>
/// Result of parsing a knob's string storage value into the descriptor's typed
/// value. <see cref="CanonicalValue"/> is the string written back to storage;
/// <see cref="TypedValue"/> is used by typed accessors.
/// </summary>
public readonly record struct KnobValueParseResult(
    bool Ok,
    string? CanonicalValue,
    object? TypedValue,
    string? Error)
{
    public static KnobValueParseResult Success(string canonicalValue, object typedValue) =>
        new(true, canonicalValue, typedValue, null);

    public static KnobValueParseResult Fail(string error) =>
        new(false, null, null, error);
}

/// <summary>
/// Result of normalising a caller-supplied knob assignment against the
/// registry. Successful results carry the canonical key and value that should
/// be persisted.
/// </summary>
public readonly record struct KnobNormalizationResult(
    bool Ok,
    string? Key,
    string? Value,
    object? TypedValue,
    string? Error)
{
    public static KnobNormalizationResult Success(string key, string value, object typedValue) =>
        new(true, key, value, typedValue, null);

    public static KnobNormalizationResult Fail(string error) =>
        new(false, null, null, null, error);
}

/// <summary>
/// A "knob" is a small, registered tuning directive that can be attached
/// per work item or per project to nudge the agent's behaviour. The framework
/// is intentionally minimalist: a knob is identified by its <see cref="Key"/>,
/// constrained by its typed value parser, and contributes its behaviour via
/// optional framework-level hooks such as <see cref="GetWorkPromptFragment"/>,
/// <see cref="GetAuditPromptFragment"/>, and <see cref="GetPipelineLifecycle"/>.
///
/// <para>
/// Adding a new knob is a localised change: implement
/// <see cref="IKnob"/>, register it as a DI singleton, and that knob is
/// immediately visible to the API (set/validate), persisted on work items,
/// and consulted by the prompt-assembly seam — no edit to the pipeline
/// core required.
/// </para>
///
/// <para>
/// Knobs remain operator-facing dials, not arbitrary plug-in handlers. Runtime
/// effects are limited to the explicit framework hooks on this interface, such
/// as per-phase prompt fragments (work + audit) and pipeline lifecycle
/// requests. Future seams (merge strategy hints, post-merge behaviour, ...)
/// plug in here by adding optional methods with default implementations that
/// contribute nothing — existing knobs need no edits.
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
    /// Operator-facing description. Keep this short; details belong in
    /// <c>docs/reference/knobs.md</c>.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// High-level value shape for this knob. When <see cref="AllowedValues"/>
    /// is non-empty, the default is <see cref="KnobValueType.Enum"/>; otherwise
    /// the default is <see cref="KnobValueType.String"/>.
    /// </summary>
    KnobValueType ValueType => AllowedValues.Count > 0
        ? KnobValueType.Enum
        : KnobValueType.String;

    /// <summary>
    /// CLR type returned by <see cref="ParseValue"/> and
    /// <see cref="IKnobRegistry.TryGetTypedValue{T}"/>. Built-in enum/string
    /// descriptors return <see cref="string"/> by default.
    /// </summary>
    Type ClrType => typeof(string);

    /// <summary>
    /// The finite enumeration of values this knob accepts. Comparisons are
    /// case-insensitive and successful normalisation persists the registered
    /// casing. An empty list means the descriptor's <see cref="ParseValue"/>
    /// hook validates the value instead.
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

    /// <summary>
    /// Optional lifecycle phases requested by this knob for the given effective
    /// value. The default preserves the current pipeline lifecycle.
    /// </summary>
    KnobPipelineLifecycle GetPipelineLifecycle(string value) => KnobPipelineLifecycle.None;

    /// <summary>
    /// Optional fragment appended to every audit-phase prompt for the given
    /// effective <paramref name="value"/>. Lets a knob shape what the
    /// auditors weigh blast-radius / breadth / scope-creep as on this item
    /// without editing the auditor's own focus list. Return <c>null</c> or
    /// whitespace when this value contributes nothing — same contract as
    /// <see cref="GetWorkPromptFragment"/>. Default implementation returns
    /// <c>null</c> so existing knobs need no edits.
    /// </summary>
    string? GetAuditPromptFragment(string value) => null;

    /// <summary>
    /// Free-form values can be operator/user controlled. Leave this false
    /// unless <see cref="GetWorkPromptFragment"/> and
    /// <see cref="GetAuditPromptFragment"/> either never emit the raw
    /// free-form value or explicitly delimit/encode it as untrusted data.
    /// The prompt preprocessor enforces this when a free-form descriptor
    /// contributes a fragment.
    /// </summary>
    bool AllowsFreeFormPromptFragments => false;

    /// <summary>
    /// Normalises and parses one storage value. Descriptors can override this
    /// to add ranges, custom numeric parsing, structured values, or a domain
    /// enum while keeping validation local to the descriptor.
    /// </summary>
    KnobValueParseResult ParseValue(string value) =>
        KnobValueParsers.ParseBuiltIn(this, value);
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
    /// Validates and normalises one (key, value) pair. Successful results carry
    /// the canonical key/value that should be persisted.
    /// </summary>
    KnobNormalizationResult Normalize(string key, string value);

    /// <summary>
    /// Validates an entire proposed map. Returns the first failure encountered.
    /// Iteration order over the input dictionary is preserved so the error
    /// names the FIRST offending key, which makes operator triage simpler.
    /// </summary>
    KnobValidationResult ValidateAll(IReadOnlyDictionary<string, string>? proposed);

    /// <summary>
    /// Reads a typed value from an already-resolved knob map. Returns
    /// <c>false</c> when the key is unknown, absent, invalid for the current
    /// descriptor, or the parsed value is not assignable to <typeparamref name="T"/>.
    /// </summary>
    bool TryGetTypedValue<T>(
        IReadOnlyDictionary<string, string> resolved,
        string key,
        out T value);

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

internal static class KnobValueParsers
{
    public static KnobValueParseResult ParseBuiltIn(IKnob knob, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return KnobValueParseResult.Fail($"knob '{knob.Key}' value must not be empty");

        var trimmed = value.Trim();
        if (knob.AllowedValues.Count > 0)
        {
            foreach (var allowed in knob.AllowedValues)
            {
                if (string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase))
                    return KnobValueParseResult.Success(allowed, allowed);
            }

            return KnobValueParseResult.Fail(
                $"knob '{knob.Key}' value '{value}' is not allowed. Allowed values: " +
                $"{string.Join(", ", knob.AllowedValues)}");
        }

        return knob.ValueType switch
        {
            KnobValueType.String => KnobValueParseResult.Success(trimmed, trimmed),
            KnobValueType.Boolean => bool.TryParse(trimmed, out var b)
                ? KnobValueParseResult.Success(b ? "true" : "false", b)
                : KnobValueParseResult.Fail($"knob '{knob.Key}' value '{value}' must be true or false"),
            KnobValueType.Integer => long.TryParse(
                    trimmed,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var i)
                ? KnobValueParseResult.Success(i.ToString(System.Globalization.CultureInfo.InvariantCulture), i)
                : KnobValueParseResult.Fail($"knob '{knob.Key}' value '{value}' must be an integer"),
            KnobValueType.Decimal => decimal.TryParse(
                    trimmed,
                    System.Globalization.NumberStyles.AllowLeadingSign |
                    System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d)
                ? KnobValueParseResult.Success(d.ToString(System.Globalization.CultureInfo.InvariantCulture), d)
                : KnobValueParseResult.Fail($"knob '{knob.Key}' value '{value}' must be a decimal number"),
            KnobValueType.Json => ParseJson(knob, trimmed, value),
            _ => KnobValueParseResult.Success(trimmed, trimmed),
        };
    }

    private static KnobValueParseResult ParseJson(IKnob knob, string trimmed, string original)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(trimmed);
            return KnobValueParseResult.Success(trimmed, document.RootElement.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return KnobValueParseResult.Fail($"knob '{knob.Key}' value '{original}' must be valid JSON");
        }
    }
}
