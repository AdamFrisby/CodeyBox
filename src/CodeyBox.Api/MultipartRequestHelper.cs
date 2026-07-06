using Microsoft.Net.Http.Headers;

namespace CodeyBox.Api;

/// <summary>Tiny helpers for streaming multipart/form-data parsing.</summary>
internal static class MultipartRequestHelper
{
    /// <summary>
    /// Returns the multipart boundary from the Content-Type header, or null
    /// when the boundary is missing/blank. Returns null (rather than throwing)
    /// so callers can translate the malformed-content-type case into a 400
    /// instead of surfacing as an unhandled 500.
    /// </summary>
    public static string? GetBoundary(MediaTypeHeaderValue contentType)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        return string.IsNullOrWhiteSpace(boundary) ? null : boundary;
    }

    /// <summary>
    /// True when the section carries a file (has either <c>filename</c> or
    /// RFC 5987 <c>filename*</c>). Disposition-type comparison is
    /// case-insensitive per RFC 7578 §4.2.
    /// </summary>
    public static bool HasFileContentDisposition(ContentDispositionHeaderValue cd) =>
        cd is not null
        && cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrEmpty(cd.FileName.Value)
            || !string.IsNullOrEmpty(cd.FileNameStar.Value));

    /// <summary>
    /// True when the section is a non-file form-data field. Case-insensitive
    /// disposition comparison, matching <see cref="HasFileContentDisposition"/>.
    /// </summary>
    public static bool HasFormDataContentDisposition(ContentDispositionHeaderValue cd) =>
        cd is not null
        && cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(cd.FileName.Value)
        && string.IsNullOrEmpty(cd.FileNameStar.Value);
}
