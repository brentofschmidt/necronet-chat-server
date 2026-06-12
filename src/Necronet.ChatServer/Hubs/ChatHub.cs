using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Necronet.Contracts;
using Necronet.ChatServer.Services;

namespace Necronet.ChatServer.Hubs;

/// <summary>
/// The browser edge. A client opens one authenticated WebSocket here,
/// then calls <see cref="JoinChannel"/> for each channel it wants to
/// watch. Joining adds the connection to the channel's SignalR group;
/// any message published to that group fans out to the connection.
///
/// The hub is the FAN-OUT edge, not (yet) the writer. Persisting a
/// message still goes through the web app's existing send RPC; getting
/// it onto the wire goes through the internal publish endpoint (see
/// Program.cs), which broadcasts to <see cref="ChannelGroups.ForChannel"/>.
/// Who calls that endpoint (web client post-send, a DB webhook, or the
/// game server) is a later decision — the hub doesn't care.
/// </summary>
[Authorize]
public sealed class ChatHub : Hub<IChatClient>
{
    private readonly IChannelMembershipService _membership;
    private readonly IChatMessageService _messages;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IChannelMembershipService membership,
        IChatMessageService messages,
        ILogger<ChatHub> logger)
    {
        _membership = membership;
        _messages = messages;
        _logger = logger;
    }

    /// <summary>The Supabase account id of the connected user (JWT sub claim).</summary>
    private Guid UserId => Guid.Parse(Context.UserIdentifier!);

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "Chat connection {ConnectionId} opened for user {UserId}",
            Context.ConnectionId, Context.UserIdentifier);
        return base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribe this connection to a channel. Rejected unless the caller
    /// is a member of the channel in the database — closing the realtime
    /// equivalent of the RLS gate.
    /// </summary>
    public async Task JoinChannel(Guid channelId)
    {
        if (!await _membership.IsMemberAsync(UserId, channelId, Context.ConnectionAborted))
        {
            throw new HubException("You are not a member of this channel.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroups.ForChannel(channelId));
        _logger.LogInformation(
            "User {UserId} joined channel {ChannelId}", UserId, channelId);
    }

    /// <summary>Unsubscribe this connection from a channel.</summary>
    public Task LeaveChannel(Guid channelId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroups.ForChannel(channelId));

    /// <summary>
    /// Post a message to a channel. The chat server is the writer: it
    /// persists the message (via <see cref="IChatMessageService"/>) on
    /// behalf of the connection's authenticated user, then fans the
    /// stored row out to the channel group — so the sender gets its own
    /// message back too and reconciles by client nonce.
    ///
    /// The sender is the JWT-validated <see cref="UserId"/>, never
    /// anything the client supplies, so a client can only post as
    /// itself. Business-rule refusals (muted / slowmode / locked / not
    /// a member) come back as a <see cref="HubException"/> the composer
    /// can show; the underlying RPC is the single source of those gates.
    /// </summary>
    public async Task SendMessage(SendMessageRequest request)
    {
        try
        {
            var message = await _messages.SendAsync(
                UserId, request, Context.ConnectionAborted);

            await Clients
                .Group(ChannelGroups.ForChannel(request.ChannelId))
                .ReceiveMessage(message);
        }
        catch (ChatMessageRejectedException ex)
        {
            // Expected business-rule refusal — surface the reason.
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            // Infrastructure fault — log it, but don't leak detail.
            _logger.LogError(ex,
                "SendMessage failed for user {UserId} channel {ChannelId}",
                UserId, request.ChannelId);
            throw new HubException("Could not send message. Please try again.");
        }
    }

    /// <summary>
    /// Subscribe this connection to its DM channel with <paramref
    /// name="peerId"/> and return that channel's id (or null if no DM
    /// has been created between them yet). The server resolves the
    /// channel from the connection's user + the peer, so the client
    /// stays peer-keyed — it never has to know or send a DM channel id.
    /// Only ever joins a channel the caller is a participant of.
    /// </summary>
    public async Task<Guid?> JoinDm(Guid peerId)
    {
        var channelId = await _messages.ResolveDmChannelAsync(
            UserId, peerId, Context.ConnectionAborted);
        if (channelId is Guid id)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId, ChannelGroups.ForChannel(id));
        }
        return channelId;
    }

    /// <summary>
    /// Send a 1:1 whisper/DM. The server persists it on the user's
    /// behalf (DM gates: whisper-privacy + mutual-friend), then returns
    /// the stored row to the caller AND fans it out to everyone ELSE in
    /// the DM channel's group. The sender reconciles its optimistic
    /// bubble (and learns the DM channel id, even for a brand-new
    /// conversation) from the return value, so there's no self-echo to
    /// dedupe; peers receive it via <c>ReceiveMessage</c>. The sender's
    /// connection is added to the group so it receives the peer's future
    /// replies. Refusals surface as a <see cref="HubException"/>.
    /// </summary>
    public async Task<ChatMessageDto> SendDm(SendDmRequest request)
    {
        try
        {
            var message = await _messages.SendDmAsync(
                UserId, request, Context.ConnectionAborted);

            var group = ChannelGroups.ForChannel(message.ChannelId);
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
            await Clients.OthersInGroup(group).ReceiveMessage(message);

            return message;
        }
        catch (ChatMessageRejectedException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SendDm failed for user {UserId} recipient {RecipientId}",
                UserId, request.RecipientId);
            throw new HubException("Could not send message. Please try again.");
        }
    }
}
