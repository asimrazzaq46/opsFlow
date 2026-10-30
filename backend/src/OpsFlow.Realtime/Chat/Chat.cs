using System.Security.Claims;

namespace OpsFlow.Realtime.Chat;

/// <summary>
/// The authenticated caller. It can only be created from an authenticated <see cref="ClaimsPrincipal"/> or explicitly by the host from its own
/// authenticated context - there is deliberately no constructor that accepts a request/body value, and chat requests carry no sender field.
/// </summary>
public sealed class ChatUser
{
    private ChatUser(string id) => Id = id;

    public string Id { get; }

    public static ChatUser FromPrincipal(ClaimsPrincipal principal, string claimType = "sub")
    {
        ArgumentNullException.ThrowIfNull(principal);
        return
            principal.Identity?.IsAuthenticated == true
            && principal.FindFirst(claimType)?.Value is { Length: > 0 } id
            ? new ChatUser(id)
            : throw new ChatException(
                ChatFailure.Unauthenticated,
                "An authenticated user is required."
            );
    }

    /// <summary>For hosts with their own auth abstraction (e.g. an <c>ICurrentUser</c>): pass ONLY an id taken from the authenticated context.</summary>
    public static ChatUser FromAuthenticatedHostContext(string authenticatedUserId) =>
        string.IsNullOrWhiteSpace(authenticatedUserId)
            ? throw new ChatException(
                ChatFailure.Unauthenticated,
                "An authenticated user is required."
            )
            : new ChatUser(authenticatedUserId);
}

/// <summary>A simple committed message for hosts that do not need their own message type.</summary>
public sealed record ChatMessage(
    string Id,
    string ConversationId,
    string SenderUserId,
    string Content,
    DateTime CreatedAtUtc,
    long Sequence
);

[RealtimeEvent("chat.message.created")]
public sealed record ChatMessageCreated(ChatMessage Message);

public sealed record ChatSendRequest(
    ChatUser Sender,
    string ConversationId,
    string ClientMessageId,
    string Content
);

/// <summary>One audience of a committed message: who receives it and the host-defined, already visibility-filtered event (it carries <c>[RealtimeEvent]</c>).</summary>
public sealed record ChatDelivery(IReadOnlyCollection<string> UserIds, object Event);

/// <summary>
/// Outcome of persisting a message. <c>Message</c> is what the host returns to the sender; <c>Deliveries</c> are the per-audience events to push
/// (empty for a duplicate). Recipient-specific visibility lives in the host: it builds one delivery per audience.
/// </summary>
public sealed record ChatAppendResult<TMessage>(
    TMessage Message,
    bool Duplicate,
    IReadOnlyList<ChatDelivery> Deliveries
);

/// <summary>Decides from CURRENT server-side state whether the user may read/post in a conversation. Client-supplied claims are never enough.</summary>
public interface IChatConversationAuthorizer
{
    ValueTask<bool> IsAllowedAsync(
        ChatUser user,
        string conversationId,
        ChatOperation operation,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Host persistence. <see cref="AppendAsync"/> must commit BEFORE returning (everything the host wants atomic - notifications, counters -
/// belongs in that same commit), be idempotent on <c>ClientMessageId</c> and report who may receive the message at commit time.
/// </summary>
public interface IChatMessageStore<TMessage>
{
    Task<ChatAppendResult<TMessage>> AppendAsync(
        ChatSendRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Optional history access (the host may also expose its own paged endpoint instead).</summary>
public interface IChatHistoryReader<TMessage>
{
    /// <summary>Newest-first page (deterministic order by sequence).</summary>
    Task<IReadOnlyList<TMessage>> GetHistoryAsync(
        ChatUser user,
        string conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken
    );
}

public interface IChatMessageValidator
{
    /// <summary>Returns an error text, or null when the content is acceptable.</summary>
    string? Validate(string? content);
}

public sealed class DefaultChatMessageValidator(int maxLength = 4000) : IChatMessageValidator
{
    public string? Validate(string? content) =>
        string.IsNullOrWhiteSpace(content) ? "Message content is required."
        : content.Length > maxLength ? $"Message content is limited to {maxLength} characters."
        : null;
}

public sealed class ChatException(ChatFailure failure, string message) : Exception(message)
{
    public ChatFailure Failure { get; } = failure;
}
