using System.Net;

namespace Tripo.Mcp;

public class TripoApiException : Exception
{
    public TripoApiException(
        string message,
        HttpStatusCode? statusCode = null,
        int? apiCode = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(
            RemoteText.Bound(message, 1024, "The Tripo request failed."),
            innerException)
    {
        StatusCode = statusCode;
        ApiCode = apiCode;
        RequestId = string.IsNullOrWhiteSpace(requestId)
            ? null
            : RemoteText.Bound(requestId, 128);
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }

    public int? ApiCode { get; }

    public string? RequestId { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsRetryableReadFailure =>
        InnerException is HttpRequestException or
            IOException or
            OperationCanceledException ||
        StatusCode == HttpStatusCode.RequestTimeout ||
        StatusCode == HttpStatusCode.TooManyRequests ||
        (StatusCode is not null && (int)StatusCode.Value >= 500);
}

public sealed class TripoCredentialException : TripoApiException
{
    public TripoCredentialException(string message)
        : base(message)
    {
    }
}

public sealed class TripoPaidRequestRejectedException : TripoApiException
{
    internal TripoPaidRequestRejectedException(TripoApiException rejection)
        : base(
            rejection.Message,
            rejection.StatusCode,
            rejection.ApiCode,
            rejection.RequestId,
            rejection.RetryAfter,
            rejection)
    {
    }
}
