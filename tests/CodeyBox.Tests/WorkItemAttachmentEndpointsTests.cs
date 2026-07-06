using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Api;

namespace CodeyBox.Tests;

public sealed class WorkItemAttachmentEndpointsTests : IDisposable
{
    private readonly AttachmentApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemAttachmentEndpointsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem Sample(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-proj"),
        Title = "test item",
        Prompt = "do stuff",
        Agent = AgentKind.Claude,
        State = state,
    };

    private static MultipartFormDataContent FormWithFile(byte[] bytes, string filename, string? contentType = null, string? caption = null)
    {
        var form = new MultipartFormDataContent();
        if (caption is not null)
            form.Add(new StringContent(caption), "caption");
        var fileContent = new ByteArrayContent(bytes);
        if (contentType is not null)
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", filename);
        return form;
    }

    [Fact]
    public async Task Upload_SavesAttachment_WhenWorkItemIsQueued()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("Hello Attachment"u8.ToArray(), "spec.txt", "text/plain", "original specification"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var list = (await response.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        Assert.NotNull(list);
        var dto = Assert.Single(list);

        Assert.Equal(item.Id.ToString(), dto.WorkItemId);
        Assert.Equal("spec.txt", dto.FileName);
        Assert.Equal("text/plain", dto.ContentType);
        Assert.Equal("Hello Attachment"u8.Length, dto.SizeBytes);
        Assert.Equal("original specification", dto.Caption);
        Assert.Equal("8b1d21b03fec79fd0386efc586ab0c897c3b6d11962c764d151c5a7bc1990246", dto.Sha256);

        var record = await _factory.AttachmentStore.GetAsync(dto.Id);
        Assert.NotNull(record);
        Assert.Equal("spec.txt", record.FileName);

        Assert.True(_factory.BlobStore.Exists(dto.Sha256));
        using var stream = _factory.BlobStore.OpenRead(dto.Sha256);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal("Hello Attachment", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Upload_ReturnsConflict_WhenWorkItemIsNotQueued()
    {
        var item = Sample(WorkItemState.Working);
        await _factory.WorkItemStore.CreateAsync(item);

        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("data"u8.ToArray(), "doc.txt"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("attachments can only be added while the work item is Queued", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_ReturnsPayloadTooLarge_WhenFileExceedsLimit()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // Default factory MaxFileSizeBytes is 1024.
        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile(new byte[1500], "large.bin"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("exceeds max-file-size", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_ReturnsConflict_WhenAttachmentCountCapExceeded()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // Factory caps MaxAttachmentsPerWorkItem at 3
        for (var i = 0; i < 3; i++)
        {
            using var r = await _client.PostAsync(
                $"/workitems/{item.Id}/attachments",
                FormWithFile(Encoding.UTF8.GetBytes($"file{i}"), $"f{i}.txt"));
            r.EnsureSuccessStatusCode();
        }

        // 4th upload trips the count cap → 409 (count limit, not size limit).
        // The pre-loop check fires because the item already has 3 attachments.
        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("overflow"u8.ToArray(), "f3.txt"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already has", err.GetProperty("error").GetString());

        // The 4th upload's blob is NOT committed (no metadata row). The staged
        // blob may linger on disk as an orphan until the sweep reclaims it;
        // that's by design — verify no extra metadata row was written.
        var list = await _factory.AttachmentStore.ListForWorkItemAsync(item.Id);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task Upload_ReturnsPayloadTooLarge_WhenTotalBytesCapExceeded()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // Factory caps MaxTotalBytesPerWorkItem at 1000 and MaxAttachmentsPerWorkItem
        // at 3. Upload three 400-byte files: the first two land (800 bytes
        // committed), the third would push the total to 1200 > 1000 → 413.
        var payload = new byte[400];
        for (var i = 0; i < 2; i++)
        {
            payload[0] = (byte)('a' + i);
            using var r = await _client.PostAsync(
                $"/workitems/{item.Id}/attachments",
                FormWithFile(payload, $"f{i}.txt"));
            r.EnsureSuccessStatusCode();
        }

        payload[0] = (byte)'z';
        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile(payload, "overflow.txt"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("per-work-item", err.GetProperty("error").GetString());

        // Only the first two committed.
        var list = await _factory.AttachmentStore.ListForWorkItemAsync(item.Id);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Upload_NeutralizesPathTraversalFilename()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // "../../etc/passwd" is stripped to its basename "passwd" by the
        // sanitizer — the path components are neutralised, not rejected.
        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("evil"u8.ToArray(), "../../etc/passwd"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        var dto = Assert.Single(list);
        Assert.Equal("passwd", dto.FileName);
    }

    [Fact]
    public async Task Upload_NeutralizesBackslashPathTraversalFilename()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("evil"u8.ToArray(), @"..\..\etc\passwd"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        var dto = Assert.Single(list);
        Assert.Equal("passwd", dto.FileName);
    }

    [Fact]
    public async Task Upload_RejectsOversizeCaption()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(new string('x', 2001)), "caption");
        form.Add(new ByteArrayContent("data"u8.ToArray()), "file", "f.txt");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("caption exceeds", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_RejectsDanglingCaptionWithoutFollowingFile()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("orphan caption"), "caption");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsUnrecognisedFormField()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("junk"), "unknown_field");
        form.Add(new ByteArrayContent("data"u8.ToArray()), "file", "f.txt");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsZeroByteAttachment()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var response = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile(Array.Empty<byte>(), "empty.txt"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsInvalidWorkItemId()
    {
        var response = await _client.PostAsync(
            $"/workitems/not-a-guid/attachments",
            FormWithFile("data"u8.ToArray(), "f.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_StreamsLargeFileUnderTheCap()
    {
        // Use a factory with high caps so a 256 KiB payload exercises the
        // multi-chunk read + incremental-hash loop (buffer is 81920 bytes)
        // without tripping the per-file or per-item limits.
        using var factory = new AttachmentApiFactory(opts =>
        {
            opts.MaxFileSizeBytes = 1024 * 1024;       // 1 MiB
            opts.MaxAttachmentsPerWorkItem = 10;
            opts.MaxTotalBytesPerWorkItem = 10 * 1024 * 1024; // 10 MiB
        });
        using var client = factory.CreateClient();
        var item = Sample(WorkItemState.Queued);
        await factory.WorkItemStore.CreateAsync(item);

        var payload = new byte[256 * 1024];
        new Random(42).NextBytes(payload);
        var expectedHash = HexSha256(payload);

        var response = await client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile(payload, "large.bin", "application/octet-stream"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        var dto = Assert.Single(list);
        Assert.Equal(payload.Length, dto.SizeBytes);
        Assert.Equal(expectedHash, dto.Sha256);

        // Round-trip the bytes
        using var getStream = await client.GetStreamAsync(
            $"/workitems/{item.Id}/attachments/{dto.Id}");
        using var ms = new MemoryStream();
        await getStream.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task List_ReturnsAllAttachments()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        using (var r1 = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("one"u8.ToArray(), "one.txt")))
            r1.EnsureSuccessStatusCode();
        using (var r2 = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("two"u8.ToArray(), "two.txt")))
            r2.EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/workitems/{item.Id}/attachments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = (await response.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.FileName == "one.txt");
        Assert.Contains(list, a => a.FileName == "two.txt");
    }

    [Fact]
    public async Task Download_ReturnsFileStream_WithCorrectHeaders()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var rUpload = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("download content"u8.ToArray(), "down.txt", "text/plain"));
        rUpload.EnsureSuccessStatusCode();
        var dtos = (await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>())!;
        var dto = dtos[0];

        var response = await _client.GetAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16L, response.Content.Headers.ContentLength);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("down.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("nosniff", response.Headers.TryGetValues("X-Content-Type-Options", out var v) ? v.First() : null);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("download content", body);
    }

    [Fact]
    public async Task Download_Returns404_WhenAttachmentBelongsToDifferentWorkItem()
    {
        var itemA = Sample(WorkItemState.Queued);
        var itemB = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(itemA);
        await _factory.WorkItemStore.CreateAsync(itemB);

        var rUpload = await _client.PostAsync(
            $"/workitems/{itemA.Id}/attachments",
            FormWithFile("secret"u8.ToArray(), "secret.txt"));
        rUpload.EnsureSuccessStatusCode();
        var dto = (await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        // Probe item B with item A's attachment id → 404 (not 200, not 500)
        var response = await _client.GetAsync($"/workitems/{itemB.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesMetadata_WhenQueued()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var rUpload = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("delete content"u8.ToArray(), "del.txt"));
        rUpload.EnsureSuccessStatusCode();
        var dto = (await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        var response = await _client.DeleteAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Metadata gone
        var rGet = await _client.GetAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NotFound, rGet.StatusCode);
        Assert.Null(await _factory.AttachmentStore.GetAsync(dto.Id));
    }

    [Fact]
    public async Task Delete_DeletesBlobOnceUnreferenced_AfterOrphanSweep()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var rUpload = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("delete content"u8.ToArray(), "del.txt"));
        rUpload.EnsureSuccessStatusCode();
        var dto = (await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        var response = await _client.DeleteAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Blob lingers (deferred to sweep). Verify it is gone after the sweep
        // runs with a zero grace window.
        Assert.True(_factory.BlobStore.Exists(dto.Sha256)); // not yet swept
        var blobsAdmin = (IWorkItemAttachmentBlobStoreAdmin)_factory.BlobStore;
        var store = _factory.AttachmentStore;
        var svc = new AttachmentCleanupService(
            store, blobsAdmin,
            () => new AttachmentsOptions { OrphanGracePeriod = TimeSpan.Zero, OrphanSweepInterval = TimeSpan.Zero },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AttachmentCleanupService>.Instance,
            new ManualTimeProvider());
        await svc.RunOrphanSweepAsync(
            new AttachmentsOptions { OrphanGracePeriod = TimeSpan.Zero }, CancellationToken.None);
        Assert.False(_factory.BlobStore.Exists(dto.Sha256));
    }

    [Fact]
    public async Task Delete_DoesNotBreakPeerAttachment_WhenBlobsAreDeduplicated()
    {
        var itemA = Sample(WorkItemState.Queued);
        var itemB = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(itemA);
        await _factory.WorkItemStore.CreateAsync(itemB);

        var bytes = "shared content"u8.ToArray();
        var expectedHash = HexSha256(bytes);

        // Upload the same bytes to two work items → two metadata rows, one
        // on-disk blob (dedup).
        var rA = await _client.PostAsync(
            $"/workitems/{itemA.Id}/attachments",
            FormWithFile(bytes, "a.txt"));
        rA.EnsureSuccessStatusCode();
        var dtoA = (await rA.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        var rB = await _client.PostAsync(
            $"/workitems/{itemB.Id}/attachments",
            FormWithFile(bytes, "b.txt"));
        rB.EnsureSuccessStatusCode();
        var dtoB = (await rB.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        Assert.Equal(expectedHash, dtoA.Sha256);
        Assert.Equal(expectedHash, dtoB.Sha256);
        Assert.True(_factory.BlobStore.Exists(expectedHash));

        // Delete A's attachment — the blob must survive because B still
        // references it.
        var del = await _client.DeleteAsync($"/workitems/{itemA.Id}/attachments/{dtoA.Id}");
        del.EnsureSuccessStatusCode();
        Assert.True(_factory.BlobStore.Exists(expectedHash));

        // B's download still round-trips the bytes.
        var get = await _client.GetAsync($"/workitems/{itemB.Id}/attachments/{dtoB.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadAsStringAsync();
        Assert.Equal("shared content", body);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenWorkItemIsNotQueued()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var rUpload = await _client.PostAsync(
            $"/workitems/{item.Id}/attachments",
            FormWithFile("data"u8.ToArray(), "f.txt"));
        rUpload.EnsureSuccessStatusCode();
        var dto = (await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>())![0];

        // Transition the item to Working (simulating pickup), then the delete
        // guard must refuse with 409 Conflict.
        await _factory.WorkItemStore.UpdateAsync(item with { State = WorkItemState.Working });

        var response = await _client.DeleteAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The attachment metadata survives — delete was refused.
        Assert.NotNull(await _factory.AttachmentStore.GetAsync(dto.Id));
    }

    private static string HexSha256(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    [Fact]
    public async Task Upload_RejectsNonMultipartContentType()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        var content = new StringContent("not multipart");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal sealed class AttachmentApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-attachment-api-{Guid.NewGuid():N}.db");
    private readonly string _rootDir = Path.Combine(
        Path.GetTempPath(), $"codeybox-attachment-blobs-{Guid.NewGuid():N}");
    private readonly Action<AttachmentsOptions>? _configureAttachments;

    public AttachmentApiFactory(Action<AttachmentsOptions>? configureAttachments = null)
    {
        _configureAttachments = configureAttachments;
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        AttachmentStore = new SqliteWorkItemAttachmentStore(_dbPath);
        BlobStore = new HostWorkItemAttachmentBlobStore(() => _rootDir);
    }

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemAttachmentStore AttachmentStore { get; }
    public HostWorkItemAttachmentBlobStore BlobStore { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            var attachments = new AttachmentsOptions
            {
                RootDirectory = _rootDir,
                MaxFileSizeBytes = 1024, // low per-file cap so limit tests trip it cheaply
                MaxAttachmentsPerWorkItem = 3,
                MaxTotalBytesPerWorkItem = 1000,
            };
            _configureAttachments?.Invoke(attachments);
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-bsl-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-bsl-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:Attachments:RootDirectory"] = attachments.RootDirectory,
                ["CodeyBox:Attachments:MaxFileSizeBytes"] = attachments.MaxFileSizeBytes.ToString(),
                ["CodeyBox:Attachments:MaxAttachmentsPerWorkItem"] = attachments.MaxAttachmentsPerWorkItem.ToString(),
                ["CodeyBox:Attachments:MaxTotalBytesPerWorkItem"] = attachments.MaxTotalBytesPerWorkItem.ToString(),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<SqliteWorkItemAttachmentStore>();
            services.RemoveAll<IWorkItemAttachmentStore>();
            services.AddSingleton<SqliteWorkItemAttachmentStore>(AttachmentStore);
            services.AddSingleton<IWorkItemAttachmentStore>(AttachmentStore);

            services.RemoveAll<HostWorkItemAttachmentBlobStore>();
            services.RemoveAll<IWorkItemAttachmentBlobStore>();
            services.RemoveAll<IWorkItemAttachmentBlobStoreAdmin>();
            services.AddSingleton<HostWorkItemAttachmentBlobStore>(BlobStore);
            services.AddSingleton<IWorkItemAttachmentBlobStore>(BlobStore);
            services.AddSingleton<IWorkItemAttachmentBlobStoreAdmin>(BlobStore);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            AttachmentStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
            try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true); } catch { }
        }
        base.Dispose(disposing);
    }
}
