using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator.Knobs;

/// <summary>
/// Built-in preprocessor that gathers fragments from every registered
/// <see cref="IKnob"/> and appends them to the agent's work prompt as a
/// single block.
///
/// <para>
/// Runs at <see cref="AgentPromptPreprocessorOrder.BuiltInLast"/> so the
/// fragments land beneath project rules and any plugin contributions — the
/// per-item directives sit closest to the agent prompt, which makes them
/// hardest to ignore.
/// </para>
///
/// <para>
/// Only fires for <see cref="AgentPromptPhase.Work"/> today. Rework, audit,
/// merge, and check-and-act phases are intentionally untouched; knob seams for
/// those phases can be added by extending <see cref="IKnob"/> with optional
/// methods.
/// </para>
/// </summary>
public sealed class KnobWorkPromptPreprocessor : IAgentPromptPreprocessor
{
    private readonly IKnobRegistry _registry;
    private readonly IWorkItemStore _store;
    private readonly ILogger<KnobWorkPromptPreprocessor> _log;

    public KnobWorkPromptPreprocessor(
        IKnobRegistry registry,
        IWorkItemStore store)
        : this(registry, store, NullLogger<KnobWorkPromptPreprocessor>.Instance)
    {
    }

    public KnobWorkPromptPreprocessor(
        IKnobRegistry registry,
        IWorkItemStore store,
        ILogger<KnobWorkPromptPreprocessor> log)
    {
        _registry = registry;
        _store = store;
        _log = log;
    }

    public int Order => AgentPromptPreprocessorOrder.BuiltInLast;

    public async Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        if (ctx.Phase != AgentPromptPhase.Work)
            return prompt;
        if (_registry.All.Count == 0)
            return prompt;

        var item = await _store.GetAsync(ctx.ItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            _log.LogError(
                "Work item {WorkItemId} was not found while applying knob prompt directives",
                ctx.ItemId);
            throw new InvalidOperationException(
                $"Work item '{ctx.ItemId}' was not found while applying knob prompt directives.");
        }

        var effective = _registry.Resolve(item.Knobs, ctx.Project.Knobs);
        var fragments = new List<(string Key, string Value, bool DisplayValue, string Fragment)>();
        foreach (var knob in _registry.All)
        {
            if (!effective.TryGetValue(knob.Key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            var fragment = knob.GetWorkPromptFragment(value);
            if (string.IsNullOrWhiteSpace(fragment))
                continue;
            var displayValue = knob.AllowedValues.Count > 0;
            if (!displayValue && !knob.AllowsFreeFormPromptFragments)
            {
                throw new InvalidOperationException(
                    $"Prompt-contributing knob '{knob.Key}' must declare finite AllowedValues " +
                    "or explicitly opt in to safe free-form prompt fragments.");
            }
            fragments.Add((knob.Key, value, displayValue, fragment));
        }

        if (fragments.Count == 0)
            return prompt;

        var sb = new StringBuilder(prompt.Length + (fragments.Count * 256));
        sb.Append(prompt);
        if (!prompt.EndsWith('\n')) sb.Append('\n');
        sb.Append('\n');
        sb.Append("## Per-item directives (knobs)\n\n");
        foreach (var (key, value, displayValue, fragment) in fragments)
        {
            sb.Append("- **");
            sb.Append(key);
            if (displayValue)
            {
                sb.Append('=');
                sb.Append(value);
            }
            sb.Append("**: ");
            sb.Append(fragment.TrimEnd());
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
