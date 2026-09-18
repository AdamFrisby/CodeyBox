namespace CodeyBox.Api;

/// <summary>
/// Tracks whether the binaries in <c>bin/</c> were built from the currently
/// checked-out revision. Refreshed at startup (warning while the service is
/// still healthy), on every configuration reload, and on demand from the
/// consistency surface — so a <c>git pull</c> under a <c>--no-build</c>
/// deploy is reported long before the next restart turns it into a fatal
/// unbound-key failure.
///
/// <para>Never prevents startup: every probe is capped and exception-safe,
/// and an unknown revision on either side reports
/// <see cref="DeployConsistencyStatus.Unknown"/>, not diverged. This service
/// reports the condition; it never rebuilds or otherwise heals the deploy —
/// deploy steps stay explicit.</para>
/// </summary>
public sealed class DeployConsistencyService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<DeployConsistencyService> _log;
    private readonly Func<string?> _builtRevisionProvider;
    private readonly Func<string?>? _checkoutRevisionProvider;
    private readonly Lock _gate = new();
    private DeployConsistencyReport _current = DeployConsistency.Evaluate(null, null);

    public DeployConsistencyService(
        IConfiguration config,
        ILogger<DeployConsistencyService> log)
        : this(config, log, () => BuildRevision.GetBuiltRevision(), checkoutRevisionProvider: null)
    {
    }

    internal DeployConsistencyService(
        IConfiguration config,
        ILogger<DeployConsistencyService> log,
        Func<string?> builtRevisionProvider,
        Func<string?>? checkoutRevisionProvider)
    {
        _config = config;
        _log = log;
        _builtRevisionProvider = builtRevisionProvider;
        _checkoutRevisionProvider = checkoutRevisionProvider;
    }

    public DeployConsistencyReport Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = Refresh();
            if (report.IsDiverged)
            {
                _log.LogWarning(
                    "Deploy inconsistency detected: the working tree ({CheckoutRevision}) has moved ahead of " +
                    "the built output ({BuiltRevision}). The service started because the current configuration " +
                    "still binds, but configuration added after the build was compiled will fail startup " +
                    "validation on the next restart. Rebuild the service (dotnet build) and restart so bin/ " +
                    "matches the checkout.",
                    report.CheckoutRevision,
                    report.BuiltRevision);
            }
        }
        catch (Exception ex)
        {
            // The consistency check must never itself prevent startup.
            _log.LogError(ex, "Deploy consistency check failed; continuing startup without a consistency report.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Re-reads both revisions and publishes the fresh report. Safe to call
    /// from request paths and the reload coordinator; never throws.
    /// </summary>
    public DeployConsistencyReport Refresh()
    {
        try
        {
            var built = SafeRead(_builtRevisionProvider);
            var checkout = _checkoutRevisionProvider is not null
                ? SafeRead(_checkoutRevisionProvider)
                : CheckoutRevisionReader.ReadCheckoutRevision(ConfiguredRepoRoot());

            var report = DeployConsistency.Evaluate(built, checkout);
            lock (_gate)
            {
                _current = report;
            }

            return report;
        }
        catch (Exception)
        {
            return Current;
        }
    }

    private string? ConfiguredRepoRoot()
    {
        // Read live from IConfiguration (rather than a bound snapshot) so a
        // reload that changes the override takes effect on the next refresh
        // without a restart — the same reason UnboundConfigKeyHostedValidator
        // reads its knobs directly. The key binds to
        // DeployConsistencyOptions.RepoRoot so strict validation accepts it.
        try
        {
            var root = _config["CodeyBox:DeployConsistency:RepoRoot"];
            if (string.IsNullOrWhiteSpace(root))
                return null;
            var full = Path.GetFullPath(root.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeRead(Func<string?> provider)
    {
        try
        {
            return provider();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
