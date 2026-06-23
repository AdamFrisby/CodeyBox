using System.Diagnostics.CodeAnalysis;
using CodeyBox.Core;

namespace CodeyBox.Deployment;

/// <summary>
/// Default <see cref="IDeploymentDriverRegistry"/> — wraps the DI-injected
/// set of <see cref="IDeploymentDriver"/> instances and dispatches lookups
/// by <see cref="IDeploymentDriver.Kind"/>. Duplicate kinds throw at
/// construction so misregistration surfaces at startup rather than first
/// dispatch.
/// </summary>
public sealed class DeploymentDriverRegistry : IDeploymentDriverRegistry
{
    private readonly Dictionary<string, IDeploymentDriver> _byKind;

    public DeploymentDriverRegistry(IEnumerable<IDeploymentDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        // OrdinalIgnoreCase mirrors the case-insensitive JSON config binder so
        // recipe authors who write "Kind": "Web-App" or "WebApp" still resolve
        // to the canonical driver Kind. The driver's own Kind string is the
        // dictionary key, so duplicate-Kind detection treats case-equivalent
        // strings as duplicates.
        _byKind = new Dictionary<string, IDeploymentDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            if (driver is null)
                continue;
            if (string.IsNullOrWhiteSpace(driver.Kind))
                throw new InvalidOperationException(
                    $"Deployment driver of type {driver.GetType().FullName} returned an empty Kind.");
            if (!_byKind.TryAdd(driver.Kind, driver))
                throw new InvalidOperationException(
                    $"Duplicate deployment driver Kind '{driver.Kind}'. Each Kind must map to exactly one IDeploymentDriver.");
        }
    }

    public bool TryGet(string kind, [MaybeNullWhen(false)] out IDeploymentDriver driver)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            driver = null;
            return false;
        }
        return _byKind.TryGetValue(kind, out driver);
    }

    public IReadOnlyCollection<string> AvailableKinds => _byKind.Keys.ToArray();
}
