using System.Net;

namespace TradingFlow.Etoro.Http;

public sealed class EtoroApiException : Exception
{
    public EtoroApiException(
        HttpStatusCode statusCode,
        string requestPath,
        string responseBody,
        string? requestId,
        Exception? innerException = null)
        : base($"eToro request failed with {(int)statusCode} {statusCode} for {requestPath}. RequestId={requestId}. Body={responseBody}", innerException)
    {
        StatusCode = statusCode;
        RequestPath = requestPath;
        ResponseBody = responseBody;
        RequestId = requestId;
    }

    public HttpStatusCode StatusCode { get; }

    public string RequestPath { get; }

    public string ResponseBody { get; }

    public string? RequestId { get; }
}
