using System.Net;
using System.Net.Mime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class WorkItemAttachmentEndpoints
{
    private const int MaxCaptionChars = 2_000;
    private const int MaxFileNameChars = 255;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/workitems/{id}/attachments");
        g.MapPost("/", UploadAsync).DisableAntiforgery();
        g.MapGet("/", ListAsync);
        g.MapGet("/{attachmentId}", DownloadAsync);
        g.MapDelete("/{attachmentId}", DeleteAsync);
    }

    private static async Task<IResult> UploadAsync(
        string id,
        HttpRequest request,
        IWorkItemStore items,
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentBlobStore blobs,
        IOptionsMonitor<CodeyBoxOptions> optsMonitor,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });

        var item = await items.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"attachments can only be added while the work item is Queued; current state is {item.State}" });

        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.HasValue
            || !contentType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "request must be multipart/form-data" });
        }

        var boundary = MultipartRequestHelper.GetBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
            return Results.BadRequest(new { error = "multipart boundary missing" });

        var opts = optsMonitor.CurrentValue.Attachments;
        var (currentCount, currentBytes) = await store.AggregateForWorkItemAsync(workItemId, ct);
        if (currentCount >= opts.MaxAttachmentsPerWorkItem)
            return PayloadTooLarge($"work item already has {currentCount} attachments (limit {opts.MaxAttachmentsPerWorkItem})");

        var reader = new MultipartReader(boundary, request.Body);
        var created = new List<AttachmentDto>();
        long runningBytes = currentBytes;
        int runningCount = currentCount;

        MultipartSection? section;
        string? pendingCaption = null;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                continue;

            if (MultipartRequestHelper.HasFileContentDisposition(disposition))
            {
                if (runningCount >= opts.MaxAttachmentsPerWorkItem)
                    return PayloadTooLarge($"max attachments per work item reached ({opts.MaxAttachmentsPerWorkItem})");

                var originalFileName = disposition.FileName.HasValue
                    ? HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? string.Empty
                    : string.Empty;
                var sanitized = FileNameSanitizer.Sanitize(originalFileName);
                if (sanitized is null)
                    return Results.BadRequest(new { error = $"filename '{originalFileName}' is invalid (path traversal or empty)" });
                if (sanitized.Length > MaxFileNameChars)
                    return Results.BadRequest(new { error = $"filename exceeds {MaxFileNameChars} chars" });

                var declaredType = section.ContentType ?? string.Empty;

                AttachmentBlobStageResult stage;
                try
                {
                    stage = await blobs.StageAsync(section.Body, opts.MaxFileSizeBytes, ct);
                }
                catch (AttachmentBlobTooLargeException)
                {
                    return PayloadTooLarge($"file exceeds max-file-size of {opts.MaxFileSizeBytes} bytes");
                }

                if (runningBytes + stage.SizeBytes > opts.MaxTotalBytesPerWorkItem)
                {
                    // Roll back the newly staged blob if nothing else references it,
                    // otherwise the dedupe path means another row still owns it.
                    if (!stage.WasDeduplicated && await store.CountReferencesAsync(stage.Sha256, ct) == 0)
                        blobs.TryDelete(stage.Sha256);
                    return PayloadTooLarge(
                        $"adding this file would exceed the per-work-item total cap of {opts.MaxTotalBytesPerWorkItem} bytes");
                }

                var record = new WorkItemAttachmentRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    WorkItemId = workItemId,
                    FileName = sanitized,
                    ContentType = declaredType,
                    SizeBytes = stage.SizeBytes,
                    Sha256 = stage.Sha256,
                    Caption = pendingCaption ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await store.CreateAsync(record, ct);
                runningBytes += stage.SizeBytes;
                runningCount++;
                pendingCaption = null;
                created.Add(ToDto(record));
            }
            else if (MultipartRequestHelper.HasFormDataContentDisposition(disposition))
            {
                var name = disposition.Name.HasValue
                    ? HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty
                    : string.Empty;
                if (string.Equals(name, "caption", StringComparison.OrdinalIgnoreCase))
                {
                    using var sr = new StreamReader(section.Body);
                    var caption = await sr.ReadToEndAsync(ct);
                    if (caption.Length > MaxCaptionChars)
                        return Results.BadRequest(new { error = $"caption exceeds {MaxCaptionChars} chars" });
                    pendingCaption = caption;
                }
            }
        }

        if (created.Count == 0)
            return Results.BadRequest(new { error = "no file field found in multipart body" });
        return Results.Created($"/workitems/{id}/attachments", created);
    }

    private static async Task<IResult> ListAsync(
        string id,
        IWorkItemAttachmentStore store,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });
        var rows = await store.ListForWorkItemAsync(workItemId, ct);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static IResult DownloadAsync(
        string id,
        string attachmentId,
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentBlobStore blobs,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });
        // Read in the response body — we need to flow the file stream out
        // directly with the right content type / filename.
        return Results.Stream(
            async writer =>
            {
                var record = await store.GetAsync(attachmentId, ct);
                if (record is null || record.WorkItemId != workItemId)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                await using var blob = blobs.OpenRead(record.Sha256);
                if (blob is null)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                ctx.Response.ContentType = string.IsNullOrWhiteSpace(record.ContentType)
                    ? MediaTypeNames.Application.Octet
                    : record.ContentType;
                ctx.Response.ContentLength = record.SizeBytes;
                ctx.Response.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("attachment")
                    {
                        FileNameStar = record.FileName,
                    }.ToString();
                await blob.CopyToAsync(writer, ct);
            });
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        string attachmentId,
        IWorkItemStore items,
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentBlobStore blobs,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });

        var item = await items.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"attachments can only be deleted while the work item is Queued; current state is {item.State}" });

        var existing = await store.GetAsync(attachmentId, ct);
        if (existing is null || existing.WorkItemId != workItemId)
            return Results.NotFound();

        var deleted = await store.DeleteAsync(attachmentId, ct);
        if (deleted is null) return Results.NotFound();

        var refsLeft = await store.CountReferencesAsync(deleted.Sha256, ct);
        if (refsLeft == 0)
            blobs.TryDelete(deleted.Sha256);
        return Results.NoContent();
    }

    private static IResult PayloadTooLarge(string error) =>
        Results.Json(new { error }, statusCode: (int)HttpStatusCode.RequestEntityTooLarge);

    private static bool TryParseWorkItemId(string s, out WorkItemId id)
    {
        if (Guid.TryParse(s, out var g))
        {
            id = new WorkItemId(g);
            return true;
        }
        id = default;
        return false;
    }

    private static AttachmentDto ToDto(WorkItemAttachmentRecord r) => new(
        r.Id,
        r.WorkItemId.ToString(),
        r.FileName,
        r.ContentType,
        r.SizeBytes,
        r.Sha256,
        r.Caption,
        r.CreatedAt);
}

internal sealed record AttachmentDto(
    string Id,
    string WorkItemId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Caption,
    DateTimeOffset CreatedAt);
