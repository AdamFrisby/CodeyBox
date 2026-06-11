using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Binds <c>CodeyBox:AuthFailurePatterns</c> through the real config + options
/// pipeline and feeds the bound object to <see cref="AuthFailurePatternBinder"/>
/// — the exact path Program.cs uses to wire <see cref="IAgentAuthFailureClassifier"/>.
/// A drift between the operator-facing config section name, the dictionary
/// shape, the filter rules, or the conversion to <see cref="AuthFailurePattern"/>
/// would silently disable the operator's extensibility hook for new CLI login
/// prompts; the rest of the auth-detection plumbing would continue to function
/// only against the in-process default pattern list. Unit-testing the classifier
/// in isolation (with a hand-built dictionary) does not catch that drift.
/// </summary>
public sealed class AuthFailurePatternBindingTests
{
    [Fact]
    public void Build_AppliesAdditionalPerAgentPattern_FromBoundConfig()
    {
        var classifier = BindAndBuild(new Dictionary<string, string?>
        {
            ["CodeyBox:AuthFailurePatterns:antigravity:0:Pattern"] = "agy says: needs login",
            ["CodeyBox:AuthFailurePatterns:antigravity:1:Pattern"] = "  ", // whitespace-only is filtered
        });

        // Operator-supplied pattern fires under the configured agent kind only.
        var hit = classifier.Detect(AgentKind.Antigravity, stderr: "agy says: needs login", stdout: null);
        Assert.NotNull(hit);
        Assert.Equal(AgentFailureKind.AuthRequired, hit.Kind);

        // …but the same string against another agent kind does NOT trip the
        // per-agent override (defaults still apply, so a default match would
        // still hit — but this string is not in the defaults).
        Assert.Null(classifier.Detect(AgentKind.Codex, stderr: "agy says: needs login", stdout: null));
    }

    [Fact]
    public void Build_AppliesAdditionalPerAgentPattern_ToStdout()
    {
        var classifier = BindAndBuild(new Dictionary<string, string?>
        {
            ["CodeyBox:AuthFailurePatterns:antigravity:0:Pattern"] = "stdout says: needs login",
        });

        var hit = classifier.DetectDetailed(
            AgentKind.Antigravity,
            stderr: null,
            stdout: "stdout says: needs login");

        Assert.NotNull(hit);
        Assert.Equal(AgentFailureKind.AuthRequired, hit.Classification.Kind);
        Assert.True(hit.IsStdoutOnly);
        Assert.Null(classifier.Detect(AgentKind.Codex, stderr: null, stdout: "stdout says: needs login"));
    }

    [Fact]
    public void Build_PreservesBuiltInPatterns_AcrossAllAgents_EvenWithNoConfigEntries()
    {
        // No CodeyBox:AuthFailurePatterns section at all — the defaults must
        // still cover every agent kind. A regression that scoped the default
        // list to agents named in the config would silently break detection
        // for any operator who has not yet defined custom patterns.
        var classifier = BindAndBuild([]);

        var hit = classifier.Detect(AgentKind.Antigravity, stderr: null,
            stdout: "Authentication required. Please visit the URL to log in:\nWaiting for authentication (timeout 30s)...");
        Assert.NotNull(hit);
        Assert.Equal(AgentFailureKind.AuthRequired, hit.Kind);
    }

    [Fact]
    public void Build_DropsEmptyAgentKeys_FromConfig()
    {
        // A blank-keyed entry in config (operator typo) must not break the
        // dictionary build — the filter in the binder is load-bearing because
        // the underlying ToDictionary would throw on a whitespace key
        // duplicating another whitespace key, or worse, register a pattern
        // under no agent kind at all.
        var classifier = BindAndBuild(new Dictionary<string, string?>
        {
            ["CodeyBox:AuthFailurePatterns::0:Pattern"] = "blank-key entry",
            ["CodeyBox:AuthFailurePatterns:antigravity:0:Pattern"] = "real-entry",
        });

        Assert.NotNull(classifier.Detect(AgentKind.Antigravity, stderr: "real-entry", stdout: null));
    }

    private static AgentAuthFailureClassifier BindAndBuild(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>()
            .Bind(config.GetSection("CodeyBox"));
        using var provider = services.BuildServiceProvider();
        var cbOpts = provider.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
        return AuthFailurePatternBinder.Build(cbOpts);
    }
}
