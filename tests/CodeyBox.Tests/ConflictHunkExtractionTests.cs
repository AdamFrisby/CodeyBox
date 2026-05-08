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
            [new ConflictHunk("file.txt", 2, 6), new ConflictHunk("file.txt", 8, 12)],
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
            [new ConflictHunk("adjacent.txt", 1, 3), new ConflictHunk("adjacent.txt", 4, 8)],
            hunks);
    }
}
