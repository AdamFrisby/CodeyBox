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
            if (!_byKey.TryAdd(knob.Key, knob))
                throw new InvalidOperationException(
                    $"Duplicate knob key '{knob.Key}'. Each registered knob must declare a unique Key.");

            if (knob.AllowedValues.Count > 0 &&
                !knob.AllowedValues.Any(v => string.Equals(v, knob.DefaultValue, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Knob '{knob.Key}' default '{knob.DefaultValue}' is not in its AllowedValues " +
                    $"[{string.Join(", ", knob.AllowedValues)}].");
            }

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
        if (string.IsNullOrWhiteSpace(key))
            return KnobValidationResult.Fail("knob key must not be empty");
        if (value is null)
            return KnobValidationResult.Fail($"knob '{key}' value must not be null");

        if (!_byKey.TryGetValue(key, out var knob))
            return KnobValidationResult.Fail(
                $"unknown knob '{key}'. Known knobs: {KnownKeysDescription()}");

        if (knob.AllowedValues.Count > 0 &&
            !knob.AllowedValues.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
        {
            return KnobValidationResult.Fail(
                $"knob '{knob.Key}' value '{value}' is not allowed. Allowed values: " +
                $"{string.Join(", ", knob.AllowedValues)}");
        }

        return KnobValidationResult.Success;
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

    public IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string>? itemKnobs,
        IReadOnlyDictionary<string, string>? projectKnobs)
    {
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var knob in _ordered)
        {
            if (itemKnobs is not null && TryGetCanonical(itemKnobs, knob, out var itemValue))
                effective[knob.Key] = itemValue!;
            else if (projectKnobs is not null && TryGetCanonical(projectKnobs, knob, out var projectValue))
                effective[knob.Key] = projectValue!;
            else
                effective[knob.Key] = knob.DefaultValue;
        }
        return effective;
    }

    private static bool TryGetCanonical(
        IReadOnlyDictionary<string, string> source,
        IKnob knob,
        out string? canonical)
    {
        if (source.TryGetValue(knob.Key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            canonical = NormaliseValueAgainstAllowedValues(knob, raw);
            return true;
        }

        foreach (var kv in source)
        {
            if (string.Equals(kv.Key, knob.Key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                canonical = NormaliseValueAgainstAllowedValues(knob, kv.Value);
                return true;
            }
        }

        canonical = null;
        return false;
    }

    private static string NormaliseValueAgainstAllowedValues(IKnob knob, string value)
    {
        if (knob.AllowedValues.Count == 0)
            return value;

        foreach (var allowed in knob.AllowedValues)
        {
            if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        // Persisted value is not in AllowedValues — treat as not set so the
        // pipeline falls back to project default / knob default. The API path
        // validates at set-time so reaching here means the knob's
        // AllowedValues changed in code since the value was persisted.
        return knob.DefaultValue;
    }

    private string KnownKeysDescription()
    {
        if (_ordered.Count == 0) return "(none registered)";
        return string.Join(", ", _ordered.Select(k => k.Key));
    }
}
