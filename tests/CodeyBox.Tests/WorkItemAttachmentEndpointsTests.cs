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
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task Upload_SavesAttachment_WhenWorkItemIsQueued()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        using var form = new MultipartFormDataContent();
        var captionContent = new StringContent("original specification");
        form.Add(captionContent, "caption");

        var fileBytes = Encoding.UTF8.GetBytes("Hello Attachment");
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        form.Add(fileContent, "file", "spec.txt");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<AttachmentDto>>();
        Assert.NotNull(list);
        var dto = Assert.Single(list);

        Assert.Equal(item.Id.ToString(), dto.WorkItemId);
        Assert.Equal("spec.txt", dto.FileName);
        Assert.Equal("text/plain", dto.ContentType);
        Assert.Equal(fileBytes.Length, dto.SizeBytes);
        Assert.Equal("original specification", dto.Caption);
        Assert.Equal("8b1d21b03fec79fd0386efc586ab0c897c3b6d11962c764d151c5a7bc1990246", dto.Sha256);

        // Verify it was written to both metadata store and blob store
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

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("data"u8.ToArray()), "file", "doc.txt");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("attachments can only be added while the work item is Queued", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_ReturnsPayloadTooLarge_WhenFileExceedsLimit()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // MaxFileSizeBytes is overridden to 100 in our factory settings
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[150]), "file", "large.bin");

        var response = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("exceeds max-file-size", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_ReturnsAllAttachments()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        // Upload first file
        using var form1 = new MultipartFormDataContent();
        form1.Add(new ByteArrayContent("one"u8.ToArray()), "file", "one.txt");
        var r1 = await _client.PostAsync($"/workitems/{item.Id}/attachments", form1);
        r1.EnsureSuccessStatusCode();

        // Upload second file
        using var form2 = new MultipartFormDataContent();
        form2.Add(new ByteArrayContent("two"u8.ToArray()), "file", "two.txt");
        var r2 = await _client.PostAsync($"/workitems/{item.Id}/attachments", form2);
        r2.EnsureSuccessStatusCode();

        // List them
        var response = await _client.GetAsync($"/workitems/{item.Id}/attachments");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<AttachmentDto>>();
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

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("download content"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        form.Add(fileContent, "file", "down.txt");
        var rUpload = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        rUpload.EnsureSuccessStatusCode();
        var dtos = await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>();
        var dto = dtos![0];

        // Download
        var response = await _client.GetAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16L, response.Content.Headers.ContentLength);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("down.txt", response.Content.Headers.ContentDisposition?.FileNameStar);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("download content", body);
    }

    [Fact]
    public async Task Delete_RemovesAttachment_WhenQueued()
    {
        var item = Sample(WorkItemState.Queued);
        await _factory.WorkItemStore.CreateAsync(item);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("delete content"u8.ToArray()), "file", "del.txt");
        var rUpload = await _client.PostAsync($"/workitems/{item.Id}/attachments", form);
        rUpload.EnsureSuccessStatusCode();
        var dtos = await rUpload.Content.ReadFromJsonAsync<List<AttachmentDto>>();
        var dto = dtos![0];

        // Delete
        var response = await _client.DeleteAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify gone
        var rGet = await _client.GetAsync($"/workitems/{item.Id}/attachments/{dto.Id}");
        Assert.Equal(HttpStatusCode.NotFound, rGet.StatusCode);

        Assert.Null(await _factory.AttachmentStore.GetAsync(dto.Id));
        Assert.False(_factory.BlobStore.Exists(dto.Sha256));
    }
}

internal sealed class AttachmentApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-attachment-api-{Guid.NewGuid():N}.db");
    private readonly string _rootDir = Path.Combine(
        Path.GetTempPath(), $"codeybox-attachment-blobs-{Guid.NewGuid():N}");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemAttachmentStore AttachmentStore { get; }
    public HostWorkItemAttachmentBlobStore BlobStore { get; }

    public AttachmentApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        AttachmentStore = new SqliteWorkItemAttachmentStore(_dbPath);
        BlobStore = new HostWorkItemAttachmentBlobStore(() => _rootDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-bsl-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-bsl-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:Attachments:RootDirectory"] = _rootDir,
                ["CodeyBox:Attachments:MaxFileSizeBytes"] = "100", // Low for testing limits
                ["CodeyBox:Attachments:MaxAttachmentsPerWorkItem"] = "3",
                ["CodeyBox:Attachments:MaxTotalBytesPerWorkItem"] = "1000"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            // Stop background services.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<SqliteWorkItemAttachmentStore>();
            services.RemoveAll<IWorkItemAttachmentStore>();
            services.AddSingleton<SqliteWorkItemAttachmentStore>(AttachmentStore);
            services.AddSingleton<IWorkItemAttachmentStore>(AttachmentStore);

            services.RemoveAll<HostWorkItemAttachmentBlobStore>();
            services.RemoveAll<IWorkItemAttachmentBlobStore>();
            services.RemoveAll<IWorkItemAttachmentAdminBlobStore>();
            services.AddSingleton<HostWorkItemAttachmentBlobStore>(BlobStore);
            services.AddSingleton<IWorkItemAttachmentBlobStore>(BlobStore);
            services.AddSingleton<IWorkItemAttachmentAdminBlobStore>(BlobStore);
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
