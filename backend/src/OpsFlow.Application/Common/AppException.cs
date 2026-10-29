namespace OpsFlow.Application.Common;

/// <summary>Expected business failure that the API maps to a ProblemDetails response.</summary>
public sealed class AppException(int statusCode, string title, string? detail = null, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    /// <summary>Field-level validation errors; when present the API answers with a ValidationProblemDetails.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static AppException BadRequest(string title, string? detail = null) => new(400, title, detail);
    public static AppException Validation(string field, params string[] messages) =>
        new(400, "One or more validation errors occurred.", null, new Dictionary<string, string[]> { [field] = messages });
    public static AppException Unauthorized(string title) => new(401, title);
    public static AppException Forbidden(string title, string? detail = null) => new(403, title, detail);
    public static AppException NotFound(string title) => new(404, title);
    public static AppException Conflict(string title, string? detail = null) => new(409, title, detail);
}
