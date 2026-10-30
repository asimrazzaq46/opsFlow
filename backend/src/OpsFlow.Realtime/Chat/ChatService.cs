using Microsoft.Extensions.Logging;

namespace OpsFlow.Realtime.Chat;

/// <summary>
/// validate -> authorize -> persist (host) -> publish the host's per-audience deliveries. Publishing happens only after the commit and is
/// best effort: a transport failure (logged by the publisher) or a misconfigured delivery event (logged here as an error) never undoes or
/// hides the committed message - clients recover through history endpoints.
/// </summary>
public sealed partial class ChatService<TMessage>(IChatConversationAuthorizer authorizer, IChatMessageStore<TMessage> store, IChatMessageValidator validator, IRealtimePublisher publisher, ILogger<ChatService<TMessage>> logger)
{
    public async Task<ChatAppendResult<TMessage>> SendAsync(ChatUser sender, string conversationId, string clientMessageId, string? content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (validator.Validate(content) is { } error)
        {
            throw new ChatException(ChatFailure.Invalid, error);
        }

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(clientMessageId))
        {
            throw new ChatException(ChatFailure.Invalid, "A conversation id and a client message id are required.");
        }

        if (!await authorizer.IsAllowedAsync(sender, conversationId, ChatOperation.Post, cancellationToken))
        {
            throw new ChatException(ChatFailure.Forbidden, "Not allowed in this conversation.");
        }

        var result = await store.AppendAsync(new ChatSendRequest(sender, conversationId, clientMessageId, content!), cancellationToken);
        if (!result.Duplicate)
        {
            foreach (var delivery in result.Deliveries)
            {
                try
                {
                    await publisher.SendToUsersAsync(delivery.UserIds, delivery.Event, CancellationToken.None);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    LogBadDelivery(logger, ex);
                }
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A committed chat message has an invalid realtime delivery configuration")]
    private static partial void LogBadDelivery(ILogger logger, Exception exception);
}

public sealed class ChatHistoryService<TMessage>(IChatConversationAuthorizer authorizer, IChatHistoryReader<TMessage> reader)
{
    public async Task<IReadOnlyList<TMessage>> GetHistoryAsync(ChatUser user, string conversationId, long? beforeSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await authorizer.IsAllowedAsync(user, conversationId, ChatOperation.Read, cancellationToken)
            ? await reader.GetHistoryAsync(user, conversationId, beforeSequence, Math.Clamp(limit, 1, 100), cancellationToken)
            : throw new ChatException(ChatFailure.Forbidden, "Not allowed in this conversation.");
    }
}
