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
/// <see cref="SettingsKeyHarnessCommand"/> setting. It is mandatory: a
/// build-only recipe does not prove package restore or downstream
/// consumption and is rejected during validation.</para>
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
        if (string.IsNullOrWhiteSpace(recipe.ArtifactPath))
            throw new ArgumentException("DeploymentRecipe.ArtifactPath is required for kind 'library'.", nameof(recipe));
        if (!recipe.Settings.TryGetValue(SettingsKeyHarnessCommand, out var harness) || string.IsNullOrWhiteSpace(harness))
            throw new ArgumentException("DeploymentRecipe.Settings['harness-command'] is required for kind 'library'.", nameof(recipe));
    }

    protected override async Task ProbeReadyAsync(
        IDeploymentSubstrate substrate,
        DeploymentRecipe recipe,
        DeploymentContext context,
        CancellationToken ct)
    {
        // {artifact} substitutes a shell-quoted form so a harness template
        // like `nuget restore {artifact}` survives spaces / quote characters
        // in the produced package path. Recipes that need the unquoted form
        // can read ArtifactPath through Environment.
        var quotedArtifact = Shell.Quote(recipe.ArtifactPath);
        var harness = recipe.Settings[SettingsKeyHarnessCommand]
            .Replace("{artifact}", quotedArtifact, StringComparison.Ordinal);

        var result = await RunDeploymentExecAsync(
            substrate,
            recipe,
            context,
            "library harness",
            ["sh", "-c", harness],
            recipe.Environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"library harness '{Tail(harness)}' exited {result.ExitCode}; stderr tail: {Tail(result.Stderr)}");
    }

    protected override DeploymentEndpoint BuildEndpoint(IDeploymentSubstrate substrate, DeploymentRecipe recipe, DeploymentContext context)
        => new()
        {
            Kind = DeploymentEndpointKind.Library,
            Path = recipe.ArtifactPath,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["substrate.id"] = substrate.Id,
                ["endpoint.scope"] = "sandbox-artifact",
                ["sandbox.path"] = recipe.ArtifactPath!,
            },
        };
}
