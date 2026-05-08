using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class ConflictHunkExtractionTests
{
    [Fact]
    public void ExtractsMultiHunkAndEofRanges()
    {
        var content = """
            one
            <<<<<<< main
            main-a
            =======
            work-a
            >>>>>>> work
            between
            <<<<<<< main
            main-b
            =======
            work-b
            >>>>>>> work
            """;

        var hunks = MergeScopeFence.ExtractConflictHunks("file.txt", content);

        Assert.Equal(
            [new ConflictHunk("file.txt", 2, 2), new ConflictHunk("file.txt", 4, 4)],
            hunks);
    }

    [Fact]
    public void ExtractsAdjacentAndSingleLineHunks()
    {
        var content = """
            <<<<<<< main
            =======
            >>>>>>> work
            <<<<<<< main
            main
            =======
            work
            >>>>>>> work
            tail
            """;

        var hunks = MergeScopeFence.ExtractConflictHunks("adjacent.txt", content);

        Assert.Equal(
            [new ConflictHunk("adjacent.txt", 1, 1), new ConflictHunk("adjacent.txt", 1, 1)],
            hunks);
    }
}
