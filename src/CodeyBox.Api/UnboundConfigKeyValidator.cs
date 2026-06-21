using System.Collections;
using System.Reflection;
using CodeyBox.Projects;
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
/// Maps a configuration sub-path to the typed POCO that <c>ConfigurationBinder</c>
/// uses for that sub-tree when the typed root is bound separately from
/// <see cref="CodeyBoxOptions"/> / <see cref="ProjectsOptions"/>.
/// Surfaces typos inside e.g. <c>CodeyBox:Plugins</c> without giving up the
/// genuinely operator-keyed plugin-id extension namespace.
/// </summary>
/// <param name="RootType">The typed POCO bound at the configuration sub-path.</param>
/// <param name="AllowsExtensionKeys">
/// When <c>true</c>, child keys at this POCO level that do not match a property
/// are treated as opaque operator-keyed extensions (the subtree is skipped, no
/// report). Use for sections that mix typed properties with operator-defined
/// extension keys at the same level — currently only <c>CodeyBox:Plugins</c>,
/// which holds <c>AssemblyPaths</c>/<c>PackageDirectories</c>/<c>Allowlist</c>
/// alongside operator-defined <c>&lt;plugin-id&gt;</c> subtrees.
/// </param>
public sealed record ExternalSectionBinding(Type RootType, bool AllowsExtensionKeys);

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
/// DateTime, DateTimeOffset, DateOnly, TimeOnly, decimal, Guid, Uri,
/// Version, object) — any child is an unbound key.</description></item>
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
    /// of full configuration paths (e.g. <c>"CodeyBox:DangerouslyDisableAuth"</c>)
    /// whose subtrees are skipped entirely — use for leaf-shaped operator keys
    /// read directly via <c>IConfiguration</c> with no matching property on the
    /// typed root graph. <paramref name="externalBindings"/> maps a sub-path to
    /// a typed POCO bound separately from the root types — the inspector
    /// recurses into the sub-path with that POCO instead of flagging it
    /// unbound, so typos like <c>CodeyBox:BuildScriptAudit:TimoutSeconds</c>
    /// still surface even though <c>BuildScriptAuditorOptions</c> is not part
    /// of the typed root union.
    /// </summary>
    public static IReadOnlyList<UnboundConfigKeyReport> Inspect(
        IConfiguration section,
        IReadOnlyCollection<Type> rootTypes,
        IReadOnlyCollection<string>? exemptPaths = null,
        IReadOnlyDictionary<string, ExternalSectionBinding>? externalBindings = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(rootTypes);
        if (rootTypes.Count == 0)
            throw new ArgumentException("At least one root type is required.", nameof(rootTypes));

        var exempt = new HashSet<string>(
            exemptPaths ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var bindings = externalBindings is null
            ? new Dictionary<string, ExternalSectionBinding>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ExternalSectionBinding>(externalBindings, StringComparer.OrdinalIgnoreCase);
        var reports = new List<UnboundConfigKeyReport>();
        WalkPoco(section, rootTypes, reports, exempt, bindings, allowsExtensionKeys: false);
        return reports;
    }

    private static void WalkPoco(
        IConfiguration node,
        IReadOnlyCollection<Type> types,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt,
        Dictionary<string, ExternalSectionBinding> externalBindings,
        bool allowsExtensionKeys)
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
                if (TryWalkCustomBoundSection(child, prop, reports, exempt, externalBindings))
                    continue;
                Walk(child, prop.PropertyType, reports, exempt, externalBindings);
                continue;
            }

            // No typed-property match. Check whether the configuration path is
            // bound separately to a typed POCO (e.g. CodeyBox:Plugins ->
            // PluginOptions) — if so, recurse into that POCO's property graph
            // so typos inside the section still surface.
            if (externalBindings.TryGetValue(child.Path, out var binding))
            {
                WalkPoco(
                    child,
                    new[] { binding.RootType },
                    reports,
                    exempt,
                    externalBindings,
                    allowsExtensionKeys: binding.AllowsExtensionKeys);
                continue;
            }

            // Operator-keyed extension namespace (e.g. plugin-id under
            // CodeyBox:Plugins) — opaque subtree, no report.
            if (allowsExtensionKeys)
                continue;

            reports.Add(new UnboundConfigKeyReport
            {
                Path = child.Path,
                NearestProperty = NearestPropertyName(child.Key, properties.Keys),
            });
        }
    }

    /// <summary>
    /// Sections whose typed property does not match the shape
    /// <see cref="ProjectsOptionsBinder.ApplyCustomMaps"/> actually accepts at
    /// runtime. The property is declared as <c>List&lt;string&gt;?</c> but the
    /// binder also reads a string-keyed map under it; the naive walker would
    /// flag every documented operator key as unbound. Dispatch to a custom
    /// walker that mirrors the binder's detection rules.
    /// </summary>
    private static bool TryWalkCustomBoundSection(
        IConfigurationSection child,
        PropertyInfo prop,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt,
        Dictionary<string, ExternalSectionBinding> externalBindings)
    {
        if (prop.DeclaringType != typeof(ProjectAuditConfig))
            return false;

        if (string.Equals(prop.Name, nameof(ProjectAuditConfig.Languages), StringComparison.Ordinal))
        {
            WalkLanguagesSection(child, reports, exempt, externalBindings);
            return true;
        }

        if (string.Equals(prop.Name, nameof(ProjectAuditConfig.AuditTypes), StringComparison.Ordinal))
        {
            WalkAuditTypesSection(child, reports, exempt, externalBindings);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks <c>Audit:Languages</c>, which the binder accepts in two shapes
    /// simultaneously: numeric-indexed string entries (the typed
    /// <see cref="List{T}"/> form) plus an <c>Overrides</c> sub-section that
    /// <see cref="ProjectsOptionsBinder.ApplyLanguageMap"/> reads as
    /// <c>Dictionary&lt;string, <see cref="ProjectLanguagePresetOverrideConfig"/>&gt;</c>.
    /// </summary>
    private static void WalkLanguagesSection(
        IConfigurationSection node,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt,
        Dictionary<string, ExternalSectionBinding> externalBindings)
    {
        foreach (var child in node.GetChildren())
        {
            if (exempt.Contains(child.Path))
                continue;

            if (int.TryParse(child.Key, out _))
            {
                // List form — element is a string leaf; any sub-key is junk.
                Walk(child, typeof(string), reports, exempt, externalBindings);
                continue;
            }

            if (string.Equals(child.Key, "Overrides", StringComparison.OrdinalIgnoreCase))
            {
                // Map form — operator-keyed dict of language id to override
                // POCO. Keys are arbitrary; recurse with the override type so
                // typos inside the override (e.g. "Replce") still flag.
                foreach (var langChild in child.GetChildren())
                {
                    if (exempt.Contains(langChild.Path))
                        continue;
                    Walk(langChild, typeof(ProjectLanguagePresetOverrideConfig), reports, exempt, externalBindings);
                }
                continue;
            }

            // Any other sub-key under Languages is junk (neither a list index
            // nor the documented Overrides map).
            reports.Add(new UnboundConfigKeyReport { Path = child.Path });
        }
    }

    /// <summary>
    /// Walks <c>Audit:AuditTypes</c>, which the binder reads as either a
    /// numeric-indexed list of audit-type ids or, via
    /// <see cref="ProjectsOptionsBinder.ApplyAuditTypeMap"/>, a string-keyed
    /// map of audit-type id to <see cref="ProjectAuditTypeOverrideConfig"/>.
    /// </summary>
    private static void WalkAuditTypesSection(
        IConfigurationSection node,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt,
        Dictionary<string, ExternalSectionBinding> externalBindings)
    {
        var children = node.GetChildren().ToList();
        if (children.Count == 0)
            return;

        // Detection mirrors ApplyAuditTypeMap: all-numeric → list, any
        // non-numeric → map. Walk each surface accordingly.
        var allNumeric = children.All(c => int.TryParse(c.Key, out _));
        var elementType = allNumeric ? typeof(string) : typeof(ProjectAuditTypeOverrideConfig);
        foreach (var child in children)
        {
            if (exempt.Contains(child.Path))
                continue;
            Walk(child, elementType, reports, exempt, externalBindings);
        }
    }

    private static void Walk(
        IConfigurationSection node,
        Type type,
        List<UnboundConfigKeyReport> reports,
        HashSet<string> exempt,
        Dictionary<string, ExternalSectionBinding> externalBindings)
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
                Walk(child, valueType, reports, exempt, externalBindings);
            }
            return;
        }

        if (TryGetEnumerableElementType(effective, out var elementType))
        {
            foreach (var child in node.GetChildren())
            {
                if (exempt.Contains(child.Path))
                    continue;
                Walk(child, elementType, reports, exempt, externalBindings);
            }
            return;
        }

        // POCO — recurse with property map.
        WalkPoco(node, new[] { effective }, reports, exempt, externalBindings, allowsExtensionKeys: false);
    }

    private static IEnumerable<KeyValuePair<string, PropertyInfo>> BuildPropertyMap(Type type)
    {
        // Mirror ConfigurationBinder's bindability rule. The binder writes
        // scalar/POCO values via a public/internal setter, and mutates the
        // existing instance in-place for IDictionary&lt;,&gt; / IList&lt;&gt; /
        // ICollection&lt;&gt; properties — even when those have no setter at
        // all. A get-only computed property of a non-collection type
        // (e.g. <c>public TimeSpan Budget => …</c>) is NOT bindable; if
        // registered here it would mask the very silent-no-op this feature
        // exists to catch.
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var prop in props)
        {
            // Indexer properties are not bindable by name.
            if (prop.GetIndexParameters().Length > 0)
                continue;

            if (!IsBindableProperty(prop))
                continue;

            var keyAlias = prop
                .GetCustomAttribute<ConfigurationKeyNameAttribute>()
                ?.Name;
            yield return new KeyValuePair<string, PropertyInfo>(
                string.IsNullOrEmpty(keyAlias) ? prop.Name : keyAlias,
                prop);
        }
    }

    private static bool IsBindableProperty(PropertyInfo prop)
    {
        var setter = prop.SetMethod;
        if (setter is not null && (setter.IsPublic || setter.IsAssembly))
            return true;

        // No accessible setter: the binder still binds when the existing
        // instance is mutable in-place. That covers IDictionary&lt;,&gt; /
        // IReadOnlyDictionary&lt;,&gt; (the binder calls Add) and
        // IList&lt;&gt; / ICollection&lt;&gt; / arrays-as-IEnumerable&lt;&gt;
        // (the binder Adds elements). Scalar/POCO get-only properties fall
        // through and are dropped.
        var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        if (IsLeaf(underlying))
            return false;
        if (TryGetDictionaryValueType(underlying, out _))
            return true;
        if (TryGetEnumerableElementType(underlying, out _))
            return true;
        return false;
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
        // Cap at 64 chars per side to keep the DP table tiny even on bizarre
        // operator keys.
        var trimmedKey = key.Length > 64 ? key[..64] : key;
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var trimmedCandidate = candidate.Length > 64 ? candidate[..64] : candidate;
            // Scale the cutoff with key length: a 4-char property like Path
            // would otherwise hint on any unrelated 4–7-char operator key
            // (distance 3 ≈ one shared char). Min length wins so a short
            // typo of a long property is still capped tightly.
            var keyLen = Math.Min(trimmedKey.Length, trimmedCandidate.Length);
            var cutoff = Math.Min(3, Math.Max(1, keyLen / 2));
            var distance = LevenshteinDistance(trimmedKey, trimmedCandidate);
            if (distance < bestDistance && distance <= cutoff)
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
