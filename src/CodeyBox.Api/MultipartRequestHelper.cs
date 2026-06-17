using Microsoft.Net.Http.Headers;

namespace CodeyBox.Api;

/// <summary>Tiny helpers for streaming multipart/form-data parsing.</summary>
internal static class MultipartRequestHelper
{
    public static string GetBoundary(MediaTypeHeaderValue contentType)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            throw new InvalidDataException("Missing content-type boundary.");
        return boundary;
    }

    public static bool HasFileContentDisposition(ContentDispositionHeaderValue cd) =>
        cd is not null
        && cd.DispositionType.Equals("form-data")
        && (!string.IsNullOrEmpty(cd.FileName.Value)
            || !string.IsNullOrEmpty(cd.FileNameStar.Value));

    public static bool HasFormDataContentDisposition(ContentDispositionHeaderValue cd) =>
        cd is not null
        && cd.DispositionType.Equals("form-data")
        && string.IsNullOrEmpty(cd.FileName.Value)
        && string.IsNullOrEmpty(cd.FileNameStar.Value);
}
