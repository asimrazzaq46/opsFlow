using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace OpsFlow.Tests.Integration.Support;

public sealed record MessageJson(Guid Id, Guid TicketId, int Sequence, Guid? SenderUserId, string? SenderDisplayName, bool SenderIsStaff, string Content, DateTime CreatedAtUtc);
public sealed record NoteJson(Guid Id, Guid TicketId, int Sequence, Guid AuthorUserId, string AuthorDisplayName, string Content, DateTime CreatedAtUtc);
public sealed record NotificationJson(Guid Id, Guid TenantId, string Type, Guid TicketId, string Subject, string? Detail, DateTime CreatedAtUtc, bool IsRead);
public sealed record AttachmentJson(Guid Id, Guid TicketId, string FileName, string ContentType, long SizeBytes, bool IsInternal, Guid? UploadedByUserId, DateTime CreatedAtUtc);
public sealed record UnreadJson(int Count);

public static class ConversationExtensions
{
    public static async Task<HttpResponseMessage> SendMessageAsync(this TicketWorld w, ApiClient who, Guid ticketId, string content, Guid? clientId = null) =>
        await who.PostAsync(w.Url($"tickets/{ticketId}/messages"), new { content, clientMessageId = clientId ?? Guid.NewGuid() });

    public static async Task<HttpResponseMessage> AddNoteAsync(this TicketWorld w, ApiClient who, Guid ticketId, string content, Guid? clientId = null) =>
        await who.PostAsync(w.Url($"tickets/{ticketId}/notes"), new { content, clientNoteId = clientId ?? Guid.NewGuid() });

    public static async Task<List<MessageJson>> MessagesAsync(this TicketWorld w, ApiClient who, Guid ticketId, string query = "") =>
        (await who.ReadAsync<PagedDto<MessageJson>>(await who.GetAsync(w.Url($"tickets/{ticketId}/messages{query}")))).Items;

    public static async Task<List<NotificationJson>> NotificationsAsync(this TicketWorld w, ApiClient who, string query = "") =>
        (await who.ReadAsync<PagedDto<NotificationJson>>(await who.GetAsync(w.Url($"notifications{query}")))).Items;

    public static async Task<int> UnreadAsync(this TicketWorld w, ApiClient who) =>
        (await who.ReadAsync<UnreadJson>(await who.GetAsync(w.Url("notifications/unread-count")))).Count;

    public static Task<HttpResponseMessage> UploadAsync(this TicketWorld w, ApiClient who, Guid ticketId, string fileName, byte[] bytes, bool internalOnly = false)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "File", fileName);
        form.Add(new StringContent(internalOnly ? "true" : "false"), "Internal");
        return who.PostContentAsync(w.Url($"tickets/{ticketId}/attachments"), form);
    }

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 73, 72, 68, 82];
}

/// <summary>A real SignalR client over the in-memory test server (WebSockets, token in the query string like a browser).</summary>
public sealed class HubClient : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Name, JsonElement Payload)> _events = new();

    private HubClient(HubConnection connection) => Connection = connection;

    public HubConnection Connection { get; }

    public IReadOnlyCollection<(string Name, JsonElement Payload)> Events => _events.ToArray();

    public static HubClient Create(ApiFactory factory, string? accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/realtime"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                // like a browser: the WebSocket handshake carries the token in the query string, not in a header
                o.WebSocketFactory = (ctx, ct) => new ValueTask<System.Net.WebSockets.WebSocket>(factory.Server.CreateWebSocketClient()
                    .ConnectAsync(accessToken is null ? ctx.Uri : new Uri($"{ctx.Uri}&access_token={Uri.EscapeDataString(accessToken)}"), ct));
                if (accessToken is not null)
                {
                    o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                }
            })
            .Build();
        var client = new HubClient(connection);
        connection.On<string, JsonElement>("Receive", (name, payload) => client._events.Enqueue((name, payload.Clone())));
        return client;
    }

    public Task StartAsync() => Connection.StartAsync();

    public async Task<bool> WaitForAsync(Func<(string Name, JsonElement Payload), bool> match, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (_events.Any(match))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public static class HttpAssert
{
    public static async Task StatusAsync(HttpStatusCode expected, HttpResponseMessage response) =>
        Assert.True(response.StatusCode == expected, $"expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
}
