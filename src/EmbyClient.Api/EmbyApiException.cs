using System.Net;

namespace EmbyClient.Api;

public sealed class EmbyApiException : Exception
{
    public EmbyApiException(HttpStatusCode statusCode, string? applicationErrorCode = null)
        : base($"Emby returned HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ApplicationErrorCode = applicationErrorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ApplicationErrorCode { get; }
    public bool IsAuthenticationFailure => StatusCode == HttpStatusCode.Unauthorized;
    public bool IsPermissionDenied => StatusCode == HttpStatusCode.Forbidden;
}

public sealed class EmbyProtocolException(string message) : Exception(message);

public sealed class EmbyTransportException(string message) : Exception(message);
