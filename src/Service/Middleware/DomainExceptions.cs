using System;
using System.Net;

namespace Quaestura.Service.Middleware;

/// <summary>
/// Domain-specific exceptions thrown by services and endpoints. They carry a stable
/// instance-level <c>QUAESTURA_*</c> error code and the intended HTTP status; the ServiceMantle
/// Problem Details boundary registered in <c>ServiceMantleComposition</c> converts them into
/// <c>application/problem+json</c> responses.
/// </summary>
public class DomainException : Exception
{
    public string ErrorCode { get; }
    public HttpStatusCode StatusCode { get; }

    public DomainException(string message, string errorCode, HttpStatusCode statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}

public class EntityNotFoundException : DomainException
{
    public EntityNotFoundException(string resource, string id)
        : base($"{resource} not found", $"QUAESTURA_{resource.ToUpperInvariant()}_NOT_FOUND", HttpStatusCode.NotFound)
    {
    }
}

public class BusinessPreconditionException : DomainException
{
    public BusinessPreconditionException(string message, string errorCode)
        : base(message, errorCode, HttpStatusCode.UnprocessableEntity)
    {
    }
}

/// <summary>
/// Thrown when the authenticated user is not allowed to perform the operation.
/// Maps to HTTP 403.
/// </summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException(string message, string errorCode)
        : base(message, errorCode, HttpStatusCode.Forbidden)
    {
    }
}
