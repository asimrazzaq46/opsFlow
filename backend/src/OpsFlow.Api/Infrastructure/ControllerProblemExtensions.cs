using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Infrastructure.Errors;

namespace OpsFlow.Api.Infrastructure;

internal static class ControllerProblemExtensions
{
    /// <summary>
    /// Answers an application failure (an <see cref="ObjectResult"/> carrying <see cref="ProblemDetails"/>) through the controller's own
    /// <c>Problem</c> / <c>ValidationProblem</c>, so the existing ProblemDetails customization (type, traceId, default detail, code) applies
    /// exactly as it does for every other API error.
    /// </summary>
    public static ActionResult Answer(this ControllerBase controller, ObjectResult failure)
    {
        var problem = (ProblemDetails)failure.Value!;
        var status = failure.StatusCode ?? problem.Status;
        if (problem.Extensions.TryGetValue("code", out var code) && code is string stable)
        {
            controller.HttpContext.Items[ProblemDetailsSetup.CodeItem] = stable;
        }

        if (problem is ValidationProblemDetails validation)
        {
            foreach (var (field, messages) in validation.Errors)
            {
                foreach (var message in messages)
                {
                    controller.ModelState.AddModelError(field, message);
                }
            }

            return controller.ValidationProblem(
                statusCode: status,
                modelStateDictionary: controller.ModelState
            );
        }

        return controller.Problem(detail: problem.Detail, statusCode: status, title: problem.Title);
    }
}
