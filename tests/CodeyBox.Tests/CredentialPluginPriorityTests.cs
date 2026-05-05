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

    // ── Segmented-constructor integration tests ───────────────────────────────

    [Fact]
    public async Task SegmentedConstructor_PriorityListFiltersAndOrdersPlugins()
    {
        var callLog = new List<string>();

        // builtInFirst returns null, pluginB returns a credential, pluginA returns null.
        var builtInFirst = new TrackingProvider("built-in-first", callLog);
        var pluginA = new TrackingProvider("a", callLog);
        var pluginB = new ReturningProvider("b", callLog, MakeCredential());
        var builtInLast = new TrackingProvider("built-in-last", callLog);

        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins =
        [
            ("a", pluginA),
            ("b", pluginB),
        ];

        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [builtInFirst],
            namedPlugins: namedPlugins,
            builtInLast: [builtInLast]);

        // Priority: b before a — chain stops at b because it returns a credential.
        var result = await chain.GetAsync(AgentKind.Claude, ["b", "a"]);

        Assert.NotNull(result);
        Assert.Equal(["built-in-first", "b"], callLog);
    }

    [Fact]
    public async Task SegmentedConstructor_EmptyPriorityFallsBackToGlobalOrder()
    {
        var callLog = new List<string>();

        var builtInFirst = new TrackingProvider("built-in-first", callLog);
        var pluginA = new TrackingProvider("a", callLog);
        var pluginB = new TrackingProvider("b", callLog);
        var builtInLast = new TrackingProvider("built-in-last", callLog);

        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins =
        [
            ("a", pluginA),
            ("b", pluginB),
        ];

        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [builtInFirst],
            namedPlugins: namedPlugins,
            builtInLast: [builtInLast]);

        // Empty priority → falls back to global discovery order (all providers).
        await chain.GetAsync(AgentKind.Claude, []);

        Assert.Equal(["built-in-first", "a", "b", "built-in-last"], callLog);
    }

    [Fact]
    public async Task SegmentedConstructor_ExpiringCredentialCachedThenRefetchedAfterExpiry()
    {
        var callLog = new List<string>();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expiry = baseTime.AddMinutes(5);
        var clock = baseTime;

        var plugin = new ReturningProvider("plugin", callLog,
            MakeCredential(expiresAt: expiry));

        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins =
        [
            ("plugin", plugin),
        ];

        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [],
            namedPlugins: namedPlugins,
            builtInLast: [],
            utcNow: () => clock);

        // First call: walks the chain.
        await chain.GetAsync(AgentKind.Claude, ["plugin"]);
        Assert.Single(callLog);

        // Second call before expiry: served from cache — chain not walked again.
        await chain.GetAsync(AgentKind.Claude, ["plugin"]);
        Assert.Single(callLog);

        // Advance clock past expiry: next call refetches.
        clock = expiry.AddSeconds(1);
        await chain.GetAsync(AgentKind.Claude, ["plugin"]);
        Assert.Equal(2, callLog.Count);
    }

    [Fact]
    public async Task SegmentedConstructor_MissingPluginIdInPriorityIsSkipped()
    {
        var callLog = new List<string>();

        var pluginA = new TrackingProvider("a", callLog);

        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins =
        [
            ("a", pluginA),
        ];

        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [],
            namedPlugins: namedPlugins,
            builtInLast: [],
            log: NullLogger.Instance);

        // "nonexistent" is silently skipped; "a" is tried normally.
        await chain.GetAsync(AgentKind.Claude, ["nonexistent", "a"]);

        Assert.Equal(["a"], callLog);
    }

    [Fact]
    public async Task SegmentedConstructor_NullByteInPriorityThrows()
    {
        IReadOnlyList<(string Id, ICredentialProvider Provider)> namedPlugins =
        [
            ("a", new TrackingProvider("a", [])),
        ];

        IProjectAwareCredentialProvider chain = new ChainedCredentialProvider(
            builtInFirst: [],
            namedPlugins: namedPlugins,
            builtInLast: []);

        // A priority ID containing a null byte must be rejected.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            chain.GetAsync(AgentKind.Claude, ["a\0b"]));
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private static AgentCredential MakeCredential(DateTimeOffset? expiresAt = null) =>
        new(AgentKind.Claude,
            new Dictionary<string, string>(),
            new Dictionary<string, string>())
        {
            ExpiresAt = expiresAt,
        };

    private sealed class TrackingProvider(string id, List<string> log) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            log.Add(id);
            return Task.FromResult<AgentCredential?>(null);
        }
    }

    private sealed class ReturningProvider(
        string id,
        List<string> log,
        AgentCredential credential) : ICredentialProvider
    {
        public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        {
            log.Add(id);
            return Task.FromResult<AgentCredential?>(credential);
        }
    }
}
