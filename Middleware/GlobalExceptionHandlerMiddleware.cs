using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DOSApi.Middleware;

/// <summary>
/// Global exception handling middleware that catches unhandled exceptions,
/// logs them, and returns a standardized JSON error response.
/// </summary>
public class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlerMiddleware> logger)
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
        catch (Exception exception)
        {
            await HandleExceptionAsync(context, exception);
        }
    }

    private Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var errorResponse = new ErrorResponse();

        switch (exception)
        {
            case ArgumentNullException or ArgumentException:
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                errorResponse.StatusCode = context.Response.StatusCode;
                errorResponse.Message = "Invalid request parameters.";
                errorResponse.Details = exception.Message;
                _logger.LogWarning(exception, "Bad request: {Message}", exception.Message);
                break;

            case InvalidOperationException:
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                errorResponse.StatusCode = context.Response.StatusCode;
                errorResponse.Message = "Invalid operation.";
                errorResponse.Details = exception.Message;
                _logger.LogWarning(exception, "Invalid operation: {Message}", exception.Message);
                break;

            case UnauthorizedAccessException:
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                errorResponse.StatusCode = context.Response.StatusCode;
                errorResponse.Message = "Unauthorized access.";
                _logger.LogWarning("Unauthorized access attempt.");
                break;

            case System.Collections.Generic.KeyNotFoundException:
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                errorResponse.StatusCode = context.Response.StatusCode;
                errorResponse.Message = "Resource not found.";
                errorResponse.Details = exception.Message;
                _logger.LogInformation(exception, "Resource not found: {Message}", exception.Message);
                break;

            default:
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                errorResponse.StatusCode = context.Response.StatusCode;
                errorResponse.Message = "An unexpected error occurred.";
                errorResponse.Details = "Please contact support if the problem persists.";
                _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);
                break;
        }

        errorResponse.Timestamp = DateTime.UtcNow;

        return context.Response.WriteAsJsonAsync(errorResponse);
    }
}

/// <summary>
/// Standard error response format returned by the global exception handler.
/// </summary>
public class ErrorResponse
{
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
}