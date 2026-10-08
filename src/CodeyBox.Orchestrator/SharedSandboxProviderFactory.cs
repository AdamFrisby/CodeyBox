using CodeyBox.HostProcess;
using CodeyBox.Sandbox.ArtifactProvenance;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Process;
using CodeyBox.Sandbox.Sprites;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-kind option inputs for <see cref="SharedSandboxProviderFactory"/>.
/// Each provider's options arrive as an accessor so hot-reloadable callers
/// (the orchestrator's <c>IOptionsMonitor</c> delegates, the executor's
/// <c>Func{ExecutorOptions}</c> delegate) keep working: providers that hold
/// the accessor observe edits, while snapshot-constructed providers
/// (bubblewrap) keep the options read at build time — exactly as before.
/// </summary>
public sealed class SandboxProviderBuildArgs
{
    /// <summary>Logger factory used for every constructed provider.</summary>
    public required ILoggerFactory Loggers { get; init; }

    /// <summary>Bubblewrap options snapshot read once at build time.</summary>
    public Func<BubblewrapSandboxOptions> BubblewrapOptions { get; init; } = () => new();

    /// <summary>Multipass options accessor held by the provider.</summary>
    public Func<MultipassSandboxOptions> MultipassOptions { get; init; } = () => new();

    /// <summary>Incus options accessor held by the provider.</summary>
    public Func<IncusSandboxOptions> IncusOptions { get; init; } = () => new();

    /// <summary>Multipass-remote options accessor held by the provider.</summary>
    public Func<MultipassRemoteSandboxOptions> MultipassRemoteOptions { get; init; } = () => new();

    /// <summary>Sprites options accessor held by the provider.</summary>
    public Func<SpritesSandboxOptions> SpritesOptions { get; init; } = () => new();

    /// <summary>
    /// Host process runner for the SSH transport behind
    /// <c>multipass-remote</c>. Null falls back to the default runner.
    /// </summary>
    public IProcessRunner? ProcessRunner { get; init; }

    /// <summary>Optional timing store shared by VM-backed providers.</summary>
    public CodeyBox.Core.ITimingStore? Timings { get; init; }

    /// <summary>Optional resource-usage store shared by VM-backed providers.</summary>
    public CodeyBox.Core.ISandboxResourceUsageStore? ResourceUsage { get; init; }

    /// <summary>
    /// Operator-owned artifact-trust accessor (hot-reloadable). Held by
    /// executable-staging providers; enforcement activates only when the
    /// policy opts in. Defaults to a disabled policy.
    /// </summary>
    public Func<ArtifactTrustOptions> ArtifactTrust { get; init; } = () => new();

    /// <summary>
    /// Shared admission service for executable provenance. Required when the
    /// trust policy enables enforcement; providers fail closed without it.
    /// Lifetime must cover every built provider (host singleton).
    /// </summary>
    public ArtifactAdmissionService? ArtifactAdmission { get; init; }

    /// <summary>
    /// Shared baseline-bake failure tracker (hot path for the
    /// <c>baseline_provisioning_blocked</c> alert and <c>/queue/status</c>).
    /// Null creates a provider-private tracker (executor hosts, unit tests).
    /// </summary>
    public CodeyBox.Core.BaselineProvisioningBlockedTracker? BaselineBlockedTracker { get; init; }

    /// <summary>
    /// Builds args whose option accessors read live executor configuration:
    /// every call observes the current <see cref="ExecutorOptions"/> value,
    /// so an operator edit lands on the next provider operation without an
    /// executor restart (subject to each provider's own snapshot semantics).
    /// </summary>
    public static SandboxProviderBuildArgs FromExecutorOptions(
        Func<ExecutorOptions> options,
        ILoggerFactory loggers,
        IProcessRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);
        return new SandboxProviderBuildArgs
        {
            Loggers = loggers,
            BubblewrapOptions = () => options().Bubblewrap,
            MultipassOptions = () => options().Multipass,
            IncusOptions = () => options().Incus,
            MultipassRemoteOptions = () => options().MultipassRemote,
            SpritesOptions = () => options().Sprites,
            ProcessRunner = runner,
        };
    }
}

/// <summary>
/// Single composition site for sandbox provider construction. Both the
/// orchestrator and the executor host resolve every provider kind through
/// here, so adding a provider means editing this switch — and only this
/// switch — instead of one bespoke branch per entry point. Kind-agnostic
/// policy (workload trust, admission control, plugin contributions) stays
/// with the callers; this factory only maps a normalised kind to its
/// provider using the supplied option accessors.
/// </summary>
public static class SharedSandboxProviderFactory
{
    /// <summary>
    /// Builds the provider for <paramref name="kind"/> (trimmed,
    /// case-insensitive). Unknown or blank kinds fail closed with a message
    /// naming the offending value and every buildable kind.
    /// </summary>
    public static CodeyBox.Core.ISandboxProvider Build(string? kind, SandboxProviderBuildArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(kind))
            throw new InvalidOperationException(
                $"Cannot build a sandbox provider for a blank kind. Valid: {ValidKinds}.");
        var normalized = kind.Trim().ToLowerInvariant();
        var loggers = args.Loggers;
        if (normalized == CodeyBox.Core.HostPlatformSupport.Process)
            return new ProcessSandboxProvider(loggers.CreateLogger<ProcessSandboxProvider>());
        if (normalized == CodeyBox.Core.HostPlatformSupport.Bubblewrap)
            return new BubblewrapSandboxProvider(
                RequireOptions(args.BubblewrapOptions, normalized),
                loggers.CreateLogger<BubblewrapSandboxProvider>(),
                args.Timings);
        if (normalized == CodeyBox.Core.HostPlatformSupport.Multipass)
            return new MultipassSandboxProvider(
                RequireAccessor(args.MultipassOptions, normalized),
                loggers.CreateLogger<MultipassSandboxProvider>(),
                args.Timings,
                args.ResourceUsage,
                trustAccessor: RequireAccessor(args.ArtifactTrust, normalized),
                admission: args.ArtifactAdmission);
        if (normalized == CodeyBox.Core.HostPlatformSupport.Incus)
            return new IncusSandboxProvider(
                RequireAccessor(args.IncusOptions, normalized),
                loggers.CreateLogger<IncusSandboxProvider>(),
                args.Timings,
                args.ResourceUsage,
                trustAccessor: RequireAccessor(args.ArtifactTrust, normalized),
                admission: args.ArtifactAdmission,
                blockedTracker: args.BaselineBlockedTracker);
        if (normalized == CodeyBox.Core.HostPlatformSupport.MultipassRemote)
            return BuildMultipassRemote(args, loggers);
        if (normalized == CodeyBox.Core.HostPlatformSupport.Sprites)
            return new SpritesSandboxProvider(
                RequireAccessor(args.SpritesOptions, normalized),
                loggers.CreateLogger<SpritesSandboxProvider>());
        throw UnknownKind(kind);
    }

    /// <summary>
    /// Fails closed for an unbuildable kind. Public so entry points can
    /// validate configuration against the same message without building.
    /// </summary>
    public static InvalidOperationException UnknownKind(string? kind) => new(
        $"Unknown sandbox provider kind '{(kind ?? "").Trim()}'. Valid: {ValidKinds}.");

    private static string ValidKinds => string.Join(
        ", ",
        CodeyBox.Core.HostPlatformSupport.AllProviderIds.OrderBy(static s => s, StringComparer.Ordinal));

    private static MultipassRemoteSandboxProvider BuildMultipassRemote(
        SandboxProviderBuildArgs args,
        ILoggerFactory loggers)
    {
        var accessor = RequireAccessor(args.MultipassRemoteOptions, CodeyBox.Core.HostPlatformSupport.MultipassRemote);
        var runner = args.ProcessRunner ?? new DefaultProcessRunner();
        var transportLog = loggers.CreateLogger<OpenSshCliTransport>();
        return new MultipassRemoteSandboxProvider(
            accessor,
            hostOptions => new OpenSshCliTransport(() => hostOptions, runner, transportLog),
            loggers.CreateLogger<MultipassRemoteSandboxProvider>());
    }

    private static Func<T> RequireAccessor<T>(Func<T>? accessor, string kind) where T : class =>
        accessor ?? throw new InvalidOperationException(
            $"Sandbox provider kind '{kind}' has no options accessor configured.");

    private static T RequireOptions<T>(Func<T>? accessor, string kind) where T : class =>
        accessor?.Invoke() ?? throw new InvalidOperationException(
            $"Sandbox provider kind '{kind}' resolved to null options.");
}
