using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// How the <c>csharp:test-pass</c> audit chooses which tests to run. Bound from
/// hot-reloadable configuration (<c>Audit:TestSelection:Mode</c>).
/// </summary>
public enum TestSelectionMode
{
    /// <summary>Run the entire suite — the default; byte-identical to the legacy path.</summary>
    All,
}

/// <summary>
/// Strongly-typed options for the test-selection seam, bound from the
/// <c>Audit:TestSelection</c> configuration section via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> so edits
/// hot-reload without a process restart.
/// </summary>
public sealed class TestSelectionOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Audit:TestSelection";

    /// <summary>
    /// Selection mode name (case-insensitive). Defaults to <c>all</c>, which keeps
    /// the emitted <c>dotnet test</c> command byte-identical to today. Parsed via
    /// <see cref="TestSelectionModeParser"/>; an unrecognised value is rejected at
    /// options-validation time.
    /// </summary>
    public string Mode { get; set; } = TestSelectionModeParser.DefaultModeName;
}

/// <summary>
/// Pure parser mapping the configured <see cref="TestSelectionOptions.Mode"/>
/// string onto <see cref="TestSelectionMode"/>. Kept separate from the options
/// POCO so both the DI mode accessor and the options validator share one source
/// of truth for the accepted values.
/// </summary>
public static class TestSelectionModeParser
{
    /// <summary>The default mode name used when configuration is absent.</summary>
    public const string DefaultModeName = "all";

    /// <summary>
    /// Attempts to parse <paramref name="value"/> (case-insensitive, trimmed) into
    /// a <see cref="TestSelectionMode"/>. Returns false for null, blank, or
    /// unrecognised values.
    /// </summary>
    public static bool TryParse(string? value, out TestSelectionMode mode)
    {
        mode = TestSelectionMode.All;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "all":
                mode = TestSelectionMode.All;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses <paramref name="value"/> or throws <see cref="FormatException"/> for
    /// an unrecognised mode. Used behind the options validator, so a bad config
    /// value fails fast at load rather than at audit time.
    /// </summary>
    public static TestSelectionMode Parse(string? value)
        => TryParse(value, out var mode)
            ? mode
            : throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Unknown {TestSelectionOptions.SectionName}:Mode value '{value}'. Valid modes: all."));
}

/// <summary>
/// The DI-registered <see cref="ITestSelector"/>. Reads the current
/// <see cref="TestSelectionMode"/> live on every call (via the injected accessor,
/// which the composition root backs with <c>IOptionsMonitor.CurrentValue</c>) and
/// dispatches to the selector registered for that mode. Adding a new mode is a
/// matter of registering another <see cref="ITestSelector"/> in the map — no
/// change to callers.
/// </summary>
public sealed class ConfiguredTestSelector : ITestSelector
{
    private readonly Func<TestSelectionMode> _modeAccessor;
    private readonly IReadOnlyDictionary<TestSelectionMode, ITestSelector> _selectorsByMode;

    public ConfiguredTestSelector(
        Func<TestSelectionMode> modeAccessor,
        IReadOnlyDictionary<TestSelectionMode, ITestSelector> selectorsByMode)
    {
        ArgumentNullException.ThrowIfNull(modeAccessor);
        ArgumentNullException.ThrowIfNull(selectorsByMode);
        if (selectorsByMode.Count == 0)
            throw new ArgumentException("at least one mode selector is required", nameof(selectorsByMode));

        _modeAccessor = modeAccessor;
        _selectorsByMode = selectorsByMode;
    }

    public TestSelectionDecision Select(TestSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = _modeAccessor();
        if (!_selectorsByMode.TryGetValue(mode, out var selector))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"No test selector is registered for mode '{mode}'."));
        }

        return selector.Select(request);
    }
}
