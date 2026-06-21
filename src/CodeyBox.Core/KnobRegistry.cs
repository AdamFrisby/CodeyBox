namespace CodeyBox.Core;

/// <summary>
/// Default <see cref="IKnobRegistry"/> implementation. Constructed once from
/// the DI-injected set of <see cref="IKnob"/> singletons, then immutable for
/// the lifetime of the process. Hot reload is intentionally not supported:
/// knobs are code, not config; restart the host to add a new knob.
/// </summary>
public sealed class KnobRegistry : IKnobRegistry
{
    private readonly Dictionary<string, IKnob> _byKey;
    private readonly IReadOnlyList<IKnob> _ordered;

    public KnobRegistry(IEnumerable<IKnob> knobs)
    {
        ArgumentNullException.ThrowIfNull(knobs);

        _byKey = new Dictionary<string, IKnob>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IKnob>();
        foreach (var knob in knobs)
        {
            ArgumentNullException.ThrowIfNull(knob);
            if (string.IsNullOrWhiteSpace(knob.Key))
                throw new InvalidOperationException(
                    $"Knob {knob.GetType().FullName} declared an empty Key.");
            if (knob.AllowedValues.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    $"Knob '{knob.Key}' declared an empty AllowedValues entry.");
            if (!_byKey.TryAdd(knob.Key, knob))
                throw new InvalidOperationException(
                    $"Duplicate knob key '{knob.Key}'. Each registered knob must declare a unique Key.");

            var defaultParse = knob.ParseValue(knob.DefaultValue);
            if (!defaultParse.Ok)
                throw new InvalidOperationException(
                    $"Knob '{knob.Key}' default '{knob.DefaultValue}' is invalid: {defaultParse.Error}");

            ordered.Add(knob);
        }

        ordered.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
        _ordered = ordered;
    }

    public IReadOnlyList<IKnob> All => _ordered;

    public bool TryGet(string key, out IKnob knob)
    {
        if (string.IsNullOrEmpty(key))
        {
            knob = null!;
            return false;
        }
        return _byKey.TryGetValue(key, out knob!);
    }

    public KnobValidationResult Validate(string key, string value)
    {
        var result = Normalize(key, value);
        return result.Ok
            ? KnobValidationResult.Success
            : KnobValidationResult.Fail(result.Error!);
    }

    public KnobNormalizationResult Normalize(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return KnobNormalizationResult.Fail("knob key must not be empty");
        if (value is null)
            return KnobNormalizationResult.Fail($"knob '{key}' value must not be null");

        var trimmedKey = key.Trim();
        if (!_byKey.TryGetValue(trimmedKey, out var knob))
            return KnobNormalizationResult.Fail(
                $"unknown knob '{key}'. Known knobs: {KnownKeysDescription()}");

        var parsed = knob.ParseValue(value);
        if (!parsed.Ok)
            return KnobNormalizationResult.Fail(parsed.Error!);

        return KnobNormalizationResult.Success(knob.Key, parsed.CanonicalValue!, parsed.TypedValue!);
    }

    public KnobValidationResult ValidateAll(IReadOnlyDictionary<string, string>? proposed)
    {
        if (proposed is null || proposed.Count == 0)
            return KnobValidationResult.Success;

        foreach (var (key, value) in proposed)
        {
            var result = Validate(key, value);
            if (!result.Ok) return result;
        }
        return KnobValidationResult.Success;
    }

    public bool TryGetTypedValue<T>(
        IReadOnlyDictionary<string, string> resolved,
        string key,
        out T value)
    {
        value = default!;
        if (resolved is null || !_byKey.TryGetValue(key, out var knob))
            return false;

        if (!TryGetCanonical(resolved, knob, out _, out var typed))
            return false;

        if (typed is T matched)
        {
            value = matched;
            return true;
        }

        return false;
    }

    public IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs)
    {
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var knob in _ordered)
        {
            if (itemKnobs is not null && TryGetCanonical(itemKnobs, knob, out var itemValue, out _))
                effective[knob.Key] = itemValue!;
            else if (projectKnobs is not null && TryGetCanonical(projectKnobs, knob, out var projectValue, out _))
                effective[knob.Key] = projectValue!;
            else
                effective[knob.Key] = knob.DefaultValue;
        }
        return effective;
    }

    private static bool TryGetCanonical(
        IReadOnlyDictionary<string, string> source,
        IKnob knob,
        out string? canonical,
        out object? typed)
    {
        if (source.TryGetValue(knob.Key, out var raw) && !string.IsNullOrWhiteSpace(raw))
            return TryParse(knob, raw, out canonical, out typed);

        foreach (var kv in source)
        {
            if (string.Equals(kv.Key, knob.Key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                return TryParse(knob, kv.Value, out canonical, out typed);
            }
        }

        canonical = null;
        typed = null;
        return false;
    }

    private static bool TryParse(IKnob knob, string value, out string? canonical, out object? typed)
    {
        var parsed = knob.ParseValue(value);
        if (parsed.Ok)
        {
            canonical = parsed.CanonicalValue!;
            typed = parsed.TypedValue;
            return true;
        }

        // Persisted value is no longer valid — signal "not set" so Resolve
        // falls through to the next precedence tier (project default → knob
        // default). The API/config paths validate at set-time so reaching here
        // means the descriptor changed in code since the value was persisted.
        canonical = null;
        typed = null;
        return false;
    }

    private string KnownKeysDescription()
    {
        if (_ordered.Count == 0) return "(none registered)";
        return string.Join(", ", _ordered.Select(k => k.Key));
    }
}
