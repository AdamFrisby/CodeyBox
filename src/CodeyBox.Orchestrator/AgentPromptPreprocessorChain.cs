using System.Reflection;
using CodeyBox.Core;
using CodeyBox.PluginSdk;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Applies all registered prompt preprocessors in the CodeyBox chain order:
/// built-in first, plugin processors, built-in last. Plugins are identified by
/// their <see cref="CodeyBoxPluginAttribute"/> and ordered within the plugin
/// segment by <see cref="IAgentPromptPreprocessor.Order"/>.
/// </summary>
public sealed class AgentPromptPreprocessorChain
{
    public static AgentPromptPreprocessorChain Empty { get; } = new([]);

    private readonly IReadOnlyList<IAgentPromptPreprocessor> _preprocessors;

    public AgentPromptPreprocessorChain(IEnumerable<IAgentPromptPreprocessor> preprocessors)
    {
        _preprocessors = preprocessors
            .Select((preprocessor, index) => new OrderedPreprocessor(preprocessor, index))
            .OrderBy(static x => x.Segment)
            .ThenBy(static x => x.EffectiveOrder)
            .ThenBy(static x => x.Index)
            .Select(static x => x.Preprocessor)
            .ToList();
    }

    public bool HasPreprocessors => _preprocessors.Count > 0;

    internal IReadOnlyList<IAgentPromptPreprocessor> OrderedPreprocessors => _preprocessors;

    public async Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        foreach (var preprocessor in _preprocessors)
        {
            ct.ThrowIfCancellationRequested();
            var next = await preprocessor.ProcessAsync(ctx, prompt, ct).ConfigureAwait(false);
            prompt = next ?? throw new InvalidOperationException(
                $"Prompt preprocessor {preprocessor.GetType().FullName} returned null.");
        }

        return prompt;
    }

    private sealed record OrderedPreprocessor(
        IAgentPromptPreprocessor Preprocessor,
        int Index)
    {
        public int Segment { get; } = ResolveSegment(Preprocessor);
        public int EffectiveOrder { get; } = ResolveEffectiveOrder(Preprocessor);
    }

    private static int ResolveSegment(IAgentPromptPreprocessor preprocessor)
    {
        if (IsPlugin(preprocessor))
            return 1;

        return preprocessor.Order >= AgentPromptPreprocessorOrder.BuiltInLast ? 2 : 0;
    }

    private static int ResolveEffectiveOrder(IAgentPromptPreprocessor preprocessor)
    {
        if (IsPlugin(preprocessor))
            return AgentPromptPreprocessorOrder.Plugin + preprocessor.Order;

        return preprocessor.Order;
    }

    private static bool IsPlugin(IAgentPromptPreprocessor preprocessor) =>
        preprocessor.GetType().GetCustomAttribute<CodeyBoxPluginAttribute>() is not null;
}
