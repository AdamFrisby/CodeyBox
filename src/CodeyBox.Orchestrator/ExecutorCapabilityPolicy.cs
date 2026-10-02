using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Applies provider-declared capabilities to what an executor host may claim
/// at registration. Where a provider declares its own capabilities, those
/// are the truth: an operator must not be able to claim a well-known
/// capability (see <see cref="SandboxCapabilities.All"/>) that none of the
/// host's serving providers implements. Non-well-known tags are operator
/// clearance tags the providers have no opinion on and always pass through —
/// mirroring <see cref="SandboxProviderCapabilityGate"/> on the
/// orchestrator side, so both sides agree on what a host can do.
/// </summary>
public static class ExecutorCapabilityPolicy
{
    /// <summary>
    /// Projects operator-declared capabilities to the effective set: every
    /// non-well-known tag plus only those well-known tags at least one of
    /// <paramref name="providers"/> declares (ordinal, case-insensitive).
    /// </summary>
    public static IReadOnlyList<string> EffectiveCapabilities(
        IEnumerable<string> declared,
        IEnumerable<ISandboxProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(providers);
        var implemented = UnionDeclared(providers);
        var effective = new List<string>();
        foreach (var raw in declared)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var tag = raw.Trim();
            if (!IsWellKnown(tag) || implemented.Contains(tag))
                effective.Add(tag);
        }
        return effective;
    }

    /// <summary>
    /// Well-known capability tags the operator declared that none of
    /// <paramref name="providers"/> implements. Empty means the declaration
    /// is honest. Used for fail-fast startup validation.
    /// </summary>
    public static IReadOnlyList<string> UnsupportedWellKnownCapabilities(
        IEnumerable<string> declared,
        IEnumerable<ISandboxProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(providers);
        var implemented = UnionDeclared(providers);
        var unsupported = new List<string>();
        foreach (var raw in declared)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var tag = raw.Trim();
            if (IsWellKnown(tag) && !implemented.Contains(tag))
                unsupported.Add(tag);
        }
        return unsupported;
    }

    /// <summary>
    /// Union of well-known capabilities implemented by
    /// <paramref name="providers"/> (normalised, case-insensitive). Used in
    /// validation messages so the operator sees what is actually available.
    /// </summary>
    public static IReadOnlySet<string> ImplementedCapabilities(IEnumerable<ISandboxProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is null)
                continue;
            foreach (var capability in provider.DeclaredCapabilities ?? [])
            {
                if (string.IsNullOrWhiteSpace(capability))
                    continue;
                var tag = capability.Trim();
                if (IsWellKnown(tag))
                    union.Add(tag);
            }
        }
        return union;
    }

    private static HashSet<string> UnionDeclared(IEnumerable<ISandboxProvider> providers)
    {
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is null)
                continue;
            foreach (var capability in provider.DeclaredCapabilities ?? [])
            {
                if (!string.IsNullOrWhiteSpace(capability))
                    union.Add(capability.Trim());
            }
        }
        return union;
    }

    private static bool IsWellKnown(string tag)
    {
        foreach (var known in SandboxCapabilities.All)
        {
            if (string.Equals(known, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Fail-fast executor startup validation for sandbox providers. Resolves
/// every declared kind through <see cref="ISandboxProviderRegistry"/> —
/// warming each kind so a bad provider fails the host fast instead of the
/// first placement — and rejects operator capability claims no serving
/// provider implements. An unknown kind fails here with the registry's
/// known kinds named, never with a hardcoded literal list.
/// </summary>
public static class ExecutorSandboxStartup
{
    /// <summary>
    /// Validates executor options and warms every declared provider kind.
    /// Returns the serving providers in declaration order. Throws
    /// <see cref="InvalidOperationException"/> naming the offending value
    /// (and the valid ones) for unknown kinds, duplicate kinds, or
    /// unimplemented well-known capabilities.
    /// </summary>
    public static IReadOnlyList<ISandboxProvider> Validate(
        ExecutorOptions options,
        ISandboxProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        options.Validate();
        var kinds = options.GetDeclaredKinds();
        if (kinds.Count == 0)
            throw new InvalidOperationException(
                "CodeyBox:Executor declares no sandbox provider kind; set CodeyBox:Executor:LocalSandboxProvider " +
                $"or CodeyBox:Executor:SandboxProviders. Valid: {ExecutorOptions.ValidProviderKinds}.");
        var providers = new List<ISandboxProvider>(kinds.Count);
        foreach (var kind in kinds)
            providers.Add(ResolveKind(registry, kind));
        var unsupported = ExecutorCapabilityPolicy.UnsupportedWellKnownCapabilities(
            options.DeclaredCapabilities, providers);
        if (unsupported.Count > 0)
        {
            var implemented = ExecutorCapabilityPolicy.ImplementedCapabilities(providers);
            var available = implemented.Count > 0
                ? string.Join(", ", implemented.OrderBy(static s => s, StringComparer.Ordinal))
                : "none (this host's providers implement no well-known capabilities; use operator clearance tags only)";
            throw new InvalidOperationException(
                $"CodeyBox:Executor:DeclaredCapabilities claims well-known capabilities the serving providers do not implement: " +
                $"{string.Join(", ", unsupported)}. Implemented: {available}.");
        }
        return providers;
    }

    private static ISandboxProvider ResolveKind(ISandboxProviderRegistry registry, string kind)
    {
        try
        {
            return registry.EnsureKind(kind);
        }
        catch (InvalidOperationException)
        {
            // The composition's unknown-kind error already names the
            // offending value and the valid kinds; let it through unwrapped.
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Executor sandbox provider kind '{kind}' failed to build. " +
                $"Registered kinds: {FormatKnown(registry)}.", ex);
        }
    }

    private static string FormatKnown(ISandboxProviderRegistry registry)
    {
        try
        {
            return registry.KnownKinds.Count > 0
                ? string.Join(", ", registry.KnownKinds.OrderBy(static s => s, StringComparer.Ordinal))
                : "(none)";
        }
        catch (Exception)
        {
            return "(unknown)";
        }
    }
}
