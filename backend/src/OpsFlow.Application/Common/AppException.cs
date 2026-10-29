namespace OpsFlow.Application.Common;

/// <summary>Expected business failure that the API maps to a ProblemDetails response.</summary>
public sealed class AppException(int statusCode, string title, string? detail = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    public static AppException BadRequest(string title, string? detail = null) => new(400, title, detail);
    public static AppException Unauthorized(string title) => new(401, title);
    public static AppException Forbidden(string title, string? detail = null) => new(403, title, detail);
    public static AppException NotFound(string title) => new(404, title);
    public static AppException Conflict(string title, string? detail = null) => new(409, title, detail);
}
