namespace OpsFlow.Application.Abstractions;

public interface ICurrentUser
{
    /// <summary>Id of the authenticated user; throws an unauthorized AppException otherwise.</summary>
    Guid UserId { get; }
}

public sealed record UserSummary(Guid Id, string Email, string DisplayName, bool EmailConfirmed);

public interface IUserDirectory
{
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public interface IEmailService
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken);
}

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public frontend origin used to build links in emails.</summary>
    public string BaseUrl { get; set; } = "http://localhost:4300";
}
