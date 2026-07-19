namespace CodeyBox.Core;

/// <summary>
/// Binds the flat, config-friendly <see cref="DeploymentRecipeConfig"/>
/// shape into the strongly-typed <see cref="DeploymentRecipe"/> the drivers
/// consume. Kept in Core so project configuration can bind deployment recipes
/// without depending on a concrete deployment implementation assembly.
/// </summary>
public static class DeploymentRecipeBinder
{
    public static DeploymentRecipe? ToRecipe(DeploymentRecipeConfig? cfg)
    {
        if (cfg is null) return null;
        if (string.IsNullOrWhiteSpace(cfg.Kind))
            throw new InvalidOperationException("DeploymentRecipe is missing 'Kind'.");
        if (string.IsNullOrWhiteSpace(cfg.ImageReference))
            throw new InvalidOperationException(
                $"DeploymentRecipe (kind '{cfg.Kind}') is missing 'ImageReference'.");

        var defaults = new DeploymentRecipe
        {
            Kind = cfg.Kind!,
            ImageReference = cfg.ImageReference!,
        };
        var ports = (cfg.Ports ?? []).ToList();
        var env = ToReadOnly(cfg.Environment);
        var settings = ToReadOnly(cfg.Settings);
        var services = new List<DeploymentService>();
        foreach (var s in cfg.Services ?? [])
        {
            if (s is null)
                throw new InvalidOperationException("DeploymentRecipe.Services cannot contain null entries.");
            services.Add(new DeploymentService
            {
                Name = s.Name ?? throw new InvalidOperationException("DeploymentService is missing 'Name'."),
                ImageReference = s.ImageReference ?? throw new InvalidOperationException(
                    $"DeploymentService '{s.Name}' is missing 'ImageReference'."),
                RunCommand = s.RunCommand,
                Environment = ToReadOnly(s.Environment),
                Ports = (s.Ports ?? []).ToList(),
                HealthEndpoint = s.HealthEndpoint,
            });
        }

        return defaults with
        {
            BuildCommand = cfg.BuildCommand ?? string.Empty,
            RunCommand = cfg.RunCommand,
            ArtifactPath = cfg.ArtifactPath,
            Environment = env,
            Services = services,
            Ports = ports,
            HealthEndpoint = cfg.HealthEndpoint,
            StartupTimeout = ResolveSecondsTimeout(cfg.StartupTimeoutSeconds, defaults.StartupTimeout),
            MaxLifetime = ResolveMinutesTimeout(cfg.MaxLifetimeMinutes, defaults.MaxLifetime),
            NetworkProfile = cfg.NetworkProfile,
            Settings = settings,
        };
    }

    private static IReadOnlyDictionary<string, string> ToReadOnly(Dictionary<string, string>? src)
    {
        if (src is null || src.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        return new Dictionary<string, string>(src, StringComparer.Ordinal);
    }

    private static TimeSpan ResolveSecondsTimeout(double? seconds, TimeSpan fallback)
    {
        if (!seconds.HasValue) return fallback;
        if (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || seconds.Value <= 0)
            throw new InvalidOperationException("DeploymentRecipe.StartupTimeoutSeconds must be a positive finite number.");
        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static TimeSpan ResolveMinutesTimeout(double? minutes, TimeSpan fallback)
    {
        if (!minutes.HasValue) return fallback;
        if (double.IsNaN(minutes.Value) || double.IsInfinity(minutes.Value) || minutes.Value <= 0)
            throw new InvalidOperationException("DeploymentRecipe.MaxLifetimeMinutes must be a positive finite number.");
        return TimeSpan.FromMinutes(minutes.Value);
    }
}

/// <summary>
/// Config-binding shape that mirrors <see cref="DeploymentRecipe"/> but
/// matches what the JSON configuration provider can deserialize directly
/// (nullable scalar fields, plain List/Dictionary properties). Resolved into
/// the immutable record via <see cref="DeploymentRecipeBinder.ToRecipe"/>.
/// </summary>
public sealed class DeploymentRecipeConfig
{
    public string? Kind { get; set; }
    public string? ImageReference { get; set; }
    public string? BuildCommand { get; set; }
    public string? RunCommand { get; set; }
    public string? ArtifactPath { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<DeploymentServiceConfig>? Services { get; set; }
    public List<int>? Ports { get; set; }
    public string? HealthEndpoint { get; set; }
    public double? StartupTimeoutSeconds { get; set; }
    public double? MaxLifetimeMinutes { get; set; }
    public string? NetworkProfile { get; set; }
    public Dictionary<string, string>? Settings { get; set; }
}

public sealed class DeploymentServiceConfig
{
    public string? Name { get; set; }
    public string? ImageReference { get; set; }
    public string? RunCommand { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<int>? Ports { get; set; }
    public string? HealthEndpoint { get; set; }
}
