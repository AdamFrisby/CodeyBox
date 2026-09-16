namespace CodeyBox.Tests;

/// <summary>
/// The outer harness injects <c>GIT_CONFIG_*</c> overrides (e.g.
/// <c>safe.bareRepository=explicit</c>) that break <c>LocalGitHost</c>
/// bare-repo plumbing: without scrubbing, pipeline tests fail fast or park
/// on multi-minute waits and exhaust the assembly run timeout. These tests
/// pin the scrubbing logic over an in-memory environment (deterministic under
/// parallel execution — never touches the process environment) plus the
/// assembly-load wiring that applies it to the real process.
/// </summary>
public sealed class AmbientGitConfigScrubTests
{
    private static TestSupport.DictionaryStringEnvironment Env(params (string Key, string? Value)[] entries)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            values[key] = value;
        return new TestSupport.DictionaryStringEnvironment(values);
    }

    [Fact]
    public void RemoveGitConfigOverrides_StripsHarnessTriple()
    {
        var env = Env(
            ("GIT_CONFIG_COUNT", "3"),
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"),
            ("GIT_CONFIG_KEY_1", "credential.interactive"),
            ("GIT_CONFIG_VALUE_1", "never"),
            ("GIT_CONFIG_KEY_2", "core.fsmonitor"),
            ("GIT_CONFIG_VALUE_2", ""),
            ("PATH", "/usr/bin"));

        TestSupport.RemoveGitConfigOverrides(env);

        Assert.Equal(["PATH"], env.Keys);
        Assert.Equal("/usr/bin", env.Get("PATH"));
    }

    [Fact]
    public void RemoveGitConfigOverrides_RemovesStrayPairsWithoutCount()
    {
        var env = Env(
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"),
            ("UNRELATED", "keep"));

        TestSupport.RemoveGitConfigOverrides(env);

        Assert.Equal(["UNRELATED"], env.Keys);
    }

    [Fact]
    public void RemoveGitConfigOverrides_RemovesSlotsBeyondGarbageCount()
    {
        var env = Env(
            ("GIT_CONFIG_COUNT", "not-a-number"),
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"));

        TestSupport.RemoveGitConfigOverrides(env);

        Assert.Empty(env.Keys);
    }

    [Fact]
    public void RemoveGitConfigOverrides_LeavesUnrelatedEntriesAlone()
    {
        var env = Env(
            ("GIT_CONFIG_COUNT", "1"),
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"),
            ("DOTNET_CLI_HOME", "/tmp/x"),
            ("NUGET_PACKAGES", "/tmp/y"));

        TestSupport.RemoveGitConfigOverrides(env);

        Assert.Equal(["DOTNET_CLI_HOME", "NUGET_PACKAGES"], env.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void IndexedGitConfigSlots_CoversCountGovernedAndStraySlots()
    {
        var env = Env(
            ("GIT_CONFIG_COUNT", "2"),
            ("GIT_CONFIG_KEY_5", "x"),
            ("GIT_CONFIG_VALUE_7", "y"));

        Assert.Equal([0, 1, 5, 7], TestSupport.IndexedGitConfigSlots(env));
    }

    [Fact]
    public void IndexedGitConfigSlots_BoundsAbsurdCount()
    {
        var env = Env(("GIT_CONFIG_COUNT", "1000000"));

        var slots = TestSupport.IndexedGitConfigSlots(env);

        Assert.Equal(TestSupport.MaxCountGovernedSlots, slots.Count);
        Assert.Equal(Enumerable.Range(0, TestSupport.MaxCountGovernedSlots).ToArray(), slots);
    }

    [Fact]
    public void RemoveGitConfigOverrides_NeutralizesAbsurdCountWithRealPairs()
    {
        var env = Env(
            ("GIT_CONFIG_COUNT", "1000000"),
            ("GIT_CONFIG_KEY_0", "safe.bareRepository"),
            ("GIT_CONFIG_VALUE_0", "explicit"));

        TestSupport.RemoveGitConfigOverrides(env);

        Assert.Empty(env.Keys);
    }

    /// <summary>
    /// The assembly-load hook must have stripped any harness-injected
    /// overrides before tests ran. Vacuous in a clean environment; decisive
    /// under the harness, where its absence breaks every git-plumbing test.
    /// </summary>
    [Fact]
    public void AssemblyLoadHook_LeavesNoGitConfigCountInProcess()
    {
        Assert.Null(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));
    }
}
