using Microsoft.AspNetCore.Antiforgery;

namespace OpsFlow.Api.Infrastructure;

/// <summary>
/// CSRF protection for the cookie-authenticated API: every unsafe /api request must carry the X-XSRF-TOKEN header
/// obtained from GET /api/v1/auth/csrf (Angular HttpClient sends it automatically from the XSRF-TOKEN cookie).
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = { Status = StatusCodes.Status400BadRequest, Title = "Invalid or missing CSRF token." },
                });
                return;
            }
        }

        await next(context);
    }
}
