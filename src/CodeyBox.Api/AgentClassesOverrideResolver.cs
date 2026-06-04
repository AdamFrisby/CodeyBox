using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace CodeyBox.Api;

/// <summary>
/// Replaces the default positional-array merge for
/// <c>CodeyBox:AgentClasses</c> with full REPLACE-on-override semantics.
///
/// <para>
/// Background: standard .NET <see cref="IConfiguration"/> merges JSON arrays
/// element-wise by index. An operator extra-config layer that supplies
/// <c>Members = [a, b, c]</c> against a base of
/// <c>Members = [w, x, y, z]</c> produces a layered <c>[a, b, c, z]</c> —
/// the base's trailing element resurfaces because the override is shorter.
/// For an AgentClass that is a silent footgun: removing a member shifts
/// indices and silently re-enables a base agent that may be broken or
/// unauthorised (see the cursor → gemini regression dated 2026-06-04 in the
/// task description).
/// </para>
///
/// <para>
/// This resolver walks <see cref="IConfigurationRoot.Providers"/> in reverse
/// precedence order. When a higher-precedence provider supplies any key
/// under <c>CodeyBox:AgentClasses</c>, that provider's view is bound in
/// isolation and replaces <see cref="CodeyBoxOptions.AgentClasses"/>
/// wholesale — no positional blend with lower-precedence layers. Operators
/// can express "these are the classes" without reverse-engineering the base
/// array length.
/// </para>
///
/// <para>
/// Applied via <see cref="IPostConfigureOptions{TOptions}"/> so the same
/// resolution runs at startup AND on every hot-reload of the watched JSON
/// files, before <c>AgentClassesConfigBuilder.Build</c> sees the list.
/// </para>
/// </summary>
public static class AgentClassesOverrideResolver
{
    private const string SectionPath = "CodeyBox:AgentClasses";

    /// <summary>
    /// If a higher-precedence configuration provider supplies any key under
    /// <c>CodeyBox:AgentClasses</c>, replaces <paramref name="options"/>.AgentClasses
    /// with the list bound from just that provider's keys (no positional
    /// merge with lower-precedence layers).
    ///
    /// <para>
    /// No-op when <paramref name="configuration"/> is not an
    /// <see cref="IConfigurationRoot"/> (some test harnesses pass plain
    /// sections) or when no provider supplies AgentClasses keys at all.
    /// </para>
    /// </summary>
    public static void ApplyTo(CodeyBoxOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationRoot root) return;

        var winner = FindHighestPrecedenceProvider(root);
        if (winner is null) return;

        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        EnumerateInto(winner, SectionPath, snapshot);
        if (snapshot.Count == 0) return;

        var subRoot = new ConfigurationBuilder()
            .Add(new MemoryConfigurationSource { InitialData = snapshot })
            .Build();

        var replacement = new List<AgentClassOptions>();
        subRoot.GetSection(SectionPath).Bind(replacement);
        options.AgentClasses = replacement;
    }

    private static IConfigurationProvider? FindHighestPrecedenceProvider(IConfigurationRoot root)
    {
        // Providers are listed in registration (precedence-low → precedence-high)
        // order; iterate in reverse so the highest-precedence supplier wins.
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.GetChildKeys(Array.Empty<string>(), SectionPath).Any())
                return provider;
        }
        return null;
    }

    private static void EnumerateInto(
        IConfigurationProvider provider,
        string path,
        Dictionary<string, string?> sink)
    {
        // GetChildKeys can yield duplicates; Distinct guards the recursion.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var childKey in provider.GetChildKeys(Array.Empty<string>(), path))
        {
            if (!seen.Add(childKey)) continue;
            var fullKey = $"{path}:{childKey}";
            if (provider.TryGet(fullKey, out var value))
                sink[fullKey] = value;
            EnumerateInto(provider, fullKey, sink);
        }
    }
}
