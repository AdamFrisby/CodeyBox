using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

internal sealed class ControllableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

internal static class ExternalBuildTestKit
{
    public static ExternalBuildOptions Options(
        ControllableClock? clock = null,
        Action<ExternalBuildOptions>? configure = null)
    {
        var opts = new ExternalBuildOptions
        {
            Enabled = true,
            ApprovedTargets =
            {
                ["fake-target"] = new ExternalBuildTargetApproval
                {
                    ProviderId = "fake-snapshot",
                    TargetId = "fake-target",
                    Configuration = "release",
                    AllowSnapshotUpload = true,
                },
                ["fake-git-target"] = new ExternalBuildTargetApproval
                {
                    ProviderId = "fake-git",
                    TargetId = "fake-git-target",
                    Configuration = "release",
                    AllowGitPublication = true,
                    AllowSnapshotUpload = false,
                },
            },
        };
        configure?.Invoke(opts);
        return opts;
    }

    public static ExternalBuildSourceIdentity Source(string digest = "aabbcc") => new()
    {
        SourceDigestSha256 = new string('a', 64),
        BaseDigestSha256 = new string('b', 64),
        SnapshotId = "snap-test",
        ByteSize = 100,
        FileCount = 2,
    };

    public static ExternalBuildStartRequest Request(
        string target = "fake-target", string attempt = "w1") => new()
        {
            ProjectId = "proj",
            WorkItemId = attempt,
            Phase = "work",
            Iteration = 1,
            Attempt = 1,
            ApprovedTargetName = target,
            Source = Source(),
            IdempotencyKey = "idem-" + attempt + "-" + target,
        };

    public static (ExternalBuildService Service, InMemoryExternalBuildStore Store, ControllableClock Clock, ExternalBuildOptions Opts)
        BuildService(FakeExternalBuildProviderBase provider, Action<ExternalBuildOptions>? configure = null)
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = Options(clock, configure);
        var store = new InMemoryExternalBuildStore();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        return (service, store, clock, opts);
    }
}
