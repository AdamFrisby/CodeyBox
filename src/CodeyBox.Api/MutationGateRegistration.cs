using CodeyBox.Audit;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Composition root for the mutation-testing rigor gate
/// (<c>tests:mutation-rigor</c>). Extracted from <c>Program.cs</c> so both the
/// host and the composition tests exercise the same code path.
///
/// <para>Default composition is fully inert: the gate is disabled and the
/// runner is <see cref="NullMutationRunner"/>. Setting
/// <c>CodeyBox:Mutation:Stryker:Enabled=true</c> wires the real
/// <see cref="StrykerMutationRunner"/> (still gated behind
/// <c>CodeyBox:Mutation:Enabled</c> at audit time). The runner is resolved
/// per audit from the current options snapshot, so flipping the Stryker
/// switch takes effect without a restart for subsequently resolved audits;
/// the <see cref="IMutationRunner"/> default registration itself stays the
/// null runner so custom operator registrations keep working.</para>
/// </summary>
public static class MutationGateRegistration
{
    /// <summary>
    /// Registers the mutation-gate options, ratchet store, runners, and
    /// auditor. Safe to call once at host startup.
    /// </summary>
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // IOptionsMonitor (not IOptions snapshot) so hot-reloads of
        // CodeyBox:Mutation (threshold, budget, Stryker switch, etc.) take
        // effect without a process restart, consistent with the rest of the
        // host's options wiring.
        services.Configure<MutationTestingAuditorOptions>(
            configuration.GetSection("CodeyBox:Mutation"));
        services.TryAddSingleton<IMutationRunner, NullMutationRunner>();
        services.TryAddSingleton<IMutationRatchetStore, InMemoryMutationRatchetStore>();
        services.TryAddSingleton<StrykerMutationRunner>(sp =>
            new StrykerMutationRunner(
                () => sp.GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>().CurrentValue.Stryker,
                sp.GetRequiredService<ILogger<StrykerMutationRunner>>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAuditor>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<MutationTestingAuditorOptions>>();
            var ratchet = sp.GetRequiredService<IMutationRatchetStore>();
            // Loud startup warning when the gate is enabled but the in-memory
            // ratchet store is the registered implementation: it is
            // process-local and every restart wipes the baseline, so the
            // "no-regression" invariant only holds within a single uptime
            // window. Operators flipping Enabled=true on a long-lived host
            // should swap in a file- or SQLite-backed store.
            if (monitor.CurrentValue.Enabled && ratchet is InMemoryMutationRatchetStore)
            {
                sp.GetRequiredService<ILogger<MutationTestingAuditor>>().LogWarning(
                    "mutation-rigor gate is enabled but the registered IMutationRatchetStore is " +
                    "InMemoryMutationRatchetStore — the no-regression baseline will be reset on every " +
                    "process restart. Register a persistent IMutationRatchetStore (file/SQLite) before " +
                    "relying on the ratchet across restarts.");
            }
            if (monitor.CurrentValue.Enabled && monitor.CurrentValue.Stryker.Enabled)
            {
                var errors = monitor.CurrentValue.Stryker.Validate();
                if (errors.Count > 0)
                {
                    sp.GetRequiredService<ILogger<StrykerMutationRunner>>().LogWarning(
                        "Stryker mutation runner is enabled but its configuration is invalid: {Errors}. " +
                        "Mutation runs will fail closed until the configuration is fixed.",
                        string.Join(" ", errors));
                }
            }
            return new MutationTestingAuditor(
                () => monitor.CurrentValue,
                SelectRunner(sp, monitor.CurrentValue),
                ratchet);
        });
    }

    /// <summary>
    /// Selects the runner for the current options snapshot: the real Stryker
    /// engine only when the operator opted in via
    /// <c>CodeyBox:Mutation:Stryker:Enabled</c>, otherwise the inert null
    /// runner. Public so composition tests can assert both wirings without
    /// booting the host.
    /// </summary>
    public static IMutationRunner SelectRunner(
        IServiceProvider services, MutationTestingAuditorOptions current)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(current);
        if (current.Stryker.Enabled)
            return (IMutationRunner)services.GetRequiredService(typeof(StrykerMutationRunner));
        return (IMutationRunner)services.GetRequiredService(typeof(IMutationRunner));
    }
}
