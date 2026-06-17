using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Api;

/// <summary>
/// One configuration key that does not bind to any property on the typed
/// options graph. Surfaced by <see cref="UnboundConfigKeyInspector"/> so the
/// operator sees a silent no-op as a startup error instead of a runtime
/// surprise (e.g. <c>CodeyBox:AgentStreams:RootDirectory</c> binding to
/// nothing while the typed property is <c>Path</c>).
/// </summary>
public sealed class UnboundConfigKeyReport
{
    public required string Path { get; init; }

    /// <summary>
    /// Optional nearest-match suggestion under the same parent section. Cheap
    /// case-insensitive Levenshtein with cutoff 3. Null when no candidate is
    /// close enough.
    /// </summary>
    public string? NearestProperty { get; init; }

    public override string ToString() =>
        NearestProperty is null
            ? Path + " — no matching option"
            : $"{Path} — no matching option; did you mean {NearestProperty}?";
}

/// <summary>
/// Walks an operator-provided <see cref="IConfiguration"/> sub-tree and
/// reports every leaf/section key that does not bind to a property on the
/// strongly-typed options graph.
///
/// <para>Recursion rules:</para>
/// <list type="bullet">
/// <item><description>POCO type — each child key must match a public property
/// (case-insensitive, respecting
/// <see cref="ConfigurationKeyNameAttribute"/>).</description></item>
/// <item><description>Dictionary&lt;TKey,TValue&gt; — keys are operator-defined
/// and skipped; values are recursed with <c>TValue</c>.</description></item>
/// <item><description>List/array/enumerable — keys are indices and skipped;
/// values are recursed with the element type.</description></item>
/// <item><description>Leaf types (primitives, strings, enums, TimeSpan,
/// DateTimeOffset, Guid, Uri) — any child is an unbound key.</description></item>
/// </list>
///
/// <para>The inspector is config-only: it never instantiates the options
/// graph, so a startup failure stays loud even when an upstream factory
/// would have papered over the mismatch.</para>
/// </summary>
public static class UnboundConfigKeyInspector
{
    /// <summary>
    /// Inspects <paramref name="section"/> against the union of properties on
    /// <paramref name="rootTypes"/>. <paramref name="exemptPaths"/> is a set
    /// of full configuration paths (e.g. <c>"CodeyBox:Plugins"</c>) whose
    /// subtrees are skipped entirely — use for sections bound outside the
    /// supplied root types.
    /// </summary>
    public static IReadOnlyList<UnboundConfigKeyReport> Inspect(
        IConfiguration section,
        IReadOnlyCollection<Type> rootTypes,
        IReadOnlyCollection<string>? exemptPaths = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(rootTypes);
        if (rootTypes.Count == 0)
            throw new ArgumentException("At least one root type is required.", nameof(rootTypes));

        var exempt = new HashSet<string>(
            exemptPaths ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var reports = new List<UnboundConfigKeyReport>();
        WalkPoco(section, rootTypes, reports, exempt);
        return reports;
    }

    private static void WalkPoco(
        IConfiguration node,
        IReadOnlyCollection<Type> types,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt)
    {
        // Union the property maps from every supplied type so multiple options
        // classes bound at the same configuration root coexist (e.g.
        // CodeyBoxOptions + ProjectsOptions both binding under "CodeyBox").
        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            foreach (var (key, prop) in BuildPropertyMap(type))
            {
                // First-wins keeps the type ordering meaningful. A duplicate
                // property name between two root types resolves to the first
                // type's view; the union is still a valid superset.
                if (!properties.ContainsKey(key))
                    properties[key] = prop;
            }
        }

        foreach (var child in node.GetChildren())
        {
            if (exempt.Contains(child.Path))
                continue;

            if (properties.TryGetValue(child.Key, out var prop))
            {
                Walk(child, prop.PropertyType, reports, exempt);
            }
            else
            {
                reports.Add(new UnboundConfigKeyReport
                {
                    Path = child.Path,
                    NearestProperty = NearestPropertyName(child.Key, properties.Keys),
                });
            }
        }
    }

    private static void Walk(
        IConfigurationSection node,
        Type type,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt)
    {
        var effective = Nullable.GetUnderlyingType(type) ?? type;

        if (IsLeaf(effective))
        {
            // Leaf type: any child is junk. Report each.
            foreach (var child in node.GetChildren())
            {
                if (exempt.Contains(child.Path))
                    continue;
                reports.Add(new UnboundConfigKeyReport { Path = child.Path });
            }
            return;
        }

        if (TryGetDictionaryValueType(effective, out var valueType))
        {
            foreach (var child in node.GetChildren())
            {
                if (exempt.Contains(child.Path))
                    continue;
                Walk(child, valueType, reports, exempt);
            }
            return;
        }

        if (TryGetEnumerableElementType(effective, out var elementType))
        {
            foreach (var child in node.GetChildren())
            {
                if (exempt.Contains(child.Path))
                    continue;
                Walk(child, elementType, reports, exempt);
            }
            return;
        }

        // POCO — recurse with property map.
        WalkPoco(node, new[] { effective }, reports, exempt);
    }

    private static IEnumerable<KeyValuePair<string, PropertyInfo>> BuildPropertyMap(Type type)
    {
        // BindingFlags mirrors ConfigurationBinder's: only public instance
        // properties that have a public/internal setter are bound. We don't
        // need to filter on setter accessibility here because we only consume
        // the property name — the goal is "does any property accept this key?".
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var prop in props)
        {
            // Indexer properties are not bindable by name.
            if (prop.GetIndexParameters().Length > 0)
                continue;

            var keyAlias = prop
                .GetCustomAttribute<ConfigurationKeyNameAttribute>()
                ?.Name;
            yield return new KeyValuePair<string, PropertyInfo>(
                string.IsNullOrEmpty(keyAlias) ? prop.Name : keyAlias,
                prop);
        }
    }

    private static bool IsLeaf(Type type)
    {
        if (type.IsPrimitive) return true;
        if (type.IsEnum) return true;
        if (type == typeof(string)) return true;
        if (type == typeof(decimal)) return true;
        if (type == typeof(TimeSpan)) return true;
        if (type == typeof(DateTime)) return true;
        if (type == typeof(DateTimeOffset)) return true;
        if (type == typeof(DateOnly)) return true;
        if (type == typeof(TimeOnly)) return true;
        if (type == typeof(Guid)) return true;
        if (type == typeof(Uri)) return true;
        if (type == typeof(Version)) return true;
        if (type == typeof(object)) return true; // untyped — treat as leaf, can't introspect
        return false;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        // Walk the type and all implemented interfaces looking for
        // IDictionary<,> / IReadOnlyDictionary<,>. The first match wins.
        if (TryMatchDictionary(type, out valueType))
            return true;
        foreach (var iface in type.GetInterfaces())
        {
            if (TryMatchDictionary(iface, out valueType))
                return true;
        }
        valueType = typeof(object);
        return false;

        static bool TryMatchDictionary(Type candidate, out Type valueType)
        {
            if (candidate.IsGenericType)
            {
                var def = candidate.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    valueType = candidate.GetGenericArguments()[1];
                    return true;
                }
            }
            valueType = typeof(object);
            return false;
        }
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(object);
            return true;
        }

        if (type == typeof(string))
        {
            // Strings are IEnumerable<char>; do not recurse them as collections.
            elementType = typeof(object);
            return false;
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            // Non-generic enumerable — element type is unknowable, treat as leaf.
            elementType = typeof(object);
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static string? NearestPropertyName(string key, IEnumerable<string> candidates)
    {
        const int maxDistance = 3;
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            // Strict prefix/contains favourite stays a hint, but distance
            // wins overall. Cap at 64 chars per side to keep the dynamic
            // table tiny even on bizarre operator keys.
            var distance = LevenshteinDistance(
                key.Length > 64 ? key[..64] : key,
                candidate.Length > 64 ? candidate[..64] : candidate);
            if (distance < bestDistance && distance <= maxDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    internal static string FormatReports(IReadOnlyList<UnboundConfigKeyReport> reports)
    {
        // One report per line, deterministic order. Caller decides whether
        // this is the text of an InvalidOperationException or a log line.
        var sorted = reports
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join(
            Environment.NewLine,
            sorted.Select(r => "  " + r.ToString().Replace("\r", "").Replace("\n", " ")));
    }
}
