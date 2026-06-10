using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator.Knobs;

/// <summary>
/// Built-in preprocessor that gathers fragments from every registered
/// <see cref="IKnob"/> and appends them to the agent's work / rework prompt
/// as a single block.
///
/// <para>
/// Runs at <see cref="AgentPromptPreprocessorOrder.BuiltInLast"/> so the
/// fragments land beneath project rules and any plugin contributions — the
/// per-item directives sit closest to the agent prompt, which makes them
/// hardest to ignore.
/// </para>
///
/// <para>
/// Only fires for <see cref="AgentPromptPhase.Work"/> and
/// <see cref="AgentPromptPhase.Rework"/> today. Audit / merge / check-and-act
/// phases are intentionally untouched; knob seams for those phases can be
/// added by extending <see cref="IKnob"/> with optional methods.
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
        if (ctx.Phase != AgentPromptPhase.Work && ctx.Phase != AgentPromptPhase.Rework)
            return prompt;
        if (_registry.All.Count == 0)
            return prompt;

        IReadOnlyDictionary<string, string>? itemKnobs = null;
        try
        {
            var item = await _store.GetAsync(ctx.ItemId, ct).ConfigureAwait(false);
            itemKnobs = item?.Knobs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defensive: a transient store failure must not strand the prompt.
            // Fall through with no per-item knobs; project defaults still apply.
            _log.LogWarning(ex,
                "Knob preprocessor could not load work item {WorkItemId} from store; falling back to project defaults",
                ctx.ItemId);
        }

        var effective = _registry.Resolve(itemKnobs, ctx.Project.Knobs);
        var fragments = new List<(string Key, string Value, string Fragment)>();
        foreach (var knob in _registry.All)
        {
            if (!effective.TryGetValue(knob.Key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            var fragment = knob.GetWorkPromptFragment(value);
            if (string.IsNullOrWhiteSpace(fragment))
                continue;
            fragments.Add((knob.Key, value, fragment));
        }

        if (fragments.Count == 0)
            return prompt;

        var sb = new StringBuilder(prompt.Length + (fragments.Count * 256));
        sb.Append(prompt);
        if (!prompt.EndsWith('\n')) sb.Append('\n');
        sb.Append('\n');
        sb.Append("## Per-item directives (knobs)\n\n");
        foreach (var (key, value, fragment) in fragments)
        {
            sb.Append("- **");
            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
            sb.Append("**: ");
            sb.Append(fragment.TrimEnd());
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
