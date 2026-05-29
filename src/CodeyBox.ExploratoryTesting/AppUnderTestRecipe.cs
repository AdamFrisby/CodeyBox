using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Base type for the per-target launch recipe. Subtype per modality
/// (<see cref="WebAppRecipe"/> for web today; CLI / native / 3D / API land
/// later) so the harness implementation can pattern-match without leaking
/// modality-specific fields onto the base.
/// </summary>
public abstract record AppUnderTestRecipe
{
    /// <summary>
    /// Short, stable identifier for the target ("jobtrack", "acme-portal").
    /// Used in logs, in derived in-VM paths (log file names, pid files), and
    /// for telemetry; it is NOT a sandbox name or user-facing display string.
    /// Lowercase ASCII letters, digits and dashes only.
    /// </summary>
    public required string TargetName { get; init; }
}

/// <summary>
/// Recipe for a web-app target driven through an in-VM browser. The harness
/// provisions a graphical sandbox, executes <see cref="BuildSteps"/> and
/// <see cref="SeedSteps"/> in order, starts <see cref="RunCommand"/> as a
/// backgrounded daemon, opens the in-VM browser at <see cref="EntryUrl"/>,
/// and polls until the app responds AND a screenshot confirms the UI has
/// rendered.
///
/// <para><b>Determinism:</b> every step is just argv passed to
/// <see cref="ISandbox.ExecAsync"/> — no shell interpolation by the harness
/// — and the harness does not invoke an LLM. Same recipe + same source +
/// same seed → same final database / file state, modulo the target's own
/// non-determinism (which is the recipe author's problem to control).</para>
/// </summary>
public sealed record WebAppRecipe : AppUnderTestRecipe
{
    /// <summary>
    /// Sandbox image reference passed through to
    /// <see cref="SandboxSpec.ImageReference"/>. Default is empty, which
    /// lets the underlying provider pick its standard graphical baseline.
    /// </summary>
    public string ImageReference { get; init; } = string.Empty;

    /// <summary>
    /// Host bind mounts the harness adds to the sandbox spec — typically the
    /// target's source tree so <see cref="BuildSteps"/> can build it. Empty
    /// means the target is fetched from inside the VM during
    /// <see cref="BuildSteps"/> (e.g. <c>git clone</c>).
    /// </summary>
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];

    /// <summary>Environment passed through to <see cref="SandboxSpec.Environment"/>.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Resource limits override. Null falls back to
    /// <see cref="SandboxResourceLimits.Default"/>. Build-heavy targets
    /// (full Blazor + EF + xUnit) usually want the default 12 GiB.
    /// </summary>
    public SandboxResourceLimits? Limits { get; init; }

    /// <summary>
    /// Logical network profile name. Defaults to
    /// <see cref="SandboxConventions.GraphicalNetworkProfile"/>, which is the
    /// only profile guaranteed to come pre-wired for graphical sandboxes.
    /// Override for targets that need a wider allowlist (e.g. an external
    /// auth provider during seed).
    /// </summary>
    public string NetworkProfile { get; init; } = SandboxConventions.GraphicalNetworkProfile;

    /// <summary>
    /// Run-and-wait steps that produce build artifacts (<c>dotnet build</c>,
    /// <c>npm ci</c>, ...). Run in declaration order; any non-zero exit
    /// fails the launch.
    /// </summary>
    public IReadOnlyList<RecipeStep> BuildSteps { get; init; } = [];

    /// <summary>
    /// Run-and-wait steps that produce a deterministic data state (DB
    /// migrations, fixture loads). Always run AFTER <see cref="BuildSteps"/>
    /// and BEFORE <see cref="RunCommand"/>. Any non-zero exit fails the
    /// launch.
    /// </summary>
    public IReadOnlyList<RecipeStep> SeedSteps { get; init; } = [];

    /// <summary>
    /// The long-running command that starts the target itself (the ASP.NET
    /// host serving the Blazor UI). The harness backgrounds this — the
    /// command's stdout / stderr go to a log file under <c>/var/log/codeybox</c>
    /// inside the VM and the harness does not wait for it to exit.
    /// </summary>
    public required RecipeStep RunCommand { get; init; }

    /// <summary>
    /// In-VM URL the harness probes for HTTP readiness and opens the
    /// browser at. Typically <c>http://localhost:5000</c> for an ASP.NET
    /// default Kestrel binding.
    /// </summary>
    public required string EntryUrl { get; init; }

    /// <summary>
    /// argv used to start the in-VM browser pointed at the entry URL. The
    /// harness substitutes the literal token <c>$URL</c> (no shell expansion)
    /// in any element with <see cref="EntryUrl"/>. The recipe is responsible
    /// for ensuring this browser is installed — typically via a
    /// <see cref="BuildSteps"/> entry that runs <c>apt-get install -y
    /// firefox-esr</c> or similar.
    /// </summary>
    public required IReadOnlyList<string> BrowserCommand { get; init; }

    /// <summary>
    /// Maximum wall-clock the harness will spend polling for the app to
    /// respond at <see cref="EntryUrl"/> (HTTP), then polling for the UI to
    /// render (screenshot). Default 2 minutes covers a cold JIT for an
    /// ASP.NET host plus a Blazor first-render.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Poll interval used by both readiness phases.</summary>
    public TimeSpan ReadinessPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Settle delay between the browser launch and the first screenshot
    /// readiness poll. Browsers take ~1s to map their window after exec;
    /// polling immediately is wasted IO.
    /// </summary>
    public TimeSpan BrowserSettleDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A single step in a recipe — one <see cref="ISandbox.ExecAsync"/> call.
/// </summary>
public sealed record RecipeStep
{
    /// <summary>
    /// argv (no shell). The harness invokes this verbatim via
    /// <see cref="SandboxExec.Argv"/>. Empty / null is invalid.
    /// </summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>
    /// Working directory inside the VM. Null defaults to
    /// <see cref="SandboxConventions.WorkDir"/>.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment for this step only. Merged with the recipe-level
    /// <see cref="WebAppRecipe.Environment"/> at exec time.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Human-readable label used in logs. Defaults to the first element of
    /// <see cref="Command"/>.
    /// </summary>
    public string? Label { get; init; }
}
