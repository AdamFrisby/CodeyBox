using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.ExploratoryTesting.Recipes;

/// <summary>
/// CodeyBox's own admin web as an app-under-test target: the orchestrator API
/// plus the Blazor Admin.Web, served from a deterministic seeded instance
/// with fake agents (no LLM, no VM — see <c>codeybox-harness admin-seeded</c>).
/// Analogous to <see cref="JobTrackRecipe"/> but self-hosting: the "target
/// source" is this same repository, mounted at
/// <see cref="SandboxConventions.WorkDir"/>.
///
/// <para><b>Determinism is in the recipe, not the harness:</b> the seed step
/// deletes any prior database and re-seeds from a fixed seed, so two launches
/// produce the same projects / work items / audits / releases to drive.</para>
/// </summary>
public static class CodeyBoxAdminRecipe
{
    /// <summary>Orchestrator API listener inside the sandbox.</summary>
    public const string ApiUrl = "http://localhost:5050";

    /// <summary>Blazor Admin.Web listener inside the sandbox (the E2E entry point).</summary>
    public const string DefaultEntryUrl = "http://localhost:5070";

    /// <summary>Determinism seed shared by the seed step and the serve step.</summary>
    public const int DefaultSeed = 42;

    /// <summary>In-VM path of the throwaway seeded SQLite database.</summary>
    public const string SeedDbPath = "/work/.codeybox-harness/admin-seed.db";

    /// <summary>
    /// Builds the canonical CodeyBox-admin recipe.
    /// </summary>
    /// <param name="sourceMount">
    /// Host directory holding this repository, mounted read-write at
    /// <see cref="SandboxConventions.WorkDir"/> so <c>dotnet build</c> can
    /// write <c>bin/</c> and <c>obj/</c>. Determinism of the source IS the
    /// caller's contract (pin a commit before launch).
    /// </param>
    /// <param name="seed">Determinism seed forwarded to seed + serve steps.</param>
    public static WebAppRecipe Default(string sourceMount, int seed = DefaultSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMount);
        if (seed < 0)
            throw new ArgumentOutOfRangeException(nameof(seed), "Seed must be non-negative.");

        var seedArg = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new WebAppRecipe
        {
            TargetName = "codeybox-admin",
            ImageReference = string.Empty,
            Mounts =
            [
                new SandboxMount
                {
                    SandboxPath = SandboxConventions.WorkDir,
                    HostPath = sourceMount,
                    ReadOnly = false,
                },
            ],
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_LAUNCH_PROFILE"] = "",
            },
            BuildSteps =
            [
                new RecipeStep
                {
                    Label = "install-firefox",
                    Command = ["sudo", "apt-get", "install", "-y", "--no-install-recommends", "firefox-esr"],
                },
                new RecipeStep
                {
                    Label = "dotnet-restore",
                    Command = ["dotnet", "restore", "CodeyBox.slnx"],
                },
                new RecipeStep
                {
                    Label = "dotnet-build",
                    Command = ["dotnet", "build", "CodeyBox.slnx", "--no-restore", "-c", "Release"],
                },
            ],
            SeedSteps =
            [
                new RecipeStep
                {
                    Label = "reset-db-dir",
                    Command = ["sh", "-c", "rm -rf /work/.codeybox-harness && mkdir -p /work/.codeybox-harness"],
                },
                new RecipeStep
                {
                    Label = "seed-admin-db",
                    Command = ["dotnet", "run", "--no-build", "-c", "Release",
                        "--project", "tools/CodeyBox.Harness",
                        "--", "admin-seeded", "seed",
                        "--seed", seedArg,
                        "--db", SeedDbPath],
                },
            ],
            RunCommand = new RecipeStep
            {
                Label = "serve-seeded-admin",
                Command = ["dotnet", "run", "--no-build", "-c", "Release",
                    "--project", "tools/CodeyBox.Harness",
                    "--", "admin-seeded", "serve",
                    "--seed", seedArg,
                    "--db", SeedDbPath,
                    "--api-url", ApiUrl,
                    "--admin-url", DefaultEntryUrl],
            },
            EntryUrl = DefaultEntryUrl,
            BrowserCommand = ["firefox", "--new-window", "$URL"],
            NetworkProfile = SandboxConventions.GraphicalNetworkProfile,
        };
    }
}
