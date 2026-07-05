using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.QuestionBank.Service.Middleware;

/// <summary>
/// Domain-specific exceptions thrown by services and endpoints.
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
        : base($"{resource} not found", $"QUESTIONBANK_{resource.ToUpperInvariant()}_NOT_FOUND", HttpStatusCode.NotFound)
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

/// <summary>
/// Global exception handling middleware. Converts known domain exceptions to structured
/// JSON error responses and masks all unhandled exceptions as 500.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (EntityNotFoundException ex)
        {
            await WriteError(context, ex.StatusCode, ex.Message, ex.ErrorCode);
        }
        catch (BusinessPreconditionException ex)
        {
            await WriteError(context, ex.StatusCode, ex.Message, ex.ErrorCode);
        }
        catch (ValidationException ex)
        {
            var detail = string.Join("; ", ex.Errors.Select(e => e.ErrorMessage));
            await WriteError(context, HttpStatusCode.BadRequest, detail, "QUESTIONBANK_VALIDATION_FAILED");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Image", StringComparison.OrdinalIgnoreCase))
        {
            await WriteError(context, HttpStatusCode.BadRequest, ex.Message, "QUESTIONBANK_VALIDATION_INVALID_IMAGE");
        }
        catch (DomainException ex)
        {
            await WriteError(context, ex.StatusCode, ex.Message, ex.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Path}", context.Request.Path);
            await WriteError(context, HttpStatusCode.InternalServerError, "Internal server error", "QUESTIONBANK_INTERNAL_ERROR");
        }
    }

    private static async Task WriteError(HttpContext context, HttpStatusCode statusCode, string message, string errorCode)
    {
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            success = false,
            message,
            errorCode,
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
    }
}
