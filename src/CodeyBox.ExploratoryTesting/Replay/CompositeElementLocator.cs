using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Tries multiple <see cref="IElementLocator"/>s in order and returns the first
/// non-null hit. The default <see cref="ReplayEngine"/> wiring registers
/// <see cref="AccessibilityElementLocator"/> first (cheap accessibility-tree
/// probe), then <see cref="VisualSignatureElementLocator"/> as the
/// non-accessibility fallback for canvas / 3D / untagged targets — the brief's
/// 'accessibility tree when present, ELSE visual / OCR / template' contract.
///
/// <para>Custom locators (richer template match, vision-LLM, …) can be wired
/// in front of or behind the defaults by passing a <c>CompositeElementLocator</c>
/// to the engine. The first locator whose <c>LocateAsync</c> returns non-null
/// wins; the rest are skipped.</para>
/// </summary>
public sealed class CompositeElementLocator : IElementLocator
{
    private readonly IReadOnlyList<IElementLocator> _locators;

    public CompositeElementLocator(params IElementLocator[] locators)
        : this((IReadOnlyList<IElementLocator>)locators)
    {
    }

    public CompositeElementLocator(IReadOnlyList<IElementLocator> locators)
    {
        ArgumentNullException.ThrowIfNull(locators);
        if (locators.Count == 0)
            throw new ArgumentException("Composite locator requires at least one inner locator.", nameof(locators));
        for (var i = 0; i < locators.Count; i++)
        {
            if (locators[i] is null)
                throw new ArgumentException($"Inner locator {i} is null.", nameof(locators));
        }
        _locators = locators;
    }

    public async Task<LocatedTarget?> LocateAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        for (var i = 0; i < _locators.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var hit = await _locators[i].LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
            if (hit is not null)
                return hit;
        }
        return null;
    }
}
