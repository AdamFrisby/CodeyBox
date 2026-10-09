using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Snapshot handoff: uncommitted capture, excludes, races, bounds, digests.</summary>
public sealed class ExternalBuildSnapshotTests
{
    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Freeze_CapturesTrackedDeletionsModesAndUntracked()
    {
        var snapshot = ExternalBuildSnapshotBuilder.Freeze(
            [("src/app.cs", Bytes("v2"), "100644"), ("run.sh", Bytes("x"), "100755")],
            ["old/file.cs"],
            [("new/notes.md", Bytes("draft"), "100644")]);

        Assert.Equal(3, snapshot.Files.Count);
        Assert.True(snapshot.HasDeletions);
        Assert.Equal(64, snapshot.SourceDigestSha256.Length);
        Assert.Contains(snapshot.Files, f => f.RelativePath == "run.sh" && f.Mode == "100755");
    }

    [Fact]
    public void Freeze_ExcludesCredentialsScratchpadsTranscriptsAndCaches()
    {
        var snapshot = ExternalBuildSnapshotBuilder.Freeze(
            [
                ("src/ok.cs", Bytes("ok"), "100644"),
                (".env", Bytes("SECRET=1"), "100644"),
                ("app/secrets.json", Bytes("{}"), "100644"),
                (".agent-scratch/plan.md", Bytes("private"), "100644"),
                (".transcripts/turn.json", Bytes("{}"), "100644"),
                (".cache/blob.bin", Bytes("c"), "100644"),
            ],
            [],
            []);
        Assert.Single(snapshot.Files);
        Assert.Equal("src/ok.cs", snapshot.Files[0].RelativePath);
    }

    [Fact]
    public void Freeze_ConcurrentEditRace_SurfacesDuplicateError()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExternalBuildSnapshotBuilder.Freeze(
                [("src/a.cs", Bytes("one"), "100644")],
                [],
                [("src/a.cs", Bytes("two"), "100644")]));
    }

    [Fact]
    public void Freeze_PathEscape_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExternalBuildSnapshotBuilder.Freeze(
                [("../../etc/passwd", Bytes("x"), "100644")], [], []));
    }

    [Fact]
    public void Freeze_EnforcesBounds()
    {
        var tiny = new ExternalBuildSnapshotPolicy { MaxTotalBytes = 4, MaxFiles = 100 };
        Assert.Throws<InvalidOperationException>(() =>
            ExternalBuildSnapshotBuilder.Freeze(
                [("a.bin", new byte[8], "100644")], [], [], tiny));

        var fewFiles = new ExternalBuildSnapshotPolicy { MaxFiles = 1 };
        Assert.Throws<InvalidOperationException>(() =>
            ExternalBuildSnapshotBuilder.Freeze(
                [("a.cs", Bytes("a"), "100644"), ("b.cs", Bytes("b"), "100644")], [], [], fewFiles));
    }

    [Fact]
    public void Freeze_DigestChangesAfterEdit_InvalidatesPriorEvidence()
    {
        var before = ExternalBuildSnapshotBuilder.Freeze([("a.cs", Bytes("one"), "100644")], [], []);
        var after = ExternalBuildSnapshotBuilder.Freeze([("a.cs", Bytes("two"), "100644")], [], []);
        Assert.NotEqual(before.SourceDigestSha256, after.SourceDigestSha256);
        Assert.True(ExternalBuildEvidenceEvaluator.IsInvalidatedByEdit(
            before.SourceDigestSha256, after.SourceDigestSha256));
        Assert.False(ExternalBuildEvidenceEvaluator.IsInvalidatedByEdit(
            before.SourceDigestSha256, before.SourceDigestSha256));
    }

    [Fact]
    public void GitPublicationPolicy_ConfinesCandidateRefs()
    {
        var policy = new ExternalBuildGitPublicationPolicy();
        policy.ValidateRef("refs/candidates/w1-abc123");
        Assert.Throws<InvalidOperationException>(() => policy.ValidateRef("refs/heads/work"));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateRef("main"));
    }

    [Fact]
    public void CrossToolchain_Fixture_ProducesDistinctDigests()
    {
        // Neutrality probe: two ecosystems through the same path stay distinct.
        var dotnet = ExternalBuildSnapshotBuilder.Freeze([("app/App.csproj", Bytes("<Project/>"), "100644")], [], []);
        var npm = ExternalBuildSnapshotBuilder.Freeze([("app/package.json", Bytes("{}"), "100644")], [], []);
        var jvm = ExternalBuildSnapshotBuilder.Freeze([("app/build.gradle", Bytes("plugins{}"), "100644")], [], []);
        Assert.NotEqual(dotnet.SourceDigestSha256, npm.SourceDigestSha256);
        Assert.NotEqual(npm.SourceDigestSha256, jvm.SourceDigestSha256);
    }
}
