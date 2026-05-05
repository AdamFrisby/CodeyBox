using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests verifying that large diffs are truncated at the documented limits.
/// </summary>
public sealed class DiffTruncationTests : IClassFixture<DiffApiFactory>
{
    private readonly DiffApiFactory _factory;

    public DiffTruncationTests(DiffApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDiff_LargerThan1MbJson_IsTruncatedWithMarker()
    {
        var item = MakeItem();
        await _factory.Store.CreateAsync(item);

        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        await CreateBareRepoWithLargeDiffAsync(_factory.GitRootDir, item.Id, "main", workBranch, targetBytes: 2 * 1024 * 1024);

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/diff");
        req.Headers.Accept.ParseAdd("application/json");
        var resp = await client.SendAsync(req);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("truncated").GetBoolean(), "truncated should be true");
        var diff = body.GetProperty("diff").GetString() ?? "";
        Assert.Contains("[... diff truncated at 1 MB", diff);

        // Diff text should not exceed ~1 MB + marker overhead (allow 10 KB slop).
        var diffBytes = System.Text.Encoding.UTF8.GetByteCount(diff);
        Assert.True(diffBytes < 1 * 1024 * 1024 + 10 * 1024,
            $"diff was {diffBytes} bytes — expected ≤ ~1 MB");
    }

    [Fact]
    public async Task GetDiff_Under1MbJson_IsNotTruncated()
    {
        var item = MakeItem();
        await _factory.Store.CreateAsync(item);

        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        // Small diff: well under 1 MB.
        await CreateBareRepoWithLargeDiffAsync(_factory.GitRootDir, item.Id, "main", workBranch, targetBytes: 512);

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/diff");
        req.Headers.Accept.ParseAdd("application/json");
        var resp = await client.SendAsync(req);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("truncated").GetBoolean(), "small diff should not be truncated");
        var diff = body.GetProperty("diff").GetString() ?? "";
        Assert.DoesNotContain("[... diff truncated", diff);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WorkItem MakeItem() => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Truncation Test",
        Prompt = "test",
        BaseBranch = "main",
        WorkBranch = null,
        State = WorkItemState.Working,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    /// <summary>
    /// Creates a bare repo where the work branch adds a file large enough that
    /// the resulting unified diff reaches approximately <paramref name="targetBytes"/> bytes.
    /// </summary>
    private static async Task CreateBareRepoWithLargeDiffAsync(
        string gitRoot, WorkItemId id, string baseBranch, string workBranch, int targetBytes)
    {
        var barePath = Path.Combine(gitRoot, id + ".git");
        var tempWork = Path.Combine(Path.GetTempPath(), $"diff-trunc-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempWork);
            await TestSupport.RunGit(tempWork, "init", "-b", baseBranch);
            await TestSupport.RunGit(tempWork, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(tempWork, "config", "user.name", "Test");

            await File.WriteAllTextAsync(Path.Combine(tempWork, "base.txt"), "base file\n");
            await TestSupport.RunGit(tempWork, "add", "base.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "initial");

            await TestSupport.RunGit(tempWork, "checkout", "-b", workBranch);

            // Write a file whose diff is at least targetBytes.
            // Each line "aaaaa...aaaa\n" (80 chars + newline) contributes roughly the same
            // to the unified diff output (one added line per content line).
            var linesNeeded = Math.Max(1, targetBytes / 81);
            var lineTemplate = new string('a', 79) + "\n";
            var sb = new System.Text.StringBuilder(linesNeeded * 80);
            for (var i = 0; i < linesNeeded; i++) sb.Append(lineTemplate);

            await File.WriteAllTextAsync(Path.Combine(tempWork, "big.txt"), sb.ToString());
            await TestSupport.RunGit(tempWork, "add", "big.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "large file commit");

            await TestSupport.RunGit(Path.GetTempPath(), "clone", "--bare", "--local", tempWork, barePath);
        }
        finally
        {
            Directory.Delete(tempWork, recursive: true);
        }
    }
}
