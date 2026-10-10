using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure ownership proof over commit messages: a remote tip counts as
/// CodeyBox's own history only when every exclusive commit carries ANY
/// accepted CodeyBox trailer in its trailer block. Each test pins a value
/// the policy produced so a single-statement production mutation flips it red.
/// </summary>
public sealed class BranchOwnershipPolicyTests
{
    private static readonly string[] Keys = ["CodeyBox-WorkItem", "Co-Authored-By"];

    [Fact]
    public void OwnedCommit_WithAllTrailers_IsOwned()
    {
        const string message =
            "agent work\n\nDo the thing.\n\n" +
            "CodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
            "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>";

        Assert.True(BranchOwnershipPolicy.IsOwnedCommit(message, Keys));
    }

    [Fact]
    public void Commit_WithSingleAcceptedTrailer_IsOwned()
    {
        // Writers stamp different sets: agents write Prompt-Revision +
        // Co-Authored-By only, so any single accepted key must suffice.
        Assert.True(BranchOwnershipPolicy.IsOwnedCommit(
            "agent work\n\nCodeyBox-Prompt-Revision: 3\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            ["CodeyBox-WorkItem", "Co-Authored-By", "CodeyBox-Prompt-Revision"]));
        Assert.True(BranchOwnershipPolicy.IsOwnedCommit(
            "agent work\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>",
            Keys));
        Assert.True(BranchOwnershipPolicy.IsOwnedCommit(
            "fixer work\n\nCodeyBox-Mechanical-Fixer: whitespace",
            ["CodeyBox-Mechanical-Fixer"]));
    }

    [Fact]
    public void Commit_ForeignCoAuthorValue_IsNotOwned()
    {
        // A foreign Co-Authored-By line as the commit's only trailer claim
        // proves nothing, even when it names a plausible value shape.
        const string message =
            "third party change\n\n" +
            "Co-Authored-By: Mallory <mallory@example.invalid>";

        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(message, Keys));
    }

    [Fact]
    public void Commit_CoAuthorKeyOrValueMismatch_IsNotOwned()
    {
        // Keys match exactly: a wrong-case key is not this instance's trailer.
        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(
            "work\n\nco-authored-by: CodeyBox <noreply@codeybox.invalid>",
            Keys));
        // Values match exactly: the bare name without the canonical identity is not proof.
        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(
            "work\n\nCo-Authored-By: CodeyBox",
            Keys));
        // A foreign value that merely contains the name is not proof either.
        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(
            "work\n\nCo-Authored-By: Mallory pretending to be CodeyBox <mallory@example.invalid>",
            Keys));
    }

    [Fact]
    public void Commit_TrailerQuotedInBodyMiddle_IsNotOwned()
    {
        const string message =
            "discuss trailers\n\n" +
            "An example footer looks like Co-Authored-By: CodeyBox <noreply@codeybox.invalid>\n" +
            "but this commit carries no trailer block.";

        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(message, Keys));
    }

    [Fact]
    public void Commit_EmptyMessage_IsNotOwned()
    {
        Assert.False(BranchOwnershipPolicy.IsOwnedCommit("", Keys));
        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(null, Keys));
    }

    [Fact]
    public void AllOwned_EmptyRange_IsNotOwnership()
    {
        Assert.False(BranchOwnershipPolicy.AreAllCommitsOwned([], Keys));
    }

    [Fact]
    public void AllOwned_SingleForeignCommit_FailsWholeTip()
    {
        const string owned =
            "agent work\n\nCodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
            "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>";

        Assert.False(BranchOwnershipPolicy.AreAllCommitsOwned([owned, "third party change"], Keys));
        Assert.True(BranchOwnershipPolicy.AreAllCommitsOwned([owned, owned], Keys));
    }

    [Fact]
    public void DefaultKeys_CoverWorkItemAndCoAuthoredBy()
    {
        Assert.Contains("Co-Authored-By", BranchOwnershipPolicy.DefaultRequiredTrailerKeys);
        Assert.Contains(CodeyBoxTrailers.WorkItemTrailerKey, BranchOwnershipPolicy.DefaultRequiredTrailerKeys);

        var options = new UpstreamBranchOwnershipOptions();
        Assert.Equal(
            BranchOwnershipPolicy.DefaultRequiredTrailerKeys,
            options.RequiredTrailerKeys);
        Assert.True(options.MaxCommitsToVerify > 0);
    }

    [Fact]
    public void KnownKeys_CoverEveryTrailerThisInstanceWrites()
    {
        Assert.Contains("Co-Authored-By", BranchOwnershipPolicy.KnownCodeyBoxTrailerKeys);
        Assert.Contains(CodeyBoxTrailers.WorkItemTrailerKey, BranchOwnershipPolicy.KnownCodeyBoxTrailerKeys);
        Assert.Contains(CodeyBoxTrailers.AgentTrailerKey, BranchOwnershipPolicy.KnownCodeyBoxTrailerKeys);
        Assert.Contains(CodeyBoxTrailers.PromptRevisionTrailerKey, BranchOwnershipPolicy.KnownCodeyBoxTrailerKeys);
        Assert.Contains(CodeyBoxTrailers.MechanicalFixerTrailerKey, BranchOwnershipPolicy.KnownCodeyBoxTrailerKeys);
    }

    [Fact]
    public void EffectiveAcceptedKeys_DropsFamiliesDisabledByAttribution()
    {
        string[] configured = ["CodeyBox-WorkItem", "CodeyBox-Agent", "Co-Authored-By"];

        var coAuthorOff = BranchOwnershipPolicy.EffectiveAcceptedKeys(
            configured, new CommitAttribution(includeCoAuthoredBy: false, includeCodeyBoxTrailers: true, includePullRequestFooter: true));
        Assert.Equal(["CodeyBox-WorkItem", "CodeyBox-Agent"], coAuthorOff);

        var trailersOff = BranchOwnershipPolicy.EffectiveAcceptedKeys(
            configured, new CommitAttribution(includeCoAuthoredBy: true, includeCodeyBoxTrailers: false, includePullRequestFooter: true));
        Assert.Equal(["Co-Authored-By"], trailersOff);

        var allOff = BranchOwnershipPolicy.EffectiveAcceptedKeys(
            configured, new CommitAttribution(includeCoAuthoredBy: false, includeCodeyBoxTrailers: false, includePullRequestFooter: true));
        Assert.Empty(allOff);

        var allOn = BranchOwnershipPolicy.EffectiveAcceptedKeys(
            configured, CommitAttribution.Default);
        Assert.Equal(configured, allOn);
    }

    [Fact]
    public void FindUnownedCommitShas_NamesOnlyTrailerlessCommits()
    {
        var owned = new OwnedBranchCommit(
            new string('a', 40),
            "agent work\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>");
        var foreign = new OwnedBranchCommit(new string('b', 40), "third party change");

        var unowned = BranchOwnershipPolicy.FindUnownedCommitShas([owned, foreign], Keys);

        Assert.Equal([new string('b', 40)], unowned);
        Assert.Empty(BranchOwnershipPolicy.FindUnownedCommitShas([owned, owned], Keys));
    }

    [Fact]
    public void DescribeUnownedCommits_NamesShasAndCountsRemainder()
    {
        var shas = Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 40)).ToList();

        var summary = BranchOwnershipPolicy.DescribeUnownedCommits(shas);

        Assert.Contains(shas[0], summary);
        Assert.Contains("(and 2 more)", summary);
        Assert.Equal("unidentified commits", BranchOwnershipPolicy.DescribeUnownedCommits([]));
        Assert.Equal("unidentified commits", BranchOwnershipPolicy.DescribeUnownedCommits(["", "  "]));
    }
}
