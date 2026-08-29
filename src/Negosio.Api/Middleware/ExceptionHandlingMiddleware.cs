using System.Diagnostics;
using System.Text.Json;
using Negosio.Api.Contracts;
using Negosio.Application.Common;

namespace Negosio.Api.Middleware;

/// <summary>
/// Centralized exception handling. Translates <see cref="AppException"/>s into the
/// <see cref="ApiError"/> envelope and hides details of unexpected failures.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            _logger.LogWarning(
                "Handled application error {Code} on {Method} {Path}: {Message}",
                ex.Code, context.Request.Method, context.Request.Path, ex.Message);

            await WriteAsync(context, ex.StatusCode, new ApiError
            {
                Code = ex.Code,
                Message = ex.Message,
                TraceId = traceId,
                Errors = ex.Errors
            });
        }
        catch (Exception ex)
        {
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            _logger.LogError(
                ex,
                "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await WriteAsync(context, StatusCodes.Status500InternalServerError, new ApiError
            {
                Code = ErrorCodes.InternalError,
                Message = _environment.IsDevelopment()
                    ? ex.Message
                    : "An unexpected error occurred. Please try again later.",
                TraceId = traceId
            });
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, ApiError error)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}
