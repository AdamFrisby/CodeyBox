using System.Net;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>Service-side failure signalled by the E2B API, the envd gateway, or the transport.</summary>
public sealed class E2bApiException : Exception
{
    public E2bApiException(HttpStatusCode? statusCode, string errorClass, string detail)
        : base($"e2b api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}")
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public E2bApiException(HttpStatusCode? statusCode, string errorClass, string detail, Exception inner)
        : base($"e2b api failure: status={(statusCode.HasValue ? (int)statusCode.Value : "transport")} errorClass={errorClass} detail={detail}", inner)
    {
        StatusCode = statusCode;
        ErrorClass = errorClass;
        Detail = detail;
    }

    public HttpStatusCode? StatusCode { get; }

    public string ErrorClass { get; }

    public string Detail { get; }
}
