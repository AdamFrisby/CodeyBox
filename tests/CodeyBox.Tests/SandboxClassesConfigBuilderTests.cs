using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="SandboxClassesConfigBuilder"/>: fail-closed load
/// validation (every rejection names the offending member and value), the
/// per-member worker/sandbox deadlock invariant, and projection of a valid
/// class to <see cref="SandboxPlacementMember"/>.
/// Each test asserts a value the code under test produced: the tests below
/// fail if validation accepts a bad catalog or if projection drops an
/// attribute, and pass only on the exact guarded behaviour.
/// </summary>
public sealed class SandboxClassesConfigBuilderTests
{
    private static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "incus", "multipass", "multipass-remote", "sprites", "bubblewrap", "process",
    };

    private static SandboxMemberOptions ValidMember() => new()
    {
        MemberId = "local",
        ProviderKind = "incus",
        Capacity = 4,
        PreferenceScore = 100,
        Capabilities = new() { "baseline-bake" },
        NetworkProfiles = new() { "default" },
        Credentials = new() { "codex" },
    };

    private static List<SandboxClassOptions> ValidClasses() => new()
    {
        new()
        {
            Id = "default",
            DisplayName = "Default",
            Members = { ValidMember() },
        },
    };

    private static IReadOnlyList<SandboxClass> Build(
        List<SandboxClassOptions> classes,
        int workers = 1,
        IReadOnlySet<string>? registered = null) =>
        SandboxClassesConfigBuilder.Build(classes, workers, registered ?? Registered, NullLogger.Instance);

    private static InvalidOperationException AssertBuildFails(
        List<SandboxClassOptions> classes,
        int workers = 1,
        IReadOnlySet<string>? registered = null) =>
        Assert.Throws<InvalidOperationException>(() => Build(classes, workers, registered));

    [Fact]
    public void Build_EmptyCatalog_ReturnsEmpty()
    {
        var catalog = Build(new List<SandboxClassOptions>());

        Assert.Empty(catalog);
    }

    [Fact]
    public void Build_ClassWithNoMembers_IsRejectedNamingTheClass()
    {
        var classes = new List<SandboxClassOptions> { new() { Id = "empty" } };

        var ex = AssertBuildFails(classes);

        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least one member", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UnregisteredProvider_IsRejectedNamingMemberAndValue()
    {
        var classes = ValidClasses();
        classes[0].Members[0].ProviderKind = "hypervisor-9";

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hypervisor-9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnregisteredProvider_IsRejectedAgainstEmptyRegistry()
    {
        var classes = ValidClasses();

        var ex = AssertBuildFails(classes, registered: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("incus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZeroCapacity_IsRejectedNamingMemberAndValue()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capacity = 0;

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Capacity=0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NegativeCapacity_IsRejected()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capacity = -3;

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Capacity=-3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingCapacity_IsRejectedNamingTheMember()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capacity = null;

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Capacity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DuplicateMemberId_IsRejectedNamingTheMember()
    {
        var classes = ValidClasses();
        classes[0].Members.Add(new SandboxMemberOptions
        {
            MemberId = "LOCAL",
            ProviderKind = "multipass",
            Capacity = 4,
            PreferenceScore = 50,
        });

        var ex = AssertBuildFails(classes);

        Assert.Contains("LOCAL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingPreferenceScore_IsRejectedNamingTheMember()
    {
        var classes = ValidClasses();
        classes[0].Members[0].PreferenceScore = null;

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PreferenceScore", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public void Build_PreferenceScoreOutOfRange_IsRejectedNamingMemberAndValue(int score)
    {
        var classes = ValidClasses();
        classes[0].Members[0].PreferenceScore = score;

        var ex = AssertBuildFails(classes);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"PreferenceScore={score}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CapacityTwoWithOneWorker_Passes()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capacity = 2;

        var catalog = Build(classes, workers: 1);

        Assert.Equal(2, Assert.Single(Assert.Single(catalog).Members).Capacity);
    }

    [Fact]
    public void Build_CapacityTwoWithTwoWorkers_IsRejectedStatingTheMinimum()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capacity = 2;

        var ex = AssertBuildFails(classes, workers: 2);

        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Capacity=2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 worker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WorkerCountBelowOne_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(ValidClasses(), workers: 0));

        Assert.Contains("MaxConcurrentWorkers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ValidClass_ProjectsToPlacementMembersPreservingEveryAttribute()
    {
        var catalog = Build(ValidClasses(), workers: 2);

        var cls = Assert.Single(catalog);
        Assert.Equal("default", cls.Id);
        Assert.Equal("Default", cls.DisplayName);
        var member = Assert.Single(cls.Members);
        var placement = member.ToPlacementMember();
        Assert.Equal("local", placement.MemberId);
        Assert.Equal(4, placement.MaxConcurrentSandboxes);
        Assert.Equal(new[] { "default" }, placement.NetworkProfiles);
        Assert.Equal(new[] { "codex" }, placement.Credentials);
        Assert.Equal(new[] { "baseline-bake" }, placement.Capabilities);
        Assert.True(placement.Healthy);
        Assert.False(placement.Cordoned);
    }

    [Fact]
    public void Build_NormalizesProviderKindAndTrimsMemberId()
    {
        var classes = ValidClasses();
        classes[0].Members[0].MemberId = "  local  ";
        classes[0].Members[0].ProviderKind = "  Incus  ";

        var catalog = Build(classes);

        var member = Assert.Single(Assert.Single(catalog).Members);
        Assert.Equal("local", member.MemberId);
        Assert.Equal("incus", member.ProviderKind);
    }

    [Fact]
    public void Build_TrimsAndDedupesTags()
    {
        var classes = ValidClasses();
        classes[0].Members[0].Capabilities = new() { "  Baseline-Bake  ", "baseline-bake", "", "  " };
        classes[0].Members[0].NetworkProfiles = new() { " lan ", "lan" };
        classes[0].Members[0].Credentials = new() { " codex ", "codex" };

        var catalog = Build(classes);

        var member = Assert.Single(Assert.Single(catalog).Members);
        Assert.Equal(new[] { "Baseline-Bake" }, member.Capabilities);
        Assert.Equal(new[] { "lan" }, member.NetworkProfiles);
        Assert.Equal(new[] { "codex" }, member.Credentials);
    }

    [Fact]
    public void Build_PreferenceScoreOrdersWithoutHardcodedRatio()
    {
        var classes = ValidClasses();
        classes[0].Members.Add(new SandboxMemberOptions
        {
            MemberId = "remote",
            ProviderKind = "multipass",
            Capacity = 4,
            PreferenceScore = 10,
        });

        var catalog = Build(classes);

        var ordered = Assert.Single(catalog).Members.OrderByDescending(m => m.PreferenceScore).ToList();
        Assert.Equal("local", ordered[0].MemberId);
        Assert.Equal("remote", ordered[1].MemberId);
    }

    [Theory]
    [MemberData(nameof(FingerprintMutations))]
    public void SerializeSandboxClasses_ObservesField(Func<List<SandboxClassOptions>> mutate)
    {
        var baseline = AgentConfigHotReload.SerializeSandboxClasses(ValidClasses());
        var mutated = AgentConfigHotReload.SerializeSandboxClasses(mutate());

        Assert.NotEqual(baseline, mutated);
    }

    public static TheoryData<Func<List<SandboxClassOptions>>> FingerprintMutations()
    {
        List<SandboxClassOptions> WithMember(Action<SandboxMemberOptions> edit)
        {
            var classes = ValidClasses();
            edit(classes[0].Members[0]);
            return classes;
        }

        List<SandboxClassOptions> WithClass(Action<SandboxClassOptions> edit)
        {
            var classes = ValidClasses();
            edit(classes[0]);
            return classes;
        }

        return new TheoryData<Func<List<SandboxClassOptions>>>
        {
            () => WithClass(c => c.Id = "other"),
            () => WithClass(c => c.DisplayName = "Other"),
            () => WithMember(m => m.MemberId = "other"),
            () => WithMember(m => m.ProviderKind = "multipass"),
            () => WithMember(m => m.HostId = "host-b"),
            () => WithMember(m => m.Capacity = 8),
            () => WithMember(m => m.Capabilities = new() { "other-tag" }),
            () => WithMember(m => m.NetworkProfiles = new() { "other-profile" }),
            () => WithMember(m => m.Credentials = new() { "other-cred" }),
            () => WithMember(m => m.PreferenceScore = 1),
        };
    }
}
