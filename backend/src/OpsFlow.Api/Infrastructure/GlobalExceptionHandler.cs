using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Common;

namespace OpsFlow.Api.Infrastructure;

internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // UPDATED: AppException ab expected business errors ke liye ProblemDetails status/title deta hai
        ProblemDetails problem;
        if (exception is AppException app)
        {
            LogHandled(logger, app.StatusCode, app.Title);
            problem = new ProblemDetails { Status = app.StatusCode, Title = app.Title, Detail = app.Detail };
        }
        else
        {
            LogUnhandled(logger, exception);
            problem = new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred." };
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected with {StatusCode}: {Title}")]
    private static partial void LogHandled(ILogger logger, int statusCode, string title);
}
