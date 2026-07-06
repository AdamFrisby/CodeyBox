using System.Net;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class WorkItemAttachmentEndpoints
{
    /// <summary>
    /// Per-caption upload cap (UTF-16 code units). Streamed-bounded at upload
    /// time so an oversized caption is rejected mid-stream rather than
    /// buffered whole. Intentionally larger than
    /// <see cref="AttachmentManifestPromptPreprocessor.MaxCaptionChars"/>:
    /// the preprocessor re-truncates defensively against pre-existing rows,
    /// so the two constants are coupled by name but not by value. Edit them
    /// together.
    /// </summary>
    private const int MaxCaptionChars = 2_000;

    private const int MaxFileNameChars = 255;
    private const int MaxContentTypeChars = 255;
    private const int StreamBufferSize = 81920;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/workitems/{id}/attachments");
        // Kestrel's default MaxRequestBodySize (30 MiB) would silently cap
        // every upload below the configured AttachmentsOptions.MaxFileSizeBytes
        // (default 100 MiB). The endpoint lifts the server-level limit at
        // entry (before any body read) so the streaming max-file check inside
        // HostWorkItemAttachmentBlobStore.StageAsync is the sole enforcement
        // point — operators tune that knob through config.
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

        // Lift Kestrel's default MaxRequestBodySize (30 MiB) for this endpoint
        // before any body read so the config-driven MaxFileSizeBytes (default
        // 100 MiB) is the real ceiling. Must be set before the body stream is
        // first accessed (HasFormContentType only inspects headers, so it is
        // safe to do this here).
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is not null && !sizeFeature.IsReadOnly)
            sizeFeature.MaxRequestBodySize = null; // unlimited at the server level

        var item = await items.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"attachments can only be added while the work item is Queued; current state is {item.State}" });

        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.HasValue
            || !contentType.MediaType.Value.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "request must be multipart/form-data" });
        }

        var boundary = MultipartRequestHelper.GetBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
            return Results.BadRequest(new { error = "multipart boundary missing" });

        var opts = optsMonitor.CurrentValue.Attachments;
        var (currentCount, currentBytes) = await store.AggregateForWorkItemAsync(workItemId, ct);
        if (currentCount >= opts.MaxAttachmentsPerWorkItem)
            return Conflict(new { error = $"work item already has {currentCount} attachments (limit {opts.MaxAttachmentsPerWorkItem})" });

        var reader = new MultipartReader(boundary, request.Body)
        {
            // Bound the header DoS axes the framework exposes: an attacker
            // cannot ship a section with an unbounded header set. Section
            // BODIES are deliberately left uncapped at the reader level so
            // that an oversized FILE still reaches StageAsync's streaming
            // size check and returns a clean 413 — capping bodies here would
            // turn a legitimate-too-large file into a 400 (malformed multipart)
            // instead. Non-file section bodies are bounded by the caption
            // bounded-read helper, and any other form-data field is rejected
            // outright so its body is never drained for free.
            HeadersCountLimit = 256,
            HeadersLengthLimit = 8 * 1024,
        };

        // Stage every file section first, then commit metadata as a single
        // atomic batch. If a per-item cap is crossed, NO metadata row is
        // written and the staged blobs are left on disk as orphans for the
        // orphan-sweep grace window to reclaim — the reference-count race
        // (delete a blob out from under a concurrent upload's not-yet-written
        // row) cannot happen because we never delete blobs on the upload path.
        var staged = new List<StagedAttachment>();
        long stagedBytes = 0;
        string? pendingCaption = null;

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    return Results.BadRequest(new { error = "malformed content-disposition header" });

                if (MultipartRequestHelper.HasFileContentDisposition(disposition))
                {
                    if (currentCount + staged.Count >= opts.MaxAttachmentsPerWorkItem)
                        return Conflict(new { error = $"max attachments per work item reached ({opts.MaxAttachmentsPerWorkItem})" });

                    // Prefer RFC 5987 filename* (UTF-8, used by modern clients
                    // for non-ASCII names) when the legacy filename field is
                    // absent, so those uploads are not rejected with an empty
                    // filename error.
                    var originalFileName =
                        (disposition.FileNameStar.HasValue
                            ? HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                            : null)
                        ?? (disposition.FileName.HasValue
                            ? HeaderUtilities.RemoveQuotes(disposition.FileName).Value
                            : null)
                        ?? string.Empty;

                    var sanitized = FileNameSanitizer.Sanitize(originalFileName);
                    if (sanitized is null)
                        return Results.BadRequest(new { error = $"filename '{originalFileName}' is invalid (path traversal or empty)" });
                    if (sanitized.Length > MaxFileNameChars)
                        return Results.BadRequest(new { error = $"filename exceeds {MaxFileNameChars} chars" });

                    var declaredType = ValidateContentType(section.ContentType);

                    AttachmentBlobStageResult stage;
                    try
                    {
                        stage = await blobs.StageAsync(section.Body, opts.MaxFileSizeBytes, ct);
                    }
                    catch (AttachmentBlobTooLargeException)
                    {
                        return PayloadTooLarge($"file exceeds max-file-size of {opts.MaxFileSizeBytes} bytes");
                    }

                    if (stage.SizeBytes == 0)
                        return Results.BadRequest(new { error = "zero-byte attachments are not permitted" });

                    if (currentBytes + stagedBytes + stage.SizeBytes > opts.MaxTotalBytesPerWorkItem)
                        return PayloadTooLarge(
                            $"adding this file would exceed the per-work-item total cap of {opts.MaxTotalBytesPerWorkItem} bytes");

                    stagedBytes += stage.SizeBytes;
                    staged.Add(new StagedAttachment(stage, sanitized, declaredType, pendingCaption ?? string.Empty));
                    pendingCaption = null;
                }
                else if (MultipartRequestHelper.HasFormDataContentDisposition(disposition))
                {
                    var name = disposition.Name.HasValue
                        ? HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty
                        : string.Empty;
                    if (string.Equals(name, "caption", StringComparison.OrdinalIgnoreCase))
                    {
                        // Bounded read: stop the moment the cap is crossed so a
                        // multi-GB caption section cannot be buffered whole.
                        var caption = await ReadBoundedStringAsync(section.Body, MaxCaptionChars, ct);
                        if (caption is null)
                            return Results.BadRequest(new { error = $"caption exceeds {MaxCaptionChars} chars" });
                        pendingCaption = caption;
                    }
                    else
                    {
                        // Reject unrecognised form fields rather than silently
                        // draining an unbounded section body.
                        return Results.BadRequest(new { error = $"unrecognised form-data field '{name}'" });
                    }
                }
                else
                {
                    return Results.BadRequest(new { error = "unrecognised multipart section disposition" });
                }
            }
        }
        catch (System.IO.InvalidDataException ex)
        {
            return Results.BadRequest(new { error = $"malformed multipart body: {TruncateMessage(ex.Message)}" });
        }

        if (pendingCaption is not null)
            return Results.BadRequest(new { error = "caption field must be followed by a file field" });
        if (staged.Count == 0)
            return Results.BadRequest(new { error = "no file field found in multipart body" });

        var records = new List<WorkItemAttachmentRecord>(staged.Count);
        var now = DateTimeOffset.UtcNow;
        foreach (var s in staged)
        {
            records.Add(new WorkItemAttachmentRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkItemId = workItemId,
                FileName = s.FileName,
                ContentType = s.ContentType,
                SizeBytes = s.Stage.SizeBytes,
                Sha256 = s.Stage.Sha256,
                Caption = s.Caption,
                CreatedAt = now,
            });
        }

        var committed = await store.CreateBatchIfUnderCapAsync(
            records, opts.MaxAttachmentsPerWorkItem, opts.MaxTotalBytesPerWorkItem, ct);
        if (!committed)
        {
            // No metadata rows were written. The staged blobs are unreferenced
            // and will be reclaimed by the orphan sweep after the grace
            // window — we do NOT delete them here, because a concurrent upload
            // of the same bytes may have just staged a dedup'd copy and be
            // about to write its own row.
            return PayloadTooLarge(
                $"upload would exceed per-work-item caps ({opts.MaxAttachmentsPerWorkItem} attachments or {opts.MaxTotalBytesPerWorkItem} bytes)");
        }

        var dtos = records.Select(ToDto).ToList();
        return Results.Created($"/workitems/{id}/attachments", dtos);
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

    private static async Task<IResult> DownloadAsync(
        string id,
        string attachmentId,
        HttpRequest request,
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentBlobStore blobs,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });

        var record = await store.GetAsync(attachmentId, ct);
        if (record is null || record.WorkItemId != workItemId)
            return Results.NotFound();

        var blob = blobs.OpenRead(record.Sha256);
        if (blob is null)
            return Results.NotFound();

        var contentType = string.IsNullOrWhiteSpace(record.ContentType)
            ? MediaTypeNames.Application.Octet
            : record.ContentType;

        // Defence-in-depth for the stored-Content-Type risk: the operator-
        // supplied MIME was validated at upload, but every download response
        // also carries nosniff + a sandbox CSP so a sniffing client cannot
        // promote a text/plain body to text/html.
        var headers = request.HttpContext.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "sandbox";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        return Results.File(
            blob,
            contentType: contentType,
            fileDownloadName: record.FileName,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        string attachmentId,
        IWorkItemStore items,
        IWorkItemAttachmentStore store,
        CancellationToken ct)
    {
        if (!TryParseWorkItemId(id, out var workItemId))
            return Results.BadRequest(new { error = "invalid work item id" });

        var item = await items.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"attachments can only be deleted while the work item is Queued; current state is {item.State}" });

        // Scope the delete by work item id inside the store so a single SQL
        // round-trip both verifies ownership and removes the row. The on-disk
        // blob is NOT deleted here: a concurrent upload of the same bytes may
        // have staged a dedup'd copy and be about to write its metadata row,
        // and deleting the blob out from under it would orphan that row. The
        // orphan sweep reclaims the blob after the grace window once no row
        // references it.
        var deleted = await store.DeleteAsync(attachmentId, scopeByWorkItemId: workItemId, ct);
        if (deleted is null) return Results.NotFound();
        return Results.NoContent();
    }

    private static IResult PayloadTooLarge(string error) =>
        Results.Json(new { error }, statusCode: (int)HttpStatusCode.RequestEntityTooLarge);

    private static IResult Conflict(object payload) =>
        Results.Json(payload, statusCode: (int)HttpStatusCode.Conflict);

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

    /// <summary>
    /// Validates a client-supplied Content-Type for safe storage and later
    /// echoing into a download response's Content-Type header. Returns the
    /// normalised value, or empty string when the client sent none. Rejects
    /// (by capping and stripping control characters) anything that is not a
    /// parseable media type — the caller can decide to 400 on the length cap
    /// upstream, but a stored value that round-trips through
    /// <see cref="MediaTypeHeaderValue"/> cannot carry header injection
    /// payloads.
    /// </summary>
    private static string ValidateContentType(string? sectionContentType)
    {
        if (string.IsNullOrWhiteSpace(sectionContentType))
            return string.Empty;
        if (sectionContentType.Length > MaxContentTypeChars)
            return string.Empty;
        if (!MediaTypeHeaderValue.TryParse(sectionContentType, out var parsed))
            return string.Empty;
        return parsed.ToString().Trim();
    }

    /// <summary>
    /// Reads up to <paramref name="maxChars"/> UTF-16 code units from
    /// <paramref name="body"/> using a bounded buffer; returns null when the
    /// stream carries more than the cap (so the caller can reject EARLY,
    /// before the whole section is materialised as a string).
    /// </summary>
    private static async Task<string?> ReadBoundedStringAsync(Stream body, int maxChars, CancellationToken ct)
    {
        // UTF-8 worst case is 4 bytes per char, so maxChars chars occupy at
        // most maxChars*4 bytes. Read at most byteCap+1 bytes: if we see the
        // +1 the section is definitely oversized. After decoding, also check
        // the char count — an ASCII-dense section packs maxChars*4 chars into
        // maxChars*4 bytes, which exceeds the char cap even within the byte
        // budget.
        var byteCap = checked(maxChars * 4);
        var buffer = new byte[StreamBufferSize];
        using var collected = new MemoryStream(capacity: Math.Min(byteCap + 1, StreamBufferSize));
        var totalRead = 0;
        while (totalRead < byteCap + 1)
        {
            var toRead = (int)Math.Min(buffer.Length, byteCap + 1 - totalRead);
            var read = await body.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            collected.Write(buffer, 0, read);
            totalRead += read;
        }
        if (totalRead > byteCap)
        {
            // There is at least one byte beyond the cap — the section is
            // oversized. Drain the rest so the next section boundary is still
            // findable, then signal rejection.
            await DrainAsync(body, ct);
            return null;
        }
        var decoded = Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
        if (decoded.Length > maxChars)
            return null; // ASCII-dense overflow within the byte budget
        return decoded;
    }

    private static async Task DrainAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[StreamBufferSize];
        try
        {
            while (await body.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
        }
        catch { /* best-effort drain; the caller is already rejecting */ }
    }

    private static string TruncateMessage(string s) =>
        s.Length <= 240 ? s : s[..240];

    private static AttachmentDto ToDto(WorkItemAttachmentRecord r) => new(
        r.Id,
        r.WorkItemId.ToString(),
        r.FileName,
        r.ContentType,
        r.SizeBytes,
        r.Sha256,
        r.Caption,
        r.CreatedAt);

    private sealed record StagedAttachment(
        AttachmentBlobStageResult Stage,
        string FileName,
        string ContentType,
        string Caption);
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
