using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace OpsFlow.Api.Infrastructure.Errors;

/// <summary>One RFC 9457 shape for EVERY error body (handled exceptions, 401/403 challenges, 404 status pages, 429 rate limits): type, title, status, detail, code, traceId.</summary>
internal static class ProblemDetailsSetup
{
    /// <summary>HttpContext.Items key where a component (e.g. the JWT challenge) records a more specific stable code.</summary>
    public const string CodeItem = "opsflow.error-code";

    public static void Configure(ProblemDetailsOptions options) => options.CustomizeProblemDetails = context =>
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;
        problem.Type = "about:blank"; // no per-error documentation page: `code` is the stable discriminator
        problem.Detail ??= DefaultDetail(status);
        if (!problem.Extensions.ContainsKey("code"))
        {
            problem.Extensions["code"] = context.HttpContext.Items[CodeItem] as string ?? DefaultCode(status, problem is HttpValidationProblemDetails);
        }

        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    };

    public static string DefaultCode(int status, bool validation) => status switch
    {
        400 => validation ? "VALIDATION_ERROR" : "BAD_REQUEST",
        401 => "UNAUTHORIZED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        409 => "CONFLICT",
        429 => "RATE_LIMITED",
        >= 500 => "INTERNAL_ERROR",
        _ => "HTTP_" + status,
    };

    // Generic and safe: never echoes exception text, SQL, tokens or paths.
    private static string? DefaultDetail(int status) => status switch
    {
        400 => "The request is invalid. Check the request body and parameters.",
        401 => "A valid Bearer access token is required.",
        403 => "You do not have permission to perform this action.",
        404 => "The requested resource was not found.",
        429 => "Too many requests. Please retry later.",
        >= 500 => "An unexpected error occurred. Please try again later.",
        _ => null,
    };
}
