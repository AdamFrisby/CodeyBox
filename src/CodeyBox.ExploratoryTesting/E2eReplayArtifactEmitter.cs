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
                entry.Observation.AccessibilitySnapshotJson);

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
                    focusedSelector = selector;
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
                        Value = typedValue ?? string.Empty,
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

                case "screenshot":
                case "move":
                case "scroll":
                case "double_click":
                    break;

                default:
                    throw new NotSupportedException($"Action '{action}' is not supported for e2e-replay emission.");
            }
        }

        return new E2eReplayArtifact
        {
            Name = options.Name ?? trace.TargetName,
            Readiness = options.Readiness ?? BuildDefaultReadiness(trace.EntryUrl),
            Steps = steps,
            Assertions = assertions,
        };
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

    private static E2eReadinessProbe? BuildDefaultReadiness(string? entryUrl)
    {
        if (string.IsNullOrWhiteSpace(entryUrl))
            return null;

        var baseUrl = entryUrl.TrimEnd('/');
        var slash = baseUrl.LastIndexOf('/');
        var origin = slash > 0 ? baseUrl[..slash] : baseUrl;
        return new E2eReadinessProbe
        {
            Url = $"{origin}/healthz",
            MaxAttempts = 30,
            DelayMs = 250,
        };
    }
}

public sealed record E2eReplayEmitOptions
{
    public string? Name { get; init; }
    public E2eReadinessProbe? Readiness { get; init; }
    public int? StepDelayAfterMs { get; init; }
}
