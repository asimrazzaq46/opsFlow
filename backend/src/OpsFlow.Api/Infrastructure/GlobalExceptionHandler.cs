using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Api.Infrastructure.Errors;

namespace OpsFlow.Api.Infrastructure;

/// <summary>
/// Single place where exceptions become HTTP responses. Only EXPECTED failures are mapped (application exceptions, malformed requests,
/// optimistic-concurrency/deadlock losses); every other exception, including unmapped database errors, is a generic 500 that is logged
/// server-side and never echoed to the client.
/// </summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = Map(httpContext, exception);
        httpContext.Response.StatusCode = problem.Status!.Value;
        return await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    private ProblemDetails Map(HttpContext http, Exception exception)
    {
        switch (exception)
        {
            case AppException app:
                LogHandled(logger, app.StatusCode, app.Title);
                var problem = app.Errors is null
                    ? new ProblemDetails { Status = app.StatusCode, Title = app.Title, Detail = app.Detail }
                    : new ValidationProblemDetails(app.Errors.ToDictionary(e => e.Key, e => e.Value)) { Status = app.StatusCode, Title = app.Title };
                if (app.Code is { } code)
                {
                    problem.Extensions["code"] = code;
                }

                return problem;

            case BadHttpRequestException bad: // malformed JSON, missing body, unreadable parameters
                LogHandled(logger, bad.StatusCode, "Malformed request");
                return new ProblemDetails { Status = bad.StatusCode, Title = "The request is malformed.", Detail = "The request body or parameters could not be read." };

            case DbUpdateConcurrencyException:
            case var _ when http.RequestServices.GetRequiredService<IOpsFlowDbContext>().IsDeadlock(exception): // 40P01 deadlock victim: the other writer won
                LogHandled(logger, StatusCodes.Status409Conflict, "Concurrent modification");
                return new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The resource was changed concurrently.", Detail = "Reload the resource and retry.", Extensions = { ["code"] = "CONCURRENCY_CONFLICT" } };

            default:
                LogUnhandled(logger, exception);
                return new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred." };
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected with {StatusCode}: {Title}")]
    private static partial void LogHandled(ILogger logger, int statusCode, string title);
}
