namespace Necronet.ChatServer.Services;

/// <summary>
/// Authorises channel subscriptions. A connection may only join the
/// SignalR group for a channel it's actually a member of — otherwise a
/// client could subscribe to <c>channel:&lt;any-guid&gt;</c> and receive
/// messages from private channels it was never in. The web app's RLS
/// protects the REST/RPC surface; this is the equivalent gate for the
/// realtime surface.
/// </summary>
public interface IChannelMembershipService
{
    Task<bool> IsMemberAsync(Guid userId, Guid channelId, CancellationToken ct);
}
