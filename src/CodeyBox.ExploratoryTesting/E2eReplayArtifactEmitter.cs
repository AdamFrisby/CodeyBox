using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Turns a computer-use <see cref="SessionTrace"/> plus explicit replay
/// assertions into a deterministic <see cref="E2eReplayArtifact"/> suitable
/// for <see cref="AutomationKind.E2eReplay"/> test cases.
/// </summary>
public static class E2eReplayArtifactEmitter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static E2eReplayArtifact EmitFromTrace(
        SessionTrace trace,
        IReadOnlyList<E2eReplayAssertion> assertions,
        E2eReplayEmitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(assertions);

        options ??= new E2eReplayEmitOptions();
        var steps = new List<E2eReplayStep>();
        string? focusedSelector = null;

        if (!string.IsNullOrWhiteSpace(trace.EntryUrl))
        {
            steps.Add(new E2eReplayStep
            {
                Action = "navigate",
                Target = trace.EntryUrl,
                DelayAfterMs = options.StepDelayAfterMs,
            });
        }

        foreach (var entry in trace.Entries.OrderBy(static e => e.Sequence))
        {
            var action = entry.Action.Kind;
            var selector = E2eSelectorResolver.Resolve(
                entry.Action.TargetDescriptor.Accessibility,
                entry.Action.TargetDescriptor.AccessibilitySnapshotJson
                ?? entry.Observation.AccessibilitySnapshotJson);

            switch (action)
            {
                case "click":
                    if (string.IsNullOrWhiteSpace(selector))
                        throw new InvalidOperationException($"Could not resolve selector for click at sequence {entry.Sequence}.");
                    steps.Add(new E2eReplayStep
                    {
                        Action = "click",
                        Selector = selector,
                        DelayAfterMs = options.StepDelayAfterMs,
                    });
                    focusedSelector = IsTextInputSelector(entry.Action.TargetDescriptor.Accessibility)
                        ? selector
                        : null;
                    break;

                case "type":
                    var typedValue = entry.Action.InputEvents.FirstOrDefault(e => e.Type == SandboxInputEventType.Type)?.Text;
                    if (string.IsNullOrWhiteSpace(focusedSelector))
                    {
                        focusedSelector = selector;
                    }
                    if (string.IsNullOrWhiteSpace(focusedSelector))
                        throw new InvalidOperationException($"Could not resolve selector for type at sequence {entry.Sequence}.");
                    steps.Add(new E2eReplayStep
                    {
                        Action = "fill",
                        Selector = focusedSelector,
                        Value = RedactSensitiveValue(focusedSelector, entry.Action.TargetDescriptor.Accessibility, typedValue),
                        DelayAfterMs = options.StepDelayAfterMs,
                    });
                    break;

                case "key":
                    var key = entry.Action.InputEvents.FirstOrDefault(e => e.Type == SandboxInputEventType.Key)?.Key;
                    if (string.IsNullOrWhiteSpace(focusedSelector))
                        throw new InvalidOperationException($"Could not resolve selector for key at sequence {entry.Sequence}.");
                    steps.Add(new E2eReplayStep
                    {
                        Action = "press",
                        Selector = focusedSelector,
                        Value = key ?? string.Empty,
                        DelayAfterMs = options.StepDelayAfterMs,
                    });
                    break;

                case "double_click":
                    if (string.IsNullOrWhiteSpace(selector))
                        throw new InvalidOperationException($"Could not resolve selector for double_click at sequence {entry.Sequence}.");
                    steps.Add(new E2eReplayStep
                    {
                        Action = "doubleClick",
                        Selector = selector,
                        DelayAfterMs = options.StepDelayAfterMs,
                    });
                    focusedSelector = null;
                    break;

                case "screenshot":
                case "move":
                case "scroll":
                    break;

                default:
                    throw new NotSupportedException($"Action '{action}' is not supported for e2e-replay emission.");
            }
        }

        var artifact = new E2eReplayArtifact
        {
            Name = options.Name ?? trace.TargetName,
            Readiness = options.Readiness ?? BuildDefaultReadiness(trace.EntryUrl),
            Steps = steps,
            Assertions = assertions,
        };

        if (!E2eReplayArtifactValidation.TryValidate(artifact, out var failureKind, out var detail))
            throw new InvalidOperationException($"Emitted artifact failed validation ({failureKind}): {detail}");

        return artifact;
    }

    private static bool IsTextInputSelector(TraceAccessibilityDescriptor? descriptor)
    {
        if (descriptor is null)
            return false;

        if (string.Equals(descriptor.Role, "textbox", StringComparison.OrdinalIgnoreCase))
            return true;

        var elementType = descriptor.ElementType ?? string.Empty;
        return elementType.Contains("password", StringComparison.OrdinalIgnoreCase)
            || elementType.Contains("#email", StringComparison.Ordinal)
            || elementType.Contains("#password", StringComparison.Ordinal);
    }

    private static string RedactSensitiveValue(string? selector, TraceAccessibilityDescriptor? descriptor, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        if (selector?.Contains("password", StringComparison.OrdinalIgnoreCase) == true)
            return Core.E2eReplaySensitiveValueRedaction.PasswordPlaceholder;

        if (descriptor is null)
            return value;

        var elementType = descriptor.ElementType ?? string.Empty;
        if (elementType.Contains("password", StringComparison.OrdinalIgnoreCase))
            return Core.E2eReplaySensitiveValueRedaction.PasswordPlaceholder;

        return value;
    }

    public static string EmitJson(
        SessionTrace trace,
        IReadOnlyList<E2eReplayAssertion> assertions,
        E2eReplayEmitOptions? options = null)
        => JsonSerializer.Serialize(EmitFromTrace(trace, assertions, options), JsonOptions);

    public static TestCase BuildTestCase(
        string id,
        string name,
        string description,
        string sourceWorkItemId,
        SessionTrace trace,
        IReadOnlyList<E2eReplayAssertion> assertions,
        E2eReplayEmitOptions? options = null,
        string? conformanceJson = null,
        string? label = null,
        DateTimeOffset? now = null)
    {
        var artifact = EmitFromTrace(trace, assertions, options);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new TestCase
        {
            Id = id,
            Name = name,
            Description = description,
            SourceWorkItemId = sourceWorkItemId,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            AutomationKind = AutomationKind.E2eReplay,
            ExecutableArtifactJson = JsonSerializer.Serialize(artifact, JsonOptions),
            ConformanceJson = conformanceJson,
            Label = label,
        };
    }

    private const int DefaultReadinessMaxAttempts = 30;
    private const int DefaultReadinessDelayMs = 250;

    private static E2eReadinessProbe? BuildDefaultReadiness(string? entryUrl)
    {
        if (string.IsNullOrWhiteSpace(entryUrl))
            return null;

        if (!Uri.TryCreate(entryUrl, UriKind.Absolute, out var uri))
            return null;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        return new E2eReadinessProbe
        {
            Url = $"{origin}/healthz",
            MaxAttempts = DefaultReadinessMaxAttempts,
            DelayMs = DefaultReadinessDelayMs,
        };
    }
}

public sealed record E2eReplayEmitOptions
{
    public string? Name { get; init; }
    public E2eReadinessProbe? Readiness { get; init; }
    public int? StepDelayAfterMs { get; init; }
}
