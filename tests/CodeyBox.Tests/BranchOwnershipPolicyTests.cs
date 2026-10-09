using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure ownership proof over commit messages: a remote tip counts as
/// CodeyBox's own history only when every exclusive commit carries the
/// configured trailers in its trailer block. Each test pins a value the
/// policy produced so a single-statement production mutation flips it red.
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
    public void Commit_MissingOneTrailer_IsNotOwned()
    {
        const string message =
            "agent work\n\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>";

        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(message, Keys));
    }

    [Fact]
    public void Commit_ForeignCoAuthorValue_IsNotOwned()
    {
        const string message =
            "third party change\n\n" +
            "CodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
            "Co-Authored-By: Mallory <mallory@example.invalid>";

        Assert.False(BranchOwnershipPolicy.IsOwnedCommit(message, Keys));
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
}
