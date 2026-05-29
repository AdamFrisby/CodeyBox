using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.ExploratoryTesting.Recipes;

/// <summary>
/// Pilot target recipe for JobTrack — an ASP.NET API + Blazor UI app. Lives
/// here in the engine repo, not in the JobTrack source tree, because the
/// recipe IS engine configuration; JobTrack is just the first target the
/// engine drives.
///
/// <para><b>Determinism is in the recipe, not the harness:</b> the build /
/// seed steps run deterministic commands (a fixed-commit checkout, a
/// fixed-seed migration). The harness re-runs them verbatim every launch,
/// so two launches against the same recipe produce the same UI state to
/// drive.</para>
/// </summary>
public static class JobTrackRecipe
{
    /// <summary>
    /// JobTrack served on the in-VM loopback at port 5080. The harness
    /// probes this URL for HTTP readiness and points the in-VM browser at
    /// it. Kept off 5000 to avoid colliding with another ASP.NET default
    /// that might already be running in the VM image.
    /// </summary>
    public const string DefaultEntryUrl = "http://localhost:5080";

    /// <summary>
    /// Builds the canonical JobTrack recipe.
    /// </summary>
    /// <param name="sourceMount">
    /// Host directory holding the JobTrack source tree, mounted read-write
    /// at <see cref="SandboxConventions.WorkDir"/> so <c>dotnet build</c>
    /// can write its <c>bin/</c> and <c>obj/</c>. The caller is responsible
    /// for ensuring this directory is at a known commit before launch —
    /// determinism of the source IS the caller's contract.
    /// </param>
    public static WebAppRecipe Default(string sourceMount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMount);

        return new WebAppRecipe
        {
            TargetName = "jobtrack",
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
                // ASP.NET binds Kestrel here. Pin to localhost so the harness's
                // in-VM curl probe and browser both reach the same listener; the
                // graphical bridge inside the VM has loopback access regardless
                // of the host-side bridge profile.
                ["ASPNETCORE_URLS"] = DefaultEntryUrl,
                // EF Core SQLite file lives under the source mount so seed
                // state is reproducible. The seed step deletes any prior file
                // and re-applies migrations from a fixed-seed fixture.
                ["JOBTRACK_DB_PATH"] = "/work/.codeybox-harness/jobtrack.db",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                // Disable launchSettings.json's browser auto-open so the
                // harness controls when and how the browser is launched.
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
                    Command = ["dotnet", "restore", "JobTrack.sln"],
                },
                new RecipeStep
                {
                    Label = "dotnet-build",
                    Command = ["dotnet", "build", "JobTrack.sln", "--no-restore", "-c", "Release"],
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
                    Label = "apply-migrations",
                    Command = ["dotnet", "ef", "database", "update", "--project", "src/JobTrack.Api"],
                },
                new RecipeStep
                {
                    Label = "load-fixture",
                    Command = ["dotnet", "run", "--project", "tools/JobTrack.SeedFixtures", "--", "--seed", "1"],
                },
            ],
            RunCommand = new RecipeStep
            {
                Label = "jobtrack-api",
                Command = ["dotnet", "run", "--no-build", "-c", "Release", "--project", "src/JobTrack.Api"],
            },
            EntryUrl = DefaultEntryUrl,
            BrowserCommand = ["firefox", "--new-window", "$URL"],
            NetworkProfile = SandboxConventions.GraphicalNetworkProfile,
        };
    }
}
