using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Deployment;

/// <summary>
/// Drives a library deployment: builds the library, restores it into a
/// minimal consumer harness, and treats the harness compiling/running as
/// the "deployment is healthy" signal. The exposed endpoint surfaces the
/// produced package path (nupkg, wheel, jar, …) so downstream auditors can
/// inspect it.
///
/// <para>The harness invocation comes from the recipe's
/// <see cref="SettingsKeyHarnessCommand"/> setting. Without one the
/// readiness check is a no-op: a successful build IS the deployment, and
/// no consumer-side restore is exercised. Recipes that want to verify
/// downstream restore must declare a harness command.</para>
/// </summary>
public sealed class LibraryDeploymentDriver : SandboxDeploymentDriverBase
{
    public const string SettingsKeyHarnessCommand = "harness-command";

    public LibraryDeploymentDriver(ILogger<LibraryDeploymentDriver>? log = null, Func<DateTimeOffset>? clock = null)
        : base(log, clock) { }

    public override string Kind => DeploymentKinds.Library;

    public override void ValidateRecipe(DeploymentRecipe recipe)
    {
        base.ValidateRecipe(recipe);
        if (string.IsNullOrWhiteSpace(recipe.BuildCommand))
            throw new ArgumentException("DeploymentRecipe.BuildCommand is required for kind 'library' (it produces the package).", nameof(recipe));
    }

    protected override async Task ProbeReadyAsync(
        ISandbox sandbox,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        // {artifact} substitutes a shell-quoted form so a harness template
        // like `nuget restore {artifact}` survives spaces / quote characters
        // in the produced package path. Recipes that need the unquoted form
        // can read ArtifactPath through Environment.
        var quotedArtifact = Shell.Quote(recipe.ArtifactPath);
        var harness = recipe.Settings.TryGetValue(SettingsKeyHarnessCommand, out var h) && !string.IsNullOrWhiteSpace(h)
            ? h.Replace("{artifact}", quotedArtifact, StringComparison.Ordinal)
            : null;
        if (harness is null)
            return; // build succeeded; no harness configured.

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", harness],
            WorkingDirectory = context.WorkingDirectory,
            ExtraEnvironment = recipe.Environment,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"library harness '{harness}' exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override DeploymentEndpoint BuildEndpoint(ISandbox sandbox, DeploymentRecipe recipe, DeploymentContext context)
        => new()
        {
            Kind = DeploymentEndpointKind.Library,
            Path = recipe.ArtifactPath,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sandbox.id"] = sandbox.Id,
            },
        };
}
