using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <c>Upstream.PluginConfig</c> is accessible to plugins at runtime
/// via <see cref="IUpstreamPluginHost.GetProjectUpstreamConfig"/>.
/// </summary>
public sealed class UpstreamPluginConfigPassThroughTests
{
    private static readonly ProjectId ProjectId = new("cfg-project");

    private static readonly IReadOnlyDictionary<string, string> ExpectedConfig =
        new Dictionary<string, string>
        {
            ["BaseUrl"] = "https://gitea.mycompany.example/api/v1",
            ["Owner"] = "myteam",
            ["Repository"] = "myproject",
        };

    private static readonly Project SampleProject = new()
    {
        Id = ProjectId,
        DisplayName = "Config Test Project",
        RepositoryUrl = "https://gitea.mycompany.example/myteam/myproject.git",
        Upstream = new ProjectUpstream
        {
            Kind = "gitea",
            PluginConfig = ExpectedConfig,
        },
    };

    [Fact]
    public void GetProjectUpstreamConfig_ReturnsProjectPluginConfig()
    {
        var repo = new InMemoryProjectRepository(SampleProject);
        var host = BuildPluginHost(repo);

        var config = host.GetProjectUpstreamConfig(ProjectId);

        Assert.Equal("https://gitea.mycompany.example/api/v1", config["BaseUrl"]);
        Assert.Equal("myteam", config["Owner"]);
        Assert.Equal("myproject", config["Repository"]);
    }

    [Fact]
    public void GetProjectUpstreamConfig_UnknownProject_ReturnsEmpty()
    {
        var repo = new InMemoryProjectRepository(SampleProject);
        var host = BuildPluginHost(repo);

        var config = host.GetProjectUpstreamConfig(new ProjectId("does-not-exist"));

        Assert.Empty(config);
    }

    [Fact]
    public void GetProjectUpstreamConfig_NoPluginConfig_ReturnsEmpty()
    {
        var projectWithNoConfig = SampleProject with
        {
            Upstream = new ProjectUpstream { Kind = "gitea" },
        };
        var repo = new InMemoryProjectRepository(projectWithNoConfig);
        var host = BuildPluginHost(repo);

        var config = host.GetProjectUpstreamConfig(ProjectId);

        Assert.Empty(config);
    }

    [Fact]
    public void GetProjectUpstreamConfig_FullDictAccessibleByPlugin()
    {
        // Simulates a plugin reading config during CompleteAsync.
        var repo = new InMemoryProjectRepository(SampleProject);
        var host = BuildPluginHost(repo);

        var plugin = new ConfigReadingFakeRemote(host);
        var outcome = plugin.ReadConfig(ProjectId);

        Assert.Equal(3, outcome.Count);
        Assert.True(outcome.ContainsKey("BaseUrl"));
    }

    private static PluginHost BuildPluginHost(IProjectRepository repo)
    {
        return new PluginHost(
            pluginId: "test.gitea",
            loggerFactory: NullLoggerFactory.Instance,
            configuration: new ConfigurationBuilder().Build(),
            projectConfigResolver: projectId =>
            {
                var project = repo.GetAsync(projectId, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return project?.Upstream.PluginConfig ?? new Dictionary<string, string>();
            });
    }
}

internal sealed class ConfigReadingFakeRemote
{
    private readonly IUpstreamPluginHost _upstreamHost;

    public ConfigReadingFakeRemote(IPluginHost host)
        => _upstreamHost = (IUpstreamPluginHost)host;

    public IReadOnlyDictionary<string, string> ReadConfig(ProjectId projectId)
        => _upstreamHost.GetProjectUpstreamConfig(projectId);
}
