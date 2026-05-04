using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <see cref="Project.CredentialProviderPriority"/> reorders plugin
/// providers and that unknown plugin IDs in the priority list are skipped with a
/// warning rather than causing an exception.
/// </summary>
public sealed class CredentialPluginPriorityTests
{
    [Fact]
    public async Task PriorityList_ReordersPluginsAsSpecified()
    {
        var callLog = new List<string>();

        var pluginA = new TrackingProvider("a", callLog);
        var pluginB = new TrackingProvider("b", callLog);

        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com",
            CredentialProviderPriority = ["b", "a"],   // B before A
        };

        IReadOnlyList<(string Id, ICredentialProvider Provider)> allPlugins =
        [
            ("a", pluginA),
            ("b", pluginB),
        ];

        var ordered = ChainedCredentialProvider.OrderByPriority(allPlugins, project.CredentialProviderPriority);
        var chain = new ChainedCredentialProvider(ordered.Select(p => p.Provider));

        await chain.GetAsync(AgentKind.Claude);

        Assert.Equal(["b", "a"], callLog);
    }

    [Fact]
    public async Task PriorityList_EmptyMeansDiscoveryOrder()
    {
        var callLog = new List<string>();

        var pluginA = new TrackingProvider("a", callLog);
        var pluginB = new TrackingProvider("b", callLog);

        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com",
            CredentialProviderPriority = [],   // empty = use all in discovery order
        };

        IReadOnlyList<(string Id, ICredentialProvider Provider)> allPlugins =
        [
            ("a", pluginA),
            ("b", pluginB),
        ];

        var ordered = ChainedCredentialProvider.OrderByPriority(allPlugins, project.CredentialProviderPriority);
        var chain = new ChainedCredentialProvider(ordered.Select(p => p.Provider));

        await chain.GetAsync(AgentKind.Claude);

        // Discovery order preserved when priority is empty.
        Assert.Equal(["a", "b"], callLog);
    }

    [Fact]
    public async Task PriorityList_MissingPluginIdSkippedWithWarning()
    {
        var callLog = new List<string>();
        var missingIds = new List<string>();

        var pluginA = new TrackingProvider("a", callLog);

        IReadOnlyList<(string Id, ICredentialProvider Provider)> allPlugins =
        [
            ("a", pluginA),
        ];

        // Priority references "nonexistent" which is not installed.
        var priority = (IReadOnlyList<string>)["nonexistent", "a"];

        var ordered = ChainedCredentialProvider.OrderByPriority(
            allPlugins,
            priority,
            onMissing: id => missingIds.Add(id));

        var chain = new ChainedCredentialProvider(ordered.Select(p => p.Provider));
        await chain.GetAsync(AgentKind.Claude);

        Assert.Contains("nonexistent", missingIds);
        Assert.Equal(["a"], callLog);
    }

    [Fact]
    public async Task PriorityList_ExcludesUnlistedPlugins()
    {
        // Project lists only "a" — plugin "b" should never be tried.
        var callLog = new List<string>();
        var pluginA = new TrackingProvider("a", callLog);
        var pluginB = new TrackingProvider("b", callLog);

        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com",
            CredentialProviderPriority = ["a"],
        };

        IReadOnlyList<(string Id, ICredentialProvider Provider)> allPlugins =
        [
            ("a", pluginA),
            ("b", pluginB),
        ];

        var ordered = ChainedCredentialProvider.OrderByPriority(allPlugins, project.CredentialProviderPriority);
        var chain = new ChainedCredentialProvider(ordered.Select(p => p.Provider));

        await chain.GetAsync(AgentKind.Claude);

        Assert.Equal(["a"], callLog);
        Assert.DoesNotContain("b", callLog);
    }

    [Fact]
    public void PriorityList_ProjectModelDefaultIsEmpty()
    {
        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com",
        };

        // Default is empty list (use global discovery order).
        Assert.Empty(project.CredentialProviderPriority);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class TrackingProvider(string id, List<string> log) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            log.Add(id);
            return Task.FromResult<AgentCredential?>(null);
        }
    }
}
