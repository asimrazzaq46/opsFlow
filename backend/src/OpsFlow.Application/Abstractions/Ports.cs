namespace OpsFlow.Application.Abstractions;

public interface ICurrentUser
{
    /// <summary>Id of the authenticated user; throws an unauthorized AppException otherwise.</summary>
    Guid UserId { get; }

    /// <summary>Id of the session the current access token belongs to.</summary>
    Guid SessionId { get; }
}

public sealed record UserSummary(Guid Id, string Email, string DisplayName, bool EmailConfirmed);

public interface IUserDirectory
{
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken
    );
}

public interface IEmailService
{
    Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Starts a best-effort delivery without holding the caller: a failure is logged and not retried. For mails that carry a one-time
    /// secret, which must never be persisted for a later retry, and whose request must not reveal through its duration whether a
    /// message was sent. Delivery that has to be retried goes through the outbox and <see cref="SendAsync"/>.
    /// </summary>
    void SendDetached(string recipient, string subject, string body);
}

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public frontend origin used to build links in emails.</summary>
    public string BaseUrl { get; set; } = "http://localhost:4200";
}
