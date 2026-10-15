using System.Net;

namespace CSweet.UI.Services;

public sealed class ApiClientException : Exception
{
    public ApiClientException(HttpStatusCode statusCode, string message, string? errorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
}
