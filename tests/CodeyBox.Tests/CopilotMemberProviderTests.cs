using CodeyBox.Agents.Copilot;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Per-member Copilot provider selection: a member's effective provider is its
/// named override, else the agent-global provider. Verifies the member/global
/// precedence, sibling isolation, the native-subscription path (no provider
/// anywhere), fail-closed resolution, and distinct route/quota identities for
/// the BYOK-harness + native-subscription pair.
/// </summary>
public sealed class CopilotMemberProviderTests
{
    private static CopilotOptions GlobalByok(string baseUrl = "https://global.example/v1") => new()
    {
        Provider = new CopilotProviderOptions { BaseUrl = baseUrl },
    };

    private static Dictionary<string, CopilotProviderOptions> CatalogWith(params (string Name, string BaseUrl)[] entries)
    {
        var catalog = new Dictionary<string, CopilotProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, baseUrl) in entries)
            catalog[name] = new CopilotProviderOptions { BaseUrl = baseUrl };
        return catalog;
    }

    private static AgentMembership CopilotMember(string? instanceId, string? providerName = null) => new()
    {
        Agent = AgentKind.Copilot,
        InstanceId = instanceId,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
        ProviderReference = providerName is null ? null : new AgentProviderReference { Name = providerName },
    };

    private static async Task<IReadOnlyDictionary<string, string>> InvocationEnvAsync(
        IAgentRunner runner, CapturingSandbox sandbox)
    {
        await runner.RunAsync(sandbox, "/work", "do the thing", credential: null);
        Assert.NotNull(sandbox.CapturedExec);
        return sandbox.CapturedExec!.ExtraEnvironment ?? new Dictionary<string, string>();
    }

    [Fact]
    public void ResolveEffectiveProvider_NoOverride_ReturnsGlobalProvider()
    {
        var global = GlobalByok();

        var effective = CopilotProviderResolver.ResolveEffectiveProvider(
            CopilotMember("harness"), global);

        Assert.Same(global.Provider, effective);
    }

    [Fact]
    public void ForMember_NoOverride_ReturnsSelf()
    {
        var runner = new CopilotAgentRunner { Options = GlobalByok() };

        var bound = ((IMemberScopedAgentRunner)runner).ForMember(CopilotMember("sub"));

        Assert.Same(runner, bound);
    }

    [Fact]
    public async Task BoundRunner_UsesMemberOverride_NotGlobal()
    {
        var options = GlobalByok("https://global.example/v1");
        options.Providers["byok"] = new CopilotProviderOptions { BaseUrl = "https://opencode.ai/zen/go/v1" };
        var runner = new CopilotAgentRunner { Options = options };

        var bound = ((IMemberScopedAgentRunner)runner).ForMember(CopilotMember("harness", "byok"));
        var env = await InvocationEnvAsync(bound, new CapturingSandbox());

        Assert.Equal("https://opencode.ai/zen/go/v1", env["COPILOT_PROVIDER_BASE_URL"]);
    }

    [Fact]
    public async Task SiblingMembers_WithDifferentOverrides_AreUnaffectedByEachOther()
    {
        var options = new CopilotOptions();
        options.Providers["byok-a"] = new CopilotProviderOptions { BaseUrl = "https://a.example/v1" };
        options.Providers["byok-b"] = new CopilotProviderOptions
        {
            BaseUrl = "https://b.example/v1",
            Type = "azure",
        };
        var runner = new CopilotAgentRunner { Options = options };
        var scoped = (IMemberScopedAgentRunner)runner;

        var envA = await InvocationEnvAsync(scoped.ForMember(CopilotMember("harness", "byok-a")), new CapturingSandbox());
        var envB = await InvocationEnvAsync(scoped.ForMember(CopilotMember("sub", "byok-b")), new CapturingSandbox());
        var envGlobal = await InvocationEnvAsync(runner, new CapturingSandbox());

        Assert.Equal("https://a.example/v1", envA["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("https://b.example/v1", envB["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("azure", envB["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("openai", envA["COPILOT_PROVIDER_TYPE"]);
        // The shared runner still renders the agent-global (unconfigured → native) path.
        Assert.DoesNotContain("COPILOT_PROVIDER_BASE_URL", envGlobal.Keys);
    }

    [Fact]
    public void ResolveEffectiveProvider_NoProviderAnywhere_MeansNativeSubscription()
    {
        var global = new CopilotOptions();

        var effective = CopilotProviderResolver.ResolveEffectiveProvider(
            CopilotMember("sub"), global);

        Assert.False(effective.IsConfigured);
        Assert.Empty(CopilotAgentRunner.BuildProviderEnvironment(effective, offline: false));
    }

    [Fact]
    public async Task Invocation_NoProviderAnywhere_EmitsNoProviderEnvironment()
    {
        var runner = new CopilotAgentRunner { Options = new CopilotOptions() };

        var env = await InvocationEnvAsync(
            ((IMemberScopedAgentRunner)runner).ForMember(CopilotMember("sub")),
            new CapturingSandbox());

        Assert.DoesNotContain("COPILOT_PROVIDER_BASE_URL", env.Keys);
        Assert.DoesNotContain("COPILOT_PROVIDER_TYPE", env.Keys);
        Assert.DoesNotContain("COPILOT_PROVIDER_WIRE_API", env.Keys);
        Assert.DoesNotContain("COPILOT_OFFLINE", env.Keys);
    }

    [Fact]
    public void ResolveEffectiveProvider_UnresolvableName_ThrowsInsteadOfFallingBack()
    {
        var global = GlobalByok();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CopilotProviderResolver.ResolveEffectiveProvider(CopilotMember("harness", "missing"), global));

        Assert.Contains("missing", ex.Message);
        Assert.Contains("copilot/harness", ex.Message);
    }

    [Fact]
    public void ForMember_UnresolvableName_ThrowsInsteadOfRunningNative()
    {
        var runner = new CopilotAgentRunner { Options = GlobalByok() };

        Assert.Throws<InvalidOperationException>(() =>
            ((IMemberScopedAgentRunner)runner).ForMember(CopilotMember("harness", "missing")));
    }

    [Fact]
    public async Task BoundRunner_PreservesInjectedSessionIdGenerator()
    {
        // ForMember rebinds only the provider: an injected deterministic
        // session-id generator must survive onto the bound runner, otherwise
        // per-member invocations silently revert to random UUIDs (and lose
        // test determinism) whenever a member names an override.
        var options = GlobalByok("https://global.example/v1");
        options.Providers["byok"] = new CopilotProviderOptions
        {
            BaseUrl = "https://opencode.ai/zen/go/v1",
            Headers = [$"x-opencode-session: {CopilotAgentRunner.ProviderSessionIdPlaceholder}"],
        };
        var runner = new CopilotAgentRunner
        {
            Options = options,
            SessionIdGenerator = () => "11111111-2222-3333-4444-555555555555",
        };

        var bound = ((IMemberScopedAgentRunner)runner).ForMember(CopilotMember("harness", "byok"));
        var env = await InvocationEnvAsync(bound, new CapturingSandbox());

        Assert.Equal(
            "x-opencode-session: 11111111-2222-3333-4444-555555555555",
            env["COPILOT_PROVIDER_HEADERS"]);
        Assert.Equal(
            "11111111-2222-3333-4444-555555555555",
            env[CopilotAgentRunner.ProviderSessionIdEnvironmentVariable]);
    }

    [Fact]
    public void TryFind_MatchesCaseInsensitively()
    {
        var catalog = CatalogWith(("BYOK", "https://a.example/v1"));

        Assert.True(CopilotProviderResolver.TryFind(catalog, "byok", out var found));
        Assert.Equal("https://a.example/v1", found!.BaseUrl);
        Assert.False(CopilotProviderResolver.TryFind(catalog, "other", out _));
        Assert.False(CopilotProviderResolver.TryFind(null, "byok", out _));
    }

    [Fact]
    public void TwoCopilotMembers_WithDifferentInstanceIds_HaveDistinctQuotaIdentities()
    {
        var harness = CopilotMember("harness", "byok");
        var sub = CopilotMember("sub");

        Assert.Equal("copilot/harness", harness.RouteKey);
        Assert.Equal("copilot/sub", sub.RouteKey);

        var harnessKey = AgentQuotaMemberKey.From(harness);
        var subKey = AgentQuotaMemberKey.From(sub);

        Assert.NotEqual(harnessKey, subKey);
        Assert.Equal("copilot/harness", harnessKey.RouteKey);
        Assert.Equal("copilot/sub", subKey.RouteKey);
    }

    [Fact]
    public void BuildProviderEnvironment_OverrideWithoutBaseUrl_MeansNativeForThatMember()
    {
        // A named entry that exists but configures no backend resolves
        // successfully and renders the native-subscription path — an explicit
        // per-member opt-out of a globally-configured BYOK endpoint.
        var options = GlobalByok();
        options.Providers["sub"] = new CopilotProviderOptions();

        var effective = CopilotProviderResolver.ResolveEffectiveProvider(
            CopilotMember("sub", "sub"), options);

        Assert.Same(options.Providers["sub"], effective);
        Assert.Empty(CopilotAgentRunner.BuildProviderEnvironment(effective, offline: true));
    }
}
