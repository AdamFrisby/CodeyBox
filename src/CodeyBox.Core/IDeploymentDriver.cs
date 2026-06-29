namespace CodeyBox.Core;

/// <summary>
/// Drives the lifecycle of a verification deployment for a project. The
/// orchestrator selects the driver whose <see cref="Kind"/> matches the
/// configured <see cref="DeploymentRecipe.Kind"/>.
///
/// <para>A deployment is an EPHEMERAL stand-up of the project's software as
/// deployed software — built artifact running on its target substrate — so
/// deployment-targeted auditors (tool smoke/health, LLM/CUA exploration,
/// optional human reviewer) can verify that "Done = merged" actually behaves
/// when stood up. Deployments are not production releases; teardown is part
/// of the normal lifecycle, not an error path.</para>
///
/// <para>Each driver implements the same lifecycle:</para>
/// <list type="number">
///   <item><b>Provision</b> — stand up the runtime substrate (sandbox VM,
///   network, mounts).</item>
///   <item><b>Deploy</b> — build the artifact and start it inside the
///   substrate.</item>
///   <item><b>HealthCheck</b> — wait for readiness (HTTP probe / process
///   liveness / invocation success, driver-appropriate).</item>
///   <item><b>Expose</b> — return connection info as a structured
///   <see cref="DeploymentEndpoint"/>.</item>
///   <item><b>Teardown</b> — always-callable, idempotent disposal via
///   <see cref="IDeploymentHandle.DisposeAsync"/>.</item>
/// </list>
///
/// <para>A driver MUST run teardown internally if any pre-expose step fails,
/// then surface the underlying error to the caller. The
/// <see cref="IDeploymentHandle"/> returned on success is the only durable
/// way for the caller to release resources — losing the handle without
/// disposing it leaks the substrate (the leak reaper sweeps these as a
/// safety net; see <c>DeploymentLeakReaper</c>).</para>
/// </summary>
public interface IDeploymentDriver
{
    /// <summary>
    /// Stable identifier matching the recipe's <see cref="DeploymentRecipe.Kind"/>.
    /// Conventional values are listed on <see cref="DeploymentKinds"/>; driver
    /// authors may register additional values.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Validate the recipe shape this driver understands. Invoked at recipe
    /// load (project config bind + every <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
    /// reload, when a deployment-driver registry is composed into the project
    /// repository) so misconfigurations surface at parse time, and again from
    /// <see cref="IDeploymentManager.StartAsync"/> as a defence in depth for
    /// hand-built recipes that bypass the binder. Throws an exception
    /// describing the offending field on failure; returns silently on success.
    /// </summary>
    void ValidateRecipe(DeploymentRecipe recipe);

    /// <summary>
    /// Runs the full Provision → Deploy → HealthCheck → Expose lifecycle. On
    /// success the returned handle is "Ready" and exposes a
    /// <see cref="DeploymentEndpoint"/>; the caller disposes it to tear the
    /// deployment down. On failure the driver tears down whatever it had
    /// provisioned before throwing.
    /// </summary>
    Task<IDeploymentHandle> DeployAsync(
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Conventional <see cref="IDeploymentDriver.Kind"/> identifiers shipped with
/// the framework. Additional driver kinds may be registered by plug-ins.
/// </summary>
public static class DeploymentKinds
{
    /// <summary>Web application (app + backing services), exposes an HTTP(S) URL.</summary>
    public const string WebApp = "web-app";

    /// <summary>Long-running process/daemon, exposes whatever endpoint the recipe declares.</summary>
    public const string Daemon = "daemon";

    /// <summary>CLI tool, exposes the installed binary path.</summary>
    public const string Cli = "cli";

    /// <summary>
    /// Library, packed and restored into a minimal consumer harness;
    /// "deployment" = the harness compiles/runs.
    /// </summary>
    public const string Library = "library";
}

/// <summary>
/// Strongly-typed shape of a deployment recipe loaded from project config.
/// Drivers consume the fields relevant to their <see cref="IDeploymentDriver.Kind"/>;
/// fields a driver does not understand are ignored. <see cref="Settings"/>
/// is a free-form bag for driver-specific options that would not otherwise fit.
/// </summary>
public sealed record DeploymentRecipe
{
    /// <summary>
    /// Matches the <see cref="IDeploymentDriver.Kind"/> of the driver that
    /// should handle this recipe.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Substrate image reference (e.g. a sandbox baseline image). Passed
    /// through to the provisioned sandbox so the driver does not need to
    /// know how the operator names baselines.
    /// </summary>
    public required string ImageReference { get; init; }

    /// <summary>
    /// Shell command run inside the provisioned substrate to build the
    /// project's artifact. Empty when the project's artifact is already
    /// produced (e.g. CI pre-built) and only needs staging.
    /// </summary>
    public string BuildCommand { get; init; } = string.Empty;

    /// <summary>
    /// Shell command run inside the substrate to start the deployed
    /// artifact (web app server, daemon main loop, …). Required for
    /// kinds that need a long-running process; ignored for library/CLI.
    /// </summary>
    public string? RunCommand { get; init; }

    /// <summary>
    /// In-substrate path to the built artifact. Used by drivers whose
    /// expose step needs to surface a file location (CLI binary path,
    /// library nupkg path).
    /// </summary>
    public string? ArtifactPath { get; init; }

    /// <summary>
    /// Environment variables forwarded into the substrate. Combined with
    /// any environment the driver needs to inject itself; the recipe's
    /// values take precedence.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Supporting services that must be deployed alongside the primary
    /// artifact (databases, message brokers, mocks, …). Each service runs
    /// in the same substrate as the primary deployment by default; drivers
    /// that need cross-VM topologies override this.
    /// </summary>
    public IReadOnlyList<DeploymentService> Services { get; init; } = [];

    /// <summary>
    /// In-substrate ports the deployment listens on. The first entry is
    /// the "primary" port the expose step builds the endpoint URL from
    /// when no explicit one is configured.
    /// </summary>
    public IReadOnlyList<int> Ports { get; init; } = [];

    /// <summary>
    /// HTTP(S) path the readiness probe hits when the kind is web. Required
    /// for the <c>web-app</c> kind (the only HTTP driver shipped today);
    /// other drivers treat null as "no HTTP probe" and fall back to a
    /// driver-appropriate readiness check (process liveness, port check,
    /// invocation success).
    /// </summary>
    public string? HealthEndpoint { get; init; }

    /// <summary>
    /// Maximum time the driver waits for readiness before treating the
    /// deployment as failed and tearing it down. Defaults to 5 minutes —
    /// long enough for a JIT-compiling app to come up cold, short enough
    /// to surface stuck deployments quickly.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Operator-declared hint for how long an exposed deployment is expected
    /// to live before the orchestrator should consider its lifetime
    /// unreasonable. Defaults to 60 minutes — long enough for a thorough
    /// audit, short enough that a forgotten deployment doesn't squat on
    /// the host overnight. Not yet enforced by an auto-teardown timer; the
    /// pipeline integration in link 2 is expected to consume this hint to
    /// schedule the dispose / extend the leak-reaper grace per-recipe.
    /// Today the leak reaper only consults its own
    /// <c>DeploymentLeakOptions.LeakAgeThreshold</c>.
    /// </summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Name of the host-side sandbox network profile this deployment must
    /// attach to (see <see cref="SandboxNetworkPolicy.ProfileName"/>). The
    /// driver passes it through verbatim; the sandbox provider resolves
    /// the name to a host bridge.
    /// </summary>
    public string? NetworkProfile { get; init; }

    /// <summary>
    /// Free-form driver-specific settings. Drivers MUST treat unknown keys
    /// as no-ops so a recipe authored for a newer driver version stays
    /// loadable. Keys are case-sensitive; convention is kebab-case.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A supporting service deployed alongside the primary artifact (database,
/// message broker, fixture, …). Drivers may render services as separate
/// sandboxes or as colocated processes inside the primary substrate.
/// </summary>
public sealed record DeploymentService
{
    public required string Name { get; init; }
    public required string ImageReference { get; init; }
    public string? RunCommand { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<int> Ports { get; init; } = [];
    public string? HealthEndpoint { get; init; }
}

/// <summary>
/// Inputs the orchestrator hands the driver at deploy time. The driver uses
/// <see cref="SandboxProvider"/> to provision its substrate; everything else
/// is recipe + context metadata.
/// </summary>
public sealed record DeploymentContext
{
    /// <summary>Working directory inside the substrate. Defaults to the conventional /work mount.</summary>
    public string WorkingDirectory { get; init; } = "/work";

    /// <summary>Sandbox provider used to provision substrate VMs.</summary>
    public required ISandboxProvider SandboxProvider { get; init; }

    /// <summary>Optional host-path bind mounts the driver should request when provisioning.</summary>
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];

    /// <summary>
    /// Project owning this deployment. Used by tracking / leak detection
    /// and surfaced on the active-deployment snapshot; the driver itself
    /// does not depend on it.
    /// </summary>
    public ProjectId? ProjectId { get; init; }
}

/// <summary>
/// Live handle to a successfully-deployed verification deployment. Disposing
/// the handle tears the deployment down; teardown is idempotent — repeated
/// disposal is a no-op so the orchestrator can call it from both the
/// normal-path finally block and the leak reaper without coordinating.
/// </summary>
public interface IDeploymentHandle : IAsyncDisposable
{
    /// <summary>Stable identifier for diagnostics; unique per deployment instance.</summary>
    string Id { get; }

    /// <summary>The kind of deployment this handle wraps (mirrors the driver Kind).</summary>
    string Kind { get; }

    /// <summary>
    /// Structured connection info the caller (auditor, smoke tester, future
    /// human reviewer link) uses to reach the deployed software.
    /// </summary>
    DeploymentEndpoint Endpoint { get; }

    /// <summary>
    /// True until <see cref="IAsyncDisposable.DisposeAsync"/> has fully torn
    /// the deployment down. Tracking lets the leak reaper and graceful
    /// shutdown paths skip handles that already completed disposal.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Optional in-VM name of the substrate sandbox this handle owns. The
    /// leak reaper reads this to skip currently-active deployments when
    /// scanning provider-managed sandboxes for orphans. Null when the
    /// deployment does not provision a sandbox.
    /// </summary>
    string? SandboxId { get; }

    /// <summary>
    /// Re-runs the driver's readiness check against the live deployment.
    /// Throws when the deployment is no longer healthy; returns on success.
    /// Distinct from the initial probe inside <see cref="IDeploymentDriver.DeployAsync"/>
    /// so callers (auditors, periodic monitors) can re-verify mid-life.
    /// </summary>
    Task HealthCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes a command inside the live deployment substrate. This is the
    /// invocation channel for sandbox-scoped endpoints such as CLI binaries or
    /// packaged library artifacts whose <see cref="DeploymentEndpoint.Path"/> is
    /// meaningful inside the deployment sandbox rather than on the host file
    /// system. Implementations that do not own an executable substrate should
    /// throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default);
}

/// <summary>
/// Structured connection info for an exposed deployment. The shape is the
/// same across drivers; <see cref="Kind"/> tells the caller which fields are
/// load-bearing.
/// </summary>
public sealed record DeploymentEndpoint
{
    public required DeploymentEndpointKind Kind { get; init; }

    /// <summary>HTTP(S) URL for web deployments. Null for other kinds.</summary>
    public string? Url { get; init; }

    /// <summary>Host/IP for TCP-style endpoints.</summary>
    public string? Host { get; init; }

    /// <summary>Port for TCP-style endpoints.</summary>
    public int? Port { get; init; }

    /// <summary>
    /// File path for kinds that expose an artifact instead of a network
    /// endpoint (CLI binary path, library nupkg path).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Free-form metadata the driver wants to surface (PID for a daemon,
    /// connection string for a service, package id for a library, …).
    /// Callers MUST treat unknown keys as opaque.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum DeploymentEndpointKind
{
    Http,
    Tcp,
    Process,
    Cli,
    Library,
}

/// <summary>
/// Resolves an <see cref="IDeploymentDriver"/> for a given recipe Kind. Loose
/// coupling: new drivers are added via DI without changing the orchestrator.
/// </summary>
public interface IDeploymentDriverRegistry
{
    bool TryGet(string kind, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IDeploymentDriver driver);
    IReadOnlyCollection<string> AvailableKinds { get; }
}

/// <summary>
/// Snapshot of one deployment currently held by the orchestrator. Returned
/// by <see cref="IDeploymentManager.GetActive"/> for /deployments API
/// responses and for the leak reaper's active-set check.
/// </summary>
public sealed record ActiveDeploymentInfo(
    string Id,
    string Kind,
    ProjectId? ProjectId,
    string? SandboxId,
    DateTimeOffset StartedAt,
    DeploymentEndpoint Endpoint);

/// <summary>
/// Orchestrator-side facade over the driver registry. Looks up the driver
/// for the recipe's Kind, runs deployment, tracks the live handle, and
/// reaps it on dispose. The pipeline layer never talks to drivers directly
/// — it asks the manager to stand a deployment up and gets back a handle.
/// </summary>
public interface IDeploymentManager
{
    /// <summary>
    /// Stand up a deployment from a recipe. Throws when no driver is
    /// registered for the recipe's Kind. The returned handle is tracked
    /// in the manager's active set until disposed.
    /// </summary>
    Task<IDeploymentHandle> StartAsync(
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Snapshot of currently-active deployments. Used by the leak reaper
    /// and operator-facing /deployments endpoints (link 2).
    /// </summary>
    IReadOnlyList<ActiveDeploymentInfo> GetActive();
}
