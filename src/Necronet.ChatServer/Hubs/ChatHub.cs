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
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IChannelMembershipService membership, ILogger<ChatHub> logger)
    {
        _membership = membership;
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
}
