using System.Net;
using System.Text.Json;

namespace FlyBrain.BrainClient;

public sealed class BrainServiceException : Exception
{
    public BrainServiceException(HttpStatusCode statusCode, BrainServiceError error)
        : base(error.Message)
    {
        StatusCode = statusCode;
        Code = error.Code;
        Details = error.Details;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }

    public JsonElement? Details { get; }
}
